// The physics stream (CRO-13, protocol.md section 9) on one bridge.
//
// PF: every new sim frame of the own aircraft is a candidate; 30 per second on average (sim time) are sent, on a
// fixed 1/30 s grid so the rate does not depend on the sim's frame rate (0.6.1; it was 22 Hz at 45 fps).
// Latest wins, nothing is ever replayed.
// Follower (PM): frames go into a playout buffer on the PF's sim time (100 ms behind, interpolated,
// extrapolated for at most 600 ms, then held). Each new own sim frame gets exactly one placement. After the
// first placement the three freezes go on; a watchdog sends a freeze again that reads off.
// The three control surfaces are not written while the PF is on the ground below 40 kt (0.6.1), so the follower's
// pilot can do the flight control check; they start at 40 kt or in the air and stay on at least 5 s.
//
// Safety (owner decision 2026-11-04, CLAUDE.md): freezes and placement ONLY while this side is the follower.
// Becoming PF releases at once, in the same call (GuardedStreamWriter.RoleChanged). If the PF is lost (or the
// server), the follower stays frozen where it is and the pilot sees a red line; only the pilot's yes releases
// it (TakeControlByPilot). Never released automatically in the air. Session end releases.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Logging;
using Crosscheck.Bridge.Sim;

namespace Crosscheck.Bridge.Follow;

public sealed record PhysicsOptions
{
    public PlayoutOptions Playout { get; init; } = new();
    /// <summary>Frames per second the PF sends on average (sim time); never more than one per sim frame.</summary>
    public double StreamHz { get; init; } = 30;
    /// <summary>The follower writes the control surfaces when the PF is in the air or at least this fast on the ground, knots.</summary>
    public double SurfaceOnGsKt { get; init; } = 40;
    /// <summary>Once the surfaces are written they stay on at least this long before the ground rule can stop them, seconds.</summary>
    public double SurfaceHoldS { get; init; } = 5;
    /// <summary>A freeze reading off is only sent again this long after the freezes went on, seconds.</summary>
    public double WatchdogGraceS { get; init; } = 1.0;
    /// <summary>stream_stats is logged this often while streaming or following, seconds (0 = never).</summary>
    public double StatsEveryS { get; init; } = 5;
    /// <summary>No frame for this long while following: "no stream" (amber), seconds.</summary>
    public double NoStreamAfterS { get; init; } = 2;
}

public enum FollowMode
{
    /// <summary>Not in a session (or role unknown): nothing is sent or written.</summary>
    Off,
    /// <summary>This side is PF: own frames are streamed. Never frozen or placed.</summary>
    Streaming,
    /// <summary>Follower, no usable frame yet: not frozen.</summary>
    Waiting,
    /// <summary>Follower, placed every sim frame, frozen.</summary>
    Following,
    /// <summary>Follower, the PF (or the server) is lost: frozen, holding the last position, waiting for the pilot's yes.</summary>
    PfLost,
    /// <summary>Follower whose pilot took the aircraft (the PF was lost): not frozen, nothing written until the role changes.</summary>
    Released,
}

/// <summary>Ground speed and ground flag of the own aircraft and of the last placed PF frame; SurfacesOn is the follower's surface rule.</summary>
public sealed record PhysicsReading(double? OwnGsKt, bool? OwnOnGround, double? PfGsKt, bool? PfOnGround, bool? SurfacesOn);

/// <summary>A thrust lever (profile stream var) that differs between this cockpit and the PF's stream.</summary>
public sealed record LeverDifference(string Var, double Pf, double Mine);

