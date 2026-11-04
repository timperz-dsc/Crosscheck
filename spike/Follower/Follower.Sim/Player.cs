// Plays a recording into this sim as the follower (CRO-17 spike; offline, own sim only).
// Every sim frame: read the follower back, step the PlaybackSession (network simulation,
// playout buffer, metrics; all in Follower.Core), write the follower's position, attitude and
// velocities. Method B also switches the sim's freezes on, method C slew. Whatever ends the
// run (normal end, handover test, stop key, Ctrl+C, a crash, process exit, the sim quitting),
// every freeze and slew this run switched on is switched off again through PlacementState.
//
// Timing (research section 6.8): the player's clock is the follower sim's own clock
// (SIMULATION TIME or ABSOLUTE TIME, the one the recording is timed with), so each sim frame
// moves the follower by exactly one frame's worth. When several state messages are handled in
// one go, only the newest is played and written.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Crosscheck.Follower;
using Microsoft.FlightSimulator.SimConnect;
using SC = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace Crosscheck.Follower.Sim;

sealed class Player
{
    /// <summary>Most messages handled in one go before the newest is played.</summary>
    private const int MaxDrain = 32;

    private readonly PlaybackSession _session;
    private readonly PlaybackSettings _settings;
    private readonly string _outRoot;
    private readonly string _recordingName;
    private readonly object _scLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<string> _notes = [];
    private readonly FrameClock _frameClock;
    private readonly List<ExtraWrite> _extras = [];
    private SC? _sc;
    private PlacementState? _placement;
    private readonly Stopwatch _clock = new();
    private bool _engaged;
    private bool _finished;
    private bool _quit;
    private Frame _lastActual;
    private bool _haveActual;
    private FlagsData _flags;
    private double _flagsAt = double.NaN;
    // Messages handled in this go: only the newest state is played.
    private bool _gotMessage;
    private StateData? _pending;
    private double _pendingAt;
    private int _pendingCount;
    private int _coalesced;
    private double _lastPlayedWall = double.NaN;
    private int _exceptions;
    // Handover test: the log after the release, and why the run ended.
    private HandoverLog? _handover;
    private string? _handoverEnd;

    /// <summary>An extra var of the mixed set. Stops being written the first time the sim refuses it.</summary>
    private sealed class ExtraWrite(Def def, string var, Func<Frame, double> value)
    {
        private readonly Queue<uint> _order = new();
        private readonly HashSet<uint> _ids = [];
        public Def Def { get; } = def;
        public string Var { get; } = var;
        public Func<Frame, double> Value { get; } = value;
        public bool Enabled { get; set; } = true;
        public int Written { get; private set; }
        public string? Refused { get; set; }

        public void Sent(uint id)
        {
            Written++;
            if (_ids.Add(id)) _order.Enqueue(id);
            while (_order.Count > 512) _ids.Remove(_order.Dequeue());
        }

        public bool Owns(uint sendId) => _ids.Contains(sendId);
    }

    public Player(PlaybackSession session, string outRoot, string recordingName)
    {
        _session = session;
        _settings = session.Settings;
        _outRoot = outRoot;
        _recordingName = recordingName;
        _frameClock = new FrameClock(session.Clock.Source);
        if (_settings.Velocities == VelocityMode.Mixed && _settings.Method != Method.C)
        {
            foreach (var (def, var, _) in SimLink.Extras)
            {
                if (def == Def.PlaceGForce && !session.Recording.Has("g_force"))
                {
                    _notes.Add("G FORCE not written: the recording has no g_force (recorded before 2026-11-04)");
                    continue;
                }
                _extras.Add(new ExtraWrite(def, var, def == Def.PlaceGForce ? f => f.GForce : f => f.VelWorldY));
            }
        }
    }