public sealed class PhysicsSync : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly IPhysicsLink _link;
    private readonly PhysicsOptions _options;
    private readonly BridgeLog _log;
    private readonly EventHub _hub;
    private readonly IReadOnlyDictionary<string, double> _leverTolerance;
    private readonly Func<string, bool> _leverWritable;
    private readonly Func<string, double, Task> _writeLever;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Timer? _statsTimer;

    private FollowMode _mode = FollowMode.Off;
    private Action<long, JsonObject>? _send;
    private long _frameNo;
    /// <summary>The sim time the next stream frame is due (a fixed grid, so a frame rate that is not a multiple of 30 loses nothing).</summary>
    private double _nextSendT = double.NegativeInfinity;
    private bool _surfacesDecided;
    private bool _surfacesOn;
    private double _surfacesOnSince;
    private PlayoutBuffer _buffer;
    private PlacementState? _placement;
    private double _engagedAtS;
    private OwnFrame? _own;
    private double _ownAtS = double.NaN;
    private PhysicsFrame? _lastPlaced;
    private PlayoutStatus _lastStatus = PlayoutStatus.Empty;
    private double _lastLateBy;
    private double _lastArrivalS = double.NaN;
    private FreezeFlags _flags;
    private bool _flagsKnown;
    private string? _pfLostCause;
    private Dictionary<string, double> _pfLevers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _leverWritten = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refusedVars = new(StringComparer.Ordinal);
    private Stats _stats = new();

    private sealed class Stats
    {
        public long Sent, Received, DroppedOld, Bad, Ignored, Placed, Interpolated, Extrapolated, Holding, Waiting, Reasserts;
        public double MaxGapMs;
        /// <summary>Ground speed (kt) and ground flag of the last placement: the PF's frame and this sim's own aircraft (CRO-41).</summary>
        public double PfGs, OwnGs, GsDiffMax;
        public bool PfOnGround, OwnOnGround;
        public readonly List<double> LatenessMs = [];
    }

    public PhysicsSync(IPhysicsLink link, BridgeLog log, EventHub hub, PhysicsOptions? options = null,
        IReadOnlyDictionary<string, double>? leverTolerance = null, Func<string, bool>? leverWritable = null,
        Func<string, double, Task>? writeLever = null)
    {
        _link = link;
        _log = log;
        _hub = hub;
        _options = options ?? new PhysicsOptions();
        _leverTolerance = leverTolerance ?? new Dictionary<string, double>();
        _leverWritable = leverWritable ?? (_ => false);
        _writeLever = writeLever ?? ((_, _) => Task.CompletedTask);
        _buffer = new PlayoutBuffer(_options.Playout);
        _link.FrameRead += OnOwnFrame;
        _link.FlagsRead += OnFlags;
        _link.WriteRefused += OnWriteRefused;
        _link.SetFollower(false);
        if (_options.StatsEveryS > 0)
            _statsTimer = new Timer(_ => LogStats(), null, TimeSpan.FromSeconds(_options.StatsEveryS), TimeSpan.FromSeconds(_options.StatsEveryS));
    }

    public IPhysicsLink Link => _link;

    /// <summary>Estimated round trip to the server (for the status bar), set by the runtime.</summary>
    public Func<double?> Rtt { get; set; } = () => null;

    public FollowMode Mode
    {
        get { lock (_gate) return _mode; }
    }

    /// <summary>The freezes Crosscheck switched on are on (this side holds or places the aircraft).</summary>
    public bool Frozen
    {
        get { lock (_gate) return _placement is { IsEngaged: true }; }
    }

    /// <summary>The aircraft is in the air: the last placed frame (follower) or the own aircraft.</summary>
    public bool InAir
    {
        get
        {
            lock (_gate)
            {
                if (_placement is { IsEngaged: true } && _lastPlaced is { } p) return !p.OnGround;
                return _own is { } o && !o.Frame.OnGround;
            }
        }
    }

    /// <summary>The sim reports a freeze on that Crosscheck does not hold right now (e.g. after a crashed bridge).</summary>
    public bool FrozenOutside
    {
        get { lock (_gate) return _flagsKnown && _flags.AnyOn && _placement is not { IsEngaged: true }; }
    }

    private long _totalSent;
    private long _totalPlaced;

    /// <summary>Stream frames sent by this side since the bridge started.</summary>
    public long FramesSent => Interlocked.Read(ref _totalSent);

    /// <summary>Placements written by this side since the bridge started.</summary>
    public long FramesPlaced => Interlocked.Read(ref _totalPlaced);

    // ---------------------------------------------------------------- session wiring (runtime, engine)

    /// <summary>A session starts: <paramref name="send"/> sends one stream frame (frame number, data).</summary>
    public void StartSession(Action<long, JsonObject> send)
    {
        lock (_gate) _send = send;
    }

    /// <summary>The session is over (left, ended, bridge stopping): every freeze Crosscheck holds is released.</summary>
    public void EndSession(string cause)
    {
        lock (_gate)
        {
            _send = null;
            ReleaseLocked(cause);
            _link.SetFollower(false);
            SetMode(FollowMode.Off, cause);
        }
    }

    /// <summary>
    /// Role from the server (control token). PF: release at once and stream. Follower: follow the PF's frames.
    /// Same role again (resume) changes nothing. A pilot who took the aircraft (Released) stays released
    /// until the role really changes.
    /// </summary>
    public void SetRole(bool isPf)
    {
        lock (_gate)
        {
            if (isPf)
            {
                // Never frozen or placed on the PF: the gate first, then the release.
                _link.SetFollower(false);
                if (_mode == FollowMode.Streaming) return;
                ReleaseLocked("became_pf");
                _nextSendT = double.NegativeInfinity;
                SetMode(FollowMode.Streaming, "became_pf");
                return;
            }
            if (_mode is FollowMode.Waiting or FollowMode.Following or FollowMode.PfLost or FollowMode.Released) return;
            StartFollowingLocked("became_pm");
        }
    }

    /// <summary>A different PF (control changed hands): the buffer starts fresh on the new PF's clock.</summary>
    public void OnNewPf()
    {
        lock (_gate)
            if (_mode is FollowMode.Waiting or FollowMode.Following) _buffer = new PlayoutBuffer(_options.Playout);
    }

    /// <summary>The other pilot's connection state (peer_status).</summary>
    public void OnPeerState(string state)
    {
        lock (_gate)
        {
            if (state is "lost" or "left") PfLostLocked("peer_" + state);
        }
    }

    /// <summary>This bridge's connection to the server: lost while following = the PF is lost from here.</summary>
    public void OnServerConnected(bool connected)
    {
        lock (_gate)
            if (!connected) PfLostLocked("server");
    }

    /// <summary>One stream message (network thread).</summary>
    public void OnStream(long frame, JsonObject data)
    {
        PhysicsFrame f;
        Dictionary<string, double> levers;
        if (!StreamFields.TryFromWire(data, out f, out levers))
        {
            lock (_gate) _stats.Bad++;
            return;
        }
        string? resumed = null;
        lock (_gate)
        {
            if (_mode is not (FollowMode.Waiting or FollowMode.Following or FollowMode.PfLost) || _own is null)
            {
                _stats.Ignored++;
                return;
            }
            var nowS = _sw.Elapsed.TotalSeconds;
            if (!double.IsNaN(_lastArrivalS)) _stats.MaxGapMs = Math.Max(_stats.MaxGapMs, (nowS - _lastArrivalS) * 1000);
            _lastArrivalS = nowS;
            if (_mode == FollowMode.PfLost)
            {
                // The PF is back: follow again (not a release; the aircraft stays frozen until placed).
                _buffer = new PlayoutBuffer(_options.Playout);
                _pfLostCause = null;
                SetMode(_placement is { IsEngaged: true } ? FollowMode.Following : FollowMode.Waiting, "pf_back");
                resumed = "Stream from the pilot flying is back. Following again.";
            }
            // A PF that started again (new clock) is far behind the newest frame: start over.
            if (_buffer.Newest is { } newest && f.T < newest.T - 2) _buffer = new PlayoutBuffer(_options.Playout);
            // Arrival on the follower's clock: its own sim time, moved on by the stopwatch since that frame.
            var arrival = _own.Frame.T + (nowS - _ownAtS);
            _stats.Received++;
            if (!_buffer.Push(f, arrival)) _stats.DroppedOld++;
            else _stats.LatenessMs.Add(_buffer.LastLateness * 1000);
            _pfLevers = levers;
        }
        if (resumed is not null) _hub.Publish("follow_resumed", resumed);
    }

    // ---------------------------------------------------------------- the pilot's decisions

    /// <summary>
    /// The pilot's yes on "PF lost – take control?" while the server cannot be reached: releases the freezes
    /// here and now, and this pilot flies on, unsynced (owner decision 2026-11-04). Only from the pilot.
    /// </summary>
    public bool TakeAircraftByPilot()
    {
        lock (_gate)
        {
            if (_mode != FollowMode.PfLost) return false;
            ReleaseLocked("pilot_took_control");
            _link.SetFollower(false);
            SetMode(FollowMode.Released, "pilot_took_control");
        }
        _hub.Publish("aircraft_released", "You have the aircraft. You are flying unsynced until the session has a pilot flying again.");
        return true;
    }

    /// <summary>
    /// "Release aircraft" (pilot action): switches all three freezes off, also ones Crosscheck does not hold
    /// any more (a crashed bridge). Refused while following a connected PF (use "I have control").
    /// </summary>
    public ApiResult ReleaseAircraft()
    {
        lock (_gate)
        {
            if (_mode is FollowMode.Following or FollowMode.Waiting)
                return ApiResult.Fail("You are following the pilot flying. Take control with \"I have control\" instead.");
            ReleaseLocked("release_aircraft");
            foreach (var ev in FollowerSafety.Events)
            {
                try { _link.SendFreeze(ev, false); }
                catch (Exception e) { _log.Event("freeze_release_failed", new JsonObject { ["event"] = ev, ["why"] = e.Message }); }
            }
            if (_mode == FollowMode.PfLost)
            {
                _link.SetFollower(false);
                SetMode(FollowMode.Released, "release_aircraft");
            }
        }
        _log.Event("release_aircraft", new JsonObject());
        _hub.Publish("aircraft_released", "Aircraft released: the sim's freezes are off.");
        return ApiResult.Success;
    }

    /// <summary>What the own aircraft and the last placed PF frame read, for the diagnosis log (read-only).</summary>
    public PhysicsReading Reading()
    {
        lock (_gate)
        {
            var following = _mode is FollowMode.Following or FollowMode.PfLost;
            return new PhysicsReading(
                _own?.Frame.GroundSpeedKt, _own?.Frame.OnGround,
                following ? _lastPlaced?.GroundSpeedKt : null, following ? _lastPlaced?.OnGround : null,
                following && _surfacesDecided ? _surfacesOn : null);
        }
    }

    /// <summary>Thrust levers that differ from the PF's stream by more than the profile's tolerance (follower only).</summary>
    public IReadOnlyList<LeverDifference> LeverDifferences()
    {
        lock (_gate) return LeverDifferencesLocked();
    }

    /// <summary>What the status bar and the red line show.</summary>
    public JsonObject StatusJson()
    {
        lock (_gate)
        {
            var nowS = _sw.Elapsed.TotalSeconds;
            var health = _mode switch
            {
                FollowMode.Following or FollowMode.Waiting => _lastStatus switch
                {
                    PlayoutStatus.Interpolated => "ok",
                    PlayoutStatus.Extrapolated => "late",
                    PlayoutStatus.Holding => "holding",
                    _ => "waiting",
                },
                FollowMode.PfLost => "holding",
                _ => null,
            };
            var noStream = _mode is FollowMode.Waiting or FollowMode.Following
                && (double.IsNaN(_lastArrivalS) ? false : nowS - _lastArrivalS > _options.NoStreamAfterS);
            var levers = new JsonArray();
            foreach (var d in LeverDifferencesLocked())
                levers.Add(new JsonObject { ["var"] = d.Var, ["pf"] = d.Pf, ["mine"] = d.Mine });
            return new JsonObject
            {
                ["mode"] = ModeWord(_mode),
                ["link"] = _link.Connected,
                ["frozen"] = _placement is { IsEngaged: true },
                ["frozen_outside"] = _flagsKnown && _flags.AnyOn && _placement is not { IsEngaged: true },
                ["in_air"] = InAirLocked(),
                ["pf_lost_cause"] = _pfLostCause,
                ["health"] = health,
                ["buffer_ms"] = _options.Playout.BufferMs,
                ["late_ms"] = Math.Round(_lastLateBy * 1000),
                ["latency_ms"] = Rtt() is { } rtt ? Math.Round(rtt) : null,
                ["no_stream"] = noStream,
                ["levers"] = levers,
                ["refused"] = new JsonArray(_refusedVars.Order().Select(v => (JsonNode)v).ToArray()),
            };
        }
    }

    public static string ModeWord(FollowMode m) => m switch
    {
        FollowMode.Streaming => "streaming",
        FollowMode.Waiting => "waiting",
        FollowMode.Following => "following",
        FollowMode.PfLost => "pf_lost",
        FollowMode.Released => "released",
        _ => "off",
    };

    // ---------------------------------------------------------------- sim frames (link thread)

    private void OnOwnFrame(OwnFrame own)
    {
        (long No, JsonObject Data)? send = null;
        Action<long, JsonObject>? sender = null;
        List<(string Var, double Value)>? leverWrites = null;
        lock (_gate)
        {
            _own = own;
            _ownAtS = _sw.Elapsed.TotalSeconds;
            switch (_mode)
            {
                case FollowMode.Streaming when _send is not null:
                    if (own.Frame.T >= _nextSendT - 1e-4)
                    {
                        // Step a fixed grid; a clock that stalled or jumped starts a new one.
                        var period = 1 / _options.StreamHz;
                        _nextSendT = own.Frame.T - _nextSendT > period ? own.Frame.T + period : _nextSendT + period;
                        send = (++_frameNo, StreamFields.ToWire(own.Frame, own.Lvars));
                        sender = _send;
                    }
                    break;
                case FollowMode.Waiting or FollowMode.Following:
                    leverWrites = PlaceLocked(own.Frame.T);
                    break;
            }
        }
        if (send is { } s && sender is not null)
        {
            Interlocked.Increment(ref _totalSent);
            lock (_gate) _stats.Sent++;
            sender(s.No, s.Data);
        }
        foreach (var (v, value) in leverWrites ?? []) _ = WriteLeverAsync(v, value);
    }

    private List<(string, double)>? PlaceLocked(double now)
    {
        var r = _buffer.Sample(now);
        if (r.Status == PlayoutStatus.Empty) return null;
        _lastStatus = r.Status;
        _lastLateBy = r.LateBy;
        switch (r.Status)
        {
            case PlayoutStatus.Interpolated: _stats.Interpolated++; break;
            case PlayoutStatus.Extrapolated: _stats.Extrapolated++; break;
            case PlayoutStatus.Holding: _stats.Holding++; break;
            case PlayoutStatus.Waiting: _stats.Waiting++; break;
        }
        try
        {
            var surfaces = UpdateSurfacesLocked(r.State, now);
            _link.Place(r.State, surfaces);
            _lastPlaced = r.State;
            _stats.Placed++;
            _totalPlaced++;
            if (_own is { } own)
            {
                _stats.PfGs = r.State.GroundSpeedKt;
                _stats.OwnGs = own.Frame.GroundSpeedKt;
                _stats.PfOnGround = r.State.OnGround;
                _stats.OwnOnGround = own.Frame.OnGround;
                _stats.GsDiffMax = Math.Max(_stats.GsDiffMax, Math.Abs(_stats.PfGs - _stats.OwnGs));
            }
            if (_placement is not { IsEngaged: true })
            {
                // Freeze after the first placement, so the aircraft is frozen where the PF is.
                _placement ??= NewPlacement();
                foreach (var ev in FollowerSafety.Events) _placement.Engage(ev);
                _engagedAtS = _sw.Elapsed.TotalSeconds;
                _log.Event("freeze_engaged", new JsonObject { ["in_air"] = !r.State.OnGround });
                SetMode(FollowMode.Following, "first_frame");
            }
        }
        catch (Exception e) when (e is SafetyViolationException or InvalidOperationException or AggregateException)
        {
            _log.Event("place_failed", new JsonObject { ["why"] = e.Message });
            return null;
        }
        // Thrust levers: only once a range is confirmed in the profile (owner decision 2026-11-04: measure first).
        List<(string, double)>? writes = null;
        foreach (var (name, _) in _leverTolerance)
        {
            if (!_leverWritable(name) || !_pfLevers.TryGetValue(name.ToLowerInvariant(), out var pf)) continue;
            if (_leverWritten.TryGetValue(name, out var last) && Math.Abs(last - pf) < 1e-9) continue;
            _leverWritten[name] = pf;
            (writes ??= []).Add((name, pf));
        }
        return writes;
    }

    /// <summary>
    /// Whether this placement writes elevator, aileron and rudder (0.6.1): not while the PF is on the ground below
    /// <see cref="PhysicsOptions.SurfaceOnGsKt"/>, so the follower's pilot can do the flight control check. Starts at that
    /// speed or in the air; once on, stays on at least <see cref="PhysicsOptions.SurfaceHoldS"/> (no flicker around 40 kt).
    /// Position, attitude and the freezes are not affected. Logs follow_surfaces on every change.
    /// </summary>
    private bool UpdateSurfacesLocked(PhysicsFrame pf, double now)
    {
        var want = !pf.OnGround || pf.GroundSpeedKt >= _options.SurfaceOnGsKt;
        string? cause = null;
        if (!_surfacesDecided)
        {
            _surfacesDecided = true;
            _surfacesOn = want;
            _surfacesOnSince = now;
            cause = "engage";
        }
        else if (want && !_surfacesOn)
        {
            _surfacesOn = true;
            _surfacesOnSince = now;
            cause = pf.OnGround ? "speed" : "airborne";
        }
        else if (!want && _surfacesOn && now - _surfacesOnSince >= _options.SurfaceHoldS)
        {
            _surfacesOn = false;
            cause = "ground_slow";
        }
        if (cause is not null)
            _log.Event("follow_surfaces", new JsonObject
            {
                ["on"] = _surfacesOn,
                ["cause"] = cause,
                ["pf_gs_kt"] = Math.Round(pf.GroundSpeedKt, 1),
                ["on_ground"] = pf.OnGround,
            });
        return _surfacesOn;
    }

    private async Task WriteLeverAsync(string name, double value)
    {
        try
        {
            await _writeLever(name, value);
        }
        catch (Exception e) when (e is InvalidOperationException)
        {
            _log.Event("lever_write_refused", new JsonObject { ["var"] = name, ["value"] = value, ["why"] = e.Message });
        }
    }

    private void OnFlags(FreezeFlags flags)
    {
        List<string>? reasserted = null;
        lock (_gate)
        {
            _flags = flags;
            _flagsKnown = true;
            if (_placement is not { IsEngaged: true } placement || _sw.Elapsed.TotalSeconds - _engagedAtS < _options.WatchdogGraceS) return;
            if (!_link.IsFollower) return;
            foreach (var ev in placement.Engaged)
            {
                if (flags.IsOn(ev)) continue;
                try
                {
                    if (placement.Reassert(ev))
                    {
                        _stats.Reasserts++;
                        (reasserted ??= []).Add(ev);
                    }
                }
                catch (SafetyViolationException e)
                {
                    _log.Event("freeze_reassert_refused", new JsonObject { ["event"] = ev, ["why"] = e.Message });
                }
            }
        }
        foreach (var ev in reasserted ?? []) _log.Event("freeze_reasserted", new JsonObject { ["event"] = ev });
    }

    private void OnWriteRefused(string var, string why)
    {
        bool first;
        lock (_gate) first = _refusedVars.Add(var);
        _log.Event("placement_refused", new JsonObject { ["var"] = var, ["why"] = why, ["first"] = first });
        if (first) _hub.Publish("placement_refused", $"The sim refused {var.ToLowerInvariant()} on the follower; it is no longer written.", new JsonObject { ["var"] = var });
    }

    // ---------------------------------------------------------------- state changes (under _gate)

    private PlacementState NewPlacement() => new((ev, data) => _link.SendFreeze(ev, data == 1));

    private void StartFollowingLocked(string cause)
    {
        _buffer = new PlayoutBuffer(_options.Playout);
        _pfLostCause = null;
        _lastStatus = PlayoutStatus.Empty;
        _lastArrivalS = double.NaN;
        _leverWritten.Clear();
        _surfacesDecided = false;
        _placement = NewPlacement();
        _link.SetFollower(true);
        SetMode(FollowMode.Waiting, cause);
    }

    private void PfLostLocked(string cause)
    {
        if (_mode is not (FollowMode.Following or FollowMode.Waiting)) return;
        _pfLostCause = cause.StartsWith("peer_", StringComparison.Ordinal) ? "peer" : "server";
        if (_placement is not { IsEngaged: true })
        {
            // Not frozen yet: nothing to hold. Wait for the PF again.
            return;
        }
        // Hold: the freezes stay on, nothing more is placed. Released only by the pilot's yes.
        SetMode(FollowMode.PfLost, cause);
        var inAir = InAirLocked();
        _log.Event("pf_lost_hold", new JsonObject { ["cause"] = cause, ["in_air"] = inAir });
        var text = _pfLostCause == "server"
            ? "Connection to the server lost. Your aircraft is held where it is. Take control?"
            : "The pilot flying is lost. Your aircraft is held where it is. Take control?";
        Task.Run(() => _hub.Publish("pf_lost", text, new JsonObject { ["cause"] = cause, ["in_air"] = inAir }));
    }

    private void ReleaseLocked(string cause)
    {
        if (_placement is null) return;
        var inAir = InAirLocked();
        IReadOnlyList<string> undone;
        try
        {
            undone = _placement.Undo();
        }
        catch (AggregateException e)
        {
            _log.Event("freeze_release_failed", new JsonObject { ["cause"] = cause, ["why"] = e.InnerException?.Message });
            _hub.Publish("freeze_release_failed", "Crosscheck could not switch the sim's freezes off. Use \"Release aircraft\" or run crosscheck restore.");
            undone = [];
        }
        _placement = null;
        _surfacesDecided = false;
        if (undone.Count > 0) _log.Event("freeze_released", new JsonObject { ["cause"] = cause, ["in_air"] = inAir, ["events"] = undone.Count });
    }

    private bool InAirLocked()
    {
        if (_placement is { IsEngaged: true } && _lastPlaced is { } p) return !p.OnGround;
        return _own is { } o && !o.Frame.OnGround;
    }

    private IReadOnlyList<LeverDifference> LeverDifferencesLocked()
    {
        if (_mode is not (FollowMode.Following or FollowMode.PfLost or FollowMode.Waiting) || _own is null) return [];
        var list = new List<LeverDifference>();
        foreach (var (name, tol) in _leverTolerance)
        {
            if (!_pfLevers.TryGetValue(name.ToLowerInvariant(), out var pf) || !_own.Lvars.TryGetValue(name, out var mine)) continue;
            if (Math.Abs(pf - mine) > tol) list.Add(new LeverDifference(name, pf, mine));
        }
        return list;
    }

    private void SetMode(FollowMode mode, string cause)
    {
        if (_mode == mode) return;
        var from = _mode;
        _mode = mode;
        _log.Event("follow_mode", new JsonObject { ["from"] = ModeWord(from), ["to"] = ModeWord(mode), ["cause"] = cause });
    }

    // ---------------------------------------------------------------- CRO-18 timing log

    /// <summary>One stream_stats line: what the stream did since the last one. Also called by tests.</summary>
    public JsonObject? LogStats()
    {
        Stats s;
        FollowMode mode;
        lock (_gate)
        {
            mode = _mode;
            s = _stats;
            _stats = new Stats();
        }
        if (mode == FollowMode.Off) return null;
        var late = s.LatenessMs.Order().ToList();
        double P(double q) => late.Count == 0 ? 0 : Math.Round(late[Math.Min(late.Count - 1, (int)(q * late.Count))], 1);
        var o = new JsonObject
        {
            ["mode"] = ModeWord(mode),
            ["sent"] = s.Sent,
            ["received"] = s.Received,
            ["dropped_old"] = s.DroppedOld,
            ["bad"] = s.Bad,
            ["ignored"] = s.Ignored,
            ["placed"] = s.Placed,
            ["interpolated"] = s.Interpolated,
            ["extrapolated"] = s.Extrapolated,
            ["holding"] = s.Holding,
            ["waiting"] = s.Waiting,
            ["late_p50_ms"] = P(0.5),
            ["late_p95_ms"] = P(0.95),
            ["late_max_ms"] = late.Count == 0 ? 0 : Math.Round(late[^1], 1),
            ["max_gap_ms"] = Math.Round(s.MaxGapMs),
            ["freeze_reasserts"] = s.Reasserts,
            ["rtt_ms"] = Rtt() is { } rtt ? Math.Round(rtt) : null,
        };
        if (s.Placed > 0)
        {
            // CRO-41: the PF's ground speed in the last placement against this sim's own aircraft.
            o["pf_gs_kt"] = Math.Round(s.PfGs, 1);
            o["own_gs_kt"] = Math.Round(s.OwnGs, 1);
            o["gs_diff_max_kt"] = Math.Round(s.GsDiffMax, 1);
            o["pf_on_ground"] = s.PfOnGround;
            o["own_on_ground"] = s.OwnOnGround;
        }
        _log.Event("stream_stats", o);
        return o;
    }

    public async ValueTask DisposeAsync()
    {
        _statsTimer?.Dispose();
        _link.FrameRead -= OnOwnFrame;
        _link.FlagsRead -= OnFlags;
        _link.WriteRefused -= OnWriteRefused;
        EndSession("bridge_stopped");
        await _link.DisposeAsync();
    }
}