    public int Run(StopSignal stop)
    {
        var started = DateTime.UtcNow;
        Console.WriteLine("connecting to the sim ...");
        while (_sc is null && !stop.IsSet)
        {
            _sc = SimLink.TryConnect("Crosscheck Follower Player (spike, offline only)", _wake);
            if (_sc is null) stop.Wait(TimeSpan.FromSeconds(2));
        }
        if (_sc is null) return 1;

        _placement = new PlacementState((ev, data) =>
        {
            lock (_scLock)
                if (_sc is { } sc) SimLink.Send(sc, ev, data);
        });
        // Last line of defence: a crash, a closed console window or a shutdown still undoes freeze and slew.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyRelease("process exit");
        AppDomain.CurrentDomain.UnhandledException += (_, _) => EmergencyRelease("unhandled exception");
        using var closed = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => EmergencyRelease("console window closed"));
        using var shutdown = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => EmergencyRelease("shutdown"));

        string ended;
        try
        {
            lock (_scLock)
            {
                _sc.OnRecvQuit += (_, _) => _quit = true;
                _sc.OnRecvException += OnException;
                _sc.OnRecvSimobjectData += OnData;
                SimLink.MapAllowedEvents(_sc);
                SimLink.DefinePlacement(_sc);
                SimLink.RequestFlags(_sc);
                SimLink.RequestState(_sc);
            }
            Console.WriteLine($"playing: method {_settings.Method}, {_settings.RateHz:0} Hz, {_settings.Network.LatencyMs:0} ± {_settings.Network.JitterMs:0} ms, buffer {_settings.Playout.BufferMs:0} ms, clock {_session.Clock.Source}, velocities {_settings.Velocities}. Press S or Esc (or Ctrl+C) to stop.");
            while (!stop.IsSet && !_quit && !_finished)
            {
                _wake.WaitOne(10);
                lock (_scLock)
                {
                    // Handle everything that is waiting, then play only the newest state.
                    var n = 0;
                    do
                    {
                        _gotMessage = false;
                        _sc.ReceiveMessage();
                    } while (_gotMessage && ++n < MaxDrain && !_quit);
                    PlayPending();
                }
            }
            ended = _handoverEnd ?? (_finished ? "normal end of the recording" : _quit ? "the sim closed" : $"stopped ({stop.Reason})");
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            ended = $"error: {e.Message}";
            Console.WriteLine($"\n{ended}");
        }
        finally
        {
            Release();
        }

        var inAir = _haveActual && !_lastActual.OnGround;
        if (_handover is null)
        {
            if (_finished && !inAir && _lastActual.GroundSpeedKt <= EndCheck.StoppedKt) ended += ", stopped on the ground";
            else if (_finished && inAir) ended += _settings.AllowAirEnd ? ", in the air (--allow-air-end)" : ", but the sim reports the follower in the air";
        }
        if (_placement is { Reasserts: > 0 } pl) _notes.Add($"freeze/slew sent again {pl.Reasserts} times because the sim reported it off");
        _notes.Add($"player clock: {(_frameClock.FallbackReason is { } fb ? fb : $"{_frameClock.Source} throughout")}; " +
                   $"{_frameClock.Repeats} repeated sim frames not played, {_frameClock.Jumps} clock jumps bridged, " +
                   $"{_coalesced} older messages skipped because a newer one was handled in the same go");
        foreach (var x in _extras)
            _notes.Add(x.Refused is { } why ? $"{x.Var}: the sim refused it ({why}); written {x.Written} times before" : $"{x.Var}: written {x.Written} times, not refused");
        if (_exceptions > 0) _notes.Add($"{_exceptions} SimConnect exceptions (see the console)");
        _notes.Add(_settings.Method switch
        {
            Method.A => "method A: position, attitude and velocities written every frame; no freeze",
            Method.B => "method B: as A, with the sim's freeze lat/lon, altitude and attitude on",
            _ => "method C: slew on; position and attitude written every frame",
        });

        var info = Report.Build(_session, "sim", _recordingName, started, ended, inAir, _placement?.Reasserts ?? 0, _notes, _handover?.Summarize());
        var dir = Report.Write(_outRoot, info, _session.Metrics.Rows, _handover);
        Console.WriteLine();
        Console.WriteLine(Report.Markdown(info));
        Console.WriteLine($"run folder: {dir}");
        if (inAir) Console.WriteLine("\n*** The aircraft is in the air and yours: fly it. ***");
        else if (_handover is not null) Console.WriteLine("\n*** Handover test done: the aircraft is yours and may still be rolling. Brake. ***");
        return 0;
    }

    private void OnException(SC sender, SIMCONNECT_RECV_EXCEPTION d)
    {
        _gotMessage = true;
        _exceptions++;
        var ex = (SIMCONNECT_EXCEPTION)d.dwException;
        foreach (var x in _extras)
        {
            if (!x.Enabled || !x.Owns(d.dwSendID)) continue;
            x.Enabled = false;
            x.Refused = ex.ToString();
            Console.WriteLine($"\nthe sim refused {x.Var} ({ex}); it is no longer written");
            return;
        }
        Console.WriteLine($"\nSimConnect exception {ex} (send {d.dwSendID}, index {d.dwIndex})");
    }

    private void OnData(SC sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        _gotMessage = true;
        if (data.dwRequestID == (uint)Req.Flags)
        {
            _flags = (FlagsData)data.dwData[0];
            _flagsAt = _clock.Elapsed.TotalSeconds;
            CheckFlags();
            return;
        }
        if (data.dwRequestID != (uint)Req.State) return;
        if (!_clock.IsRunning) _clock.Start();
        // Keep only the newest; PlayPending plays it once everything waiting has been handled.
        _pending = (StateData)data.dwData[0];
        _pendingAt = _clock.Elapsed.TotalSeconds;
        _pendingCount++;
    }

    private void PlayPending()
    {
        if (_pending is not { } state) return;
        if (_pendingCount > 1) _coalesced += _pendingCount - 1;
        var wall = _pendingAt;
        (_pending, _pendingCount) = (null, 0);
        if (_finished || _placement is not { } placement || _sc is not { } sc) return;

        var raw = SimLink.ToFrame(0, state);
        var simClock = _frameClock.Source switch
        {
            ClockSource.Sim => raw.SimTimeS,
            ClockSource.Abs => raw.AbsTimeS,
            _ => 0,
        };
        if (!_frameClock.Next(wall, simClock)) return; // the same sim frame again: nothing new to play
        var t = _frameClock.Now;
        var actual = raw with { T = t };
        var wallMs = double.IsNaN(_lastPlayedWall) ? double.NaN : (wall - _lastPlayedWall) * 1000;
        _lastPlayedWall = wall;
        _lastActual = actual;
        _haveActual = true;

        if (_handover is { } log)
        {
            // Released: only watch. Nothing is written any more.
            log.Add(t, actual);
            if (log.Done)
            {
                _handoverEnd = $"handover test: freezes released at {_settings.HandoverTestS:0.0} s while taxiing, then logged {log.Rows[^1].T:0.0} s";
                _finished = true;
            }
            return;
        }
        if (placement.IsUndone) return;

        var target = _session.OnSimFrame(t, actual, wallMs);
        if (_session.HandoverDue)
        {
            // As the stop key does: switch off everything this run switched on, then only watch.
            var undone = placement.Undo();
            _handover = new HandoverLog(_session.Recording.Frames, target.RenderTime, _settings.HandoverTestS ?? 0);
            _handover.Add(t, actual);
            Console.WriteLine($"\nhandover test: released {string.Join(", ", undone.Select(u => u.Event))} at {actual.GroundSpeedKt:0} kt; logging {HandoverTest.LogS:0} s. The aircraft is yours: be ready to brake.");
            return;
        }

        var slewing = _settings.Method == Method.C;
        SimLink.Place(sc, target.State, _settings.Velocities, slewing);
        if (!slewing)
            foreach (var x in _extras)
                if (x.Enabled) x.Sent(SimLink.PlaceExtra(sc, x.Def, x.Value(target.State)));
        if (!_engaged)
        {
            // Freeze or slew after the first placement, so the aircraft is frozen where the recording starts.
            _engaged = true;
            foreach (var ev in AllowedEvents.Engage(_settings.Method)) placement.Engage(ev);
        }
        if (_session.Finished) _finished = true;
        if (_session.Metrics.Rows.Count % 120 == 1)
            Console.Write($"\r{t,6:0} s  {target.Status,-12}  {actual.GroundSpeedKt,4:0} kt  {(actual.OnGround ? "ground" : "air   ")}   ");
    }

    private void CheckFlags()
    {
        if (_placement is not { IsUndone: false } placement || !_engaged || _clock.Elapsed.TotalSeconds < 1) return;
        foreach (var ev in placement.Engaged)
            if (SimLink.FlagFor(ev, _flags) < 0.5 && placement.Reassert(ev))
                Console.WriteLine($"\n{ev}: the sim reports it off; sent again");
    }

    /// <summary>Undo freeze and slew (if not done yet), then read the flags back for up to 1.5 s.</summary>
    private void Release()
    {
        if (_placement is null) return;
        try
        {
            var undone = _placement.Undo();
            if (undone.Count > 0) Console.WriteLine($"\nreleased: {string.Join(", ", undone.Select(u => u.Event))}");
            var asked = _clock.Elapsed.TotalSeconds;
            var deadline = Stopwatch.StartNew();
            while (_sc is not null && !_quit && deadline.Elapsed < TimeSpan.FromSeconds(1.5))
            {
                _wake.WaitOne(20);
                lock (_scLock) _sc.ReceiveMessage();
                if (_flagsAt > asked && AllOff()) break;
            }
            if (_sc is not null && !_quit)
            {
                var msg = AllOff() ? "sim reports freeze and slew off" : "WARNING: the sim still reports a freeze or slew on. Run `follower-sim restore`.";
                Console.WriteLine(msg);
                _notes.Add(msg);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"\nWARNING: release failed ({e.Message}). Run `follower-sim restore` now.");
            _notes.Add($"release failed: {e.Message}");
        }
        finally
        {
            lock (_scLock)
            {
                try { _sc?.Dispose(); } catch (COMException) { }
                _sc = null;
            }
        }
    }

    private bool AllOff() => _flags.LatLon < 0.5 && _flags.Altitude < 0.5 && _flags.Attitude < 0.5 && _flags.Slew < 0.5;

    /// <summary>From another thread (process exit, crash): send the undo at once, no waiting.</summary>
    private void EmergencyRelease(string why)
    {
        try
        {
            if (_placement?.Undo() is { Count: > 0 } undone)
                Console.Error.WriteLine($"{why}: released {string.Join(", ", undone.Select(u => u.Event))}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{why}: release failed ({e.Message}). Run `follower-sim restore`.");
        }
    }
}
