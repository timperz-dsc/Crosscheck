using System.Text.Json.Nodes;
using System.Threading.Channels;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Logging;
using Crosscheck.Bridge.Protocol;
using Crosscheck.Bridge.Sim;
using Crosscheck.Profiles.Model;
using Microsoft.Extensions.Logging;

namespace Crosscheck.Bridge.Sync;

public sealed record EngineOptions
{
    /// <summary>Own-write echo window on top of settle_ms.</summary>
    public int EchoWindowMs { get; init; } = 1000;
    /// <summary>Step 1 of verification: the own var must show the written value within this time (1–2 frames normally).</summary>
    public int StoredTimeoutMs { get; init; } = 250;
    /// <summary>settle_ms sent with commands whose var has no verify block.</summary>
    public int DefaultSettleMs { get; init; } = 100;
    /// <summary>Which seat this pilot sits in (whose takeover pushbutton is theirs). null = host is captain, guest is fo.</summary>
    public SeatSide? SeatSide { get; init; }
    /// <summary>Acks kept for answering re-sends. Pulse and delta ids are remembered beyond this (never applied twice).</summary>
    public int AckCacheSize { get; init; } = 5000;
    /// <summary>
    /// Safety release (CLAUDE.md, protocol.md §5): a push button Crosscheck pressed down for the other pilot is
    /// released after this long if their "up" never arrives. Below Fenix's 40 s takeover latch.
    /// </summary>
    public int SafetyReleaseMs { get; init; } = 30_000;
    /// <summary>A mirrored "up" is never written sooner than this after its "down" (shortest real click gap, observe-report.md).</summary>
    public int EdgeFloorMs { get; init; } = 50;
    /// <summary>A pilot monitoring's takeover press is dropped if control is not granted within this time.</summary>
    public int TakeoverGrantTimeoutMs { get; init; } = 5000;
    /// <summary>
    /// "Release it" (CRO-33): a stuck button must have rested unchanged at least this long (no pilot holds
    /// it). Same time as the server's stuck_pressed detection.
    /// </summary>
    public int StuckMs { get; init; } = 5000;
    /// <summary>Incoming edges for a button the local pilot is pressing wait at most this long for the local release.</summary>
    public int LocalHoldMaxMs { get; init; } = 10_000;
    /// <summary>The lamp of a push button is read once per burst, this long after its last edge (gate test 2 cause D).</summary>
    public int EffectSettleMs { get; init; } = 400;
    /// <summary>A deny-list press is reported at most once per this time per button.</summary>
    public int DeniedRepeatMs { get; init; } = 2000;
}

/// <summary>Cockpit seat: left (captain) or right (first officer). Not the same as the session seat host/guest.</summary>
public enum SeatSide
{
    Captain,
    Fo,
}

/// <summary>
/// The core of the bridge. Detects pilot actions per sync class and sends them as commands,
/// applies the other pilot's commands and the server's corrections with two-step
/// verification, answers snapshots, and never sends back the read-back of its own writes.
/// </summary>
public sealed class SyncEngine : IAsyncDisposable
{
    private static readonly HashSet<SyncClass> Absolute = [SyncClass.Verified, SyncClass.Analog, SyncClass.Owned];
    private static readonly HashSet<SyncClass> Watched = [SyncClass.Verified, SyncClass.Analog, SyncClass.Owned, SyncClass.Delta, SyncClass.Pulse, SyncClass.Observe];

    private readonly Profile _profile;
    private readonly ISimAdapter _sim;
    private readonly IFenixGraphQl _gql;
    private readonly IProtocolChannel _client;
    private readonly EventHub _hub;
    private readonly BridgeLog _log;
    private readonly BridgeStatus _status;
    private readonly ILogger _logger;
    private readonly EngineOptions _options;
    private readonly Dictionary<string, VarEntry> _vars;
    private readonly object _gate = new();
    private readonly EchoTracker _echo;
    private readonly Dictionary<string, double> _lastSeen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _baseline = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Var, bool Absolute, JsonObject Body)> _pendingOwn = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastSeqByVar = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonObject> _ackCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _ackOrder = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    /// <summary>Sender: time of the last edge per push button (gap_ms of the next one).</summary>
    private readonly Dictionary<string, double> _edgeAt = new(StringComparer.Ordinal);
    /// <summary>
    /// Receiver: push buttons Crosscheck itself pressed down in this sim for the other pilot. Only these may
    /// ever be released by Crosscheck (the pilot's "up", or the safety release). A local touch removes them.
    /// </summary>
    private readonly Dictionary<string, MirroredDown> _mirroredDown = new(StringComparer.Ordinal);
    /// <summary>
    /// Receiver: spring-loaded switches (verified with rest) Crosscheck itself moved away from rest in this sim.
    /// Only these may ever be returned to rest by Crosscheck (safety release). A local touch removes them.
    /// </summary>
    private readonly Dictionary<string, MirroredDown> _springHeld = new(StringComparer.Ordinal);
    /// <summary>safety_release messages not sent yet (the link was down); sent after resume.</summary>
    private readonly List<JsonObject> _pendingSafety = [];
    /// <summary>A pilot monitoring's takeover press, held back until control is granted (protocol.md §8).</summary>
    private readonly List<(VarEntry Spec, string Edge, int GapMs)> _takeoverHeld = [];
    private bool _takeoverRequested;
    private Timer? _takeoverTimer;
    /// <summary>Lamp of an edge button → the button: the lamp is the button's state (toggle snapshot item).</summary>
    private readonly Dictionary<string, string> _toggleLamps;
    /// <summary>
    /// Ids of pulse and delta commands already applied. Never evicted during the session: a duplicate
    /// can never press or click twice (the ack cache only keeps the last 5000).
    /// </summary>
    private readonly HashSet<string> _appliedEvents = new(StringComparer.Ordinal);
    /// <summary>Controls watched read-only because they are not synced yet (name → label).</summary>
    private readonly IReadOnlyDictionary<string, string> _unsynced;
    private readonly Dictionary<string, double> _unsyncedSentAt = new(StringComparer.Ordinal);
    /// <summary>Display vars of delta entries: compared under the encoder (an input), not as indicators.</summary>
    private readonly HashSet<string> _deltaDisplays;
    /// <summary>Own command id → what the pilot did, in words (for the ack in the recent list).</summary>
    private readonly Dictionary<string, string> _sentText = new(StringComparer.Ordinal);
    /// <summary>Deny-list buttons: when this pilot's last press was reported (one per DeniedRepeatMs).</summary>
    private readonly Dictionary<string, double> _deniedSentAt = new(StringComparer.Ordinal);
    /// <summary>Verified vars whose value was seen outside the allowed set: never offered for "Set both".</summary>
    private readonly HashSet<string> _seenOutside = new(StringComparer.Ordinal);
    /// <summary>Counter detector: last value and consecutive +1 steps, per source ("local:" / "remote:" + var).</summary>
    private readonly Dictionary<string, (double Last, int Steps)> _counterRun = new(StringComparer.Ordinal);
    /// <summary>Verified vars that behave like press counters: not synced, writes blocked.</summary>
    private readonly HashSet<string> _counterSuspect = new(StringComparer.Ordinal);
    /// <summary>Absolute vars whose write changed the value and landed in this sim this session ("Set both", CRO-40).</summary>
    private readonly HashSet<string> _writeConfirmed = new(StringComparer.Ordinal);
    /// <summary>Edge buttons the local pilot is pressing right now (down seen, up not yet): when it started.</summary>
    private readonly Dictionary<string, double> _localPress = new(StringComparer.Ordinal);
    /// <summary>Incoming edges held back while the local pilot presses the same button, in arrival order.</summary>
    private readonly Dictionary<string, List<ApplyJob>> _parked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Timer> _parkTimers = new(StringComparer.Ordinal);
    /// <summary>Receiver: the value this bridge last wrote to an edge button (judge the next edge against it).</summary>
    private readonly Dictionary<string, double> _lastEdgeWrite = new(StringComparer.Ordinal);
    /// <summary>When each watched var last changed (any source).</summary>
    private readonly Dictionary<string, double> _lastChangeAt = new(StringComparer.Ordinal);
    /// <summary>One lamp read per push button burst: the timer and the down edge it belongs to.</summary>
    private readonly Dictionary<string, (Timer Timer, string Id, string Lamp)> _effectTimers = new(StringComparer.Ordinal);
    private readonly IClock _clock;
    private readonly Channel<ApplyJob> _applyQueue = Channel.CreateUnbounded<ApplyJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private Task? _applyLoop;
    private Timer? _snapshotTimer;
    private long _appliedSeq;
    private long _commandCounter;
    private bool _sessionActive;
    private string? _pfClientId;
    private int _dropIncoming;

    public SyncEngine(Profile profile, ISimAdapter sim, IFenixGraphQl gql, IProtocolChannel client, EventHub hub,
        BridgeLog log, BridgeStatus status, ILogger logger, IClock? clock = null, EngineOptions? options = null,
        UnsyncedList? unsynced = null)
    {
        _unsynced = (unsynced ?? UnsyncedList.Empty).Labels;
        _deltaDisplays = profile.Vars.Where(v => v.Class == SyncClass.Delta && v.Display is not null).Select(v => v.Display!).ToHashSet(StringComparer.Ordinal);
        _profile = profile;
        _sim = sim;
        _gql = gql;
        _client = client;
        _hub = hub;
        _log = log;
        _status = status;
        _logger = logger;
        _options = options ?? new EngineOptions();
        _clock = clock ?? SystemClock.Instance;
        _echo = new EchoTracker(_clock);
        _vars = profile.Vars.ToDictionary(v => v.Name, StringComparer.Ordinal);
        _toggleLamps = profile.Vars.Where(IsEdge).Where(v => v.Verify?.Lvar is not null && _vars.ContainsKey(v.Verify.Lvar))
            .GroupBy(v => v.Verify!.Lvar!).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal);
        Labels = new Labels(profile);
    }

    // ---------------------------------------------------------------- counters (status, tests)

    public long CommandsSent { get; private set; }
    public long CommandsApplied { get; private set; }
    public long CorrectionsRefused { get; private set; }
    /// <summary>Acks for our own commands that came back ok from the other sim.</summary>
    public long AcksOk => Interlocked.Read(ref _acksOk);
    private long _acksOk;
    public long EchoSuppressed => _echo.Suppressed;
    public bool IsPf => _pfClientId is not null && _pfClientId == _client.ClientId;

    public JsonObject Counters() => new()
    {
        ["commands_sent"] = CommandsSent,
        ["commands_applied"] = CommandsApplied,
        ["echo_suppressed"] = EchoSuppressed,
        ["corrections_refused"] = CorrectionsRefused,
        ["acks_ok"] = AcksOk,
        ["applied_seq"] = Interlocked.Read(ref _appliedSeq),
    };

    /// <summary>Profile vars (never ignore/stream) and the read-only watch list of controls not synced yet.</summary>
    public SimPlan Plan => PlanFor(_profile, _unsynced.Keys);

    public static SimPlan PlanFor(Profile profile, IEnumerable<string>? unsynced = null)
    {
        var vars = profile.Vars.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in unsynced ?? []) if (!vars.Contains(n)) names.Add(n);
        foreach (var v in profile.Vars.Where(v => Watched.Contains(v.Class)))
        {
            names.Add(v.Name);
            if (v.Display is { } d && vars.Contains(d)) names.Add(d);
            if (v.Verify?.Lvar is { } e && vars.Contains(e)) names.Add(e);
            if (v.Powered is { } p && vars.Contains(p)) names.Add(p);
            if (v.CompareWhen is { } when)
                foreach (var c in (when.Same ?? []).Append(when.Powered).OfType<string>().Where(vars.Contains)) names.Add(c);
        }
        return new SimPlan(names.Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>GraphQL paths to poll: datarefs and verify sources.</summary>
    public IReadOnlyCollection<string> GraphQlPaths => GraphQlPathsFor(_profile);

    public static IReadOnlyCollection<string> GraphQlPathsFor(Profile profile) =>
        (profile.Datarefs ?? []).Where(d => d.Class != SyncClass.Ignore).Select(d => d.Path)
            .Concat(profile.Vars.Select(v => v.Verify?.Graphql).OfType<string>())
            .Distinct().ToList();

    // ---------------------------------------------------------------- what the UI shows

    /// <summary>When the other pilot was lost (null while connected).</summary>
    public DateTimeOffset? PeerLostAt { get; private set; }
    /// <summary>Last time this side sent its hashes while the other pilot was connected.</summary>
    public DateTimeOffset? LastComparedAt { get; private set; }
    /// <summary>The last control_changed in which this side became PF (open differences as information).</summary>
    public JsonObject? ControlNotice { get; private set; }
    public DateTimeOffset? ControlNoticeAt { get; private set; }
    /// <summary>Peer name from joined / peer_status.</summary>
    public string? PeerName { get; private set; }
    /// <summary>Groups this side reports (have at least one value).</summary>
    public IReadOnlyList<string> ActiveGroups => Groups.ToList();

    private readonly Dictionary<string, JsonArray> _drift = new(StringComparer.Ordinal);
    /// <summary>Owned drift (absolute) in the first report of this session.</summary>
    private readonly Dictionary<string, double> _driftAtStart = new(StringComparer.Ordinal);

    /// <summary>
    /// Owned values that differ by more than the profile's drift_warn right now (fuel: "load the
    /// same fuel in the EFB"). One amber line, never a desync. Information only: never written.
    /// </summary>
    public IReadOnlyList<(string Var, double Delta)> DriftWarnings
    {
        get
        {
            var current = Drift;
            return (_profile.Datarefs ?? [])
                .Where(d => d.Class == SyncClass.Owned && d.DriftWarn is { } t && current.TryGetValue(d.Path, out var c) && Math.Abs(c.Delta) > t)
                .Select(d => (d.Path, current[d.Path].Delta))
                .ToList();
        }
    }

    private void OnDrift(string group, JsonArray values)
    {
        lock (_gate)
        {
            _drift[group] = values;
            var deltas = values.OfType<JsonObject>().ToDictionary(v => v["var"]!.GetValue<string>(), v => Math.Abs(v["delta"]!.GetValue<double>()));
            foreach (var d in (_profile.Datarefs ?? []).Where(d => d.Group == group && d.Class == SyncClass.Owned))
            {
                var now = deltas.GetValueOrDefault(d.Path);
                if (_driftAtStart.TryAdd(d.Path, now) && now > (d.DriftWarn ?? double.MaxValue))
                    _log.Event("drift_at_start", new JsonObject { ["var"] = d.Path, ["delta"] = now, ["warn_above"] = d.DriftWarn });
            }
        }
    }

    /// <summary>Latest owned drift per var: (pf, pm, delta) in the dataref's units.</summary>
    public IReadOnlyDictionary<string, (double Pf, double Pm, double Delta)> Drift
    {
        get
        {
            lock (_gate)
                return _drift.Values.SelectMany(a => a).OfType<JsonObject>().ToDictionary(
                    v => v["var"]!.GetValue<string>(),
                    v => (v["pf"]!.GetValue<double>(), v["pm"]!.GetValue<double>(), v["delta"]!.GetValue<double>()));
        }
    }

    public Labels Labels { get; }

    /// <summary>Tests: drop the next n incoming commands as if lost on the way.</summary>
    public void DropIncomingCommands(int n) => Interlocked.Exchange(ref _dropIncoming, n);

    public void Start()
    {
        lock (_gate)
        {
            foreach (var name in Plan.Watched)
            {
                if (!_sim.TryGet(name, out var v)) continue;
                _lastSeen[name] = v;
                if (_vars.TryGetValue(name, out var spec) && spec.Class == SyncClass.Delta) _baseline[name] = v;
            }
        }
        _sim.ValueChanged += OnSimValue;
        _sim.StateChanged += OnSimState;
        _client.MessageReceived += OnMessage;
        _client.Resumed += OnResumed;
        _client.StateChanged += OnServerState;
        _applyLoop = Task.Run(ApplyLoop);
    }

    // ---------------------------------------------------------------- local changes → commands

    private void OnSimState(SimState state)
    {
        _status.Sim = state == SimState.Connected ? "connected" : "disconnected";
        _hub.Publish("sim_state", state == SimState.Connected ? "Sim connected." : "Sim not connected; retrying. Not synced until it is back.",
            new JsonObject { ["state"] = _status.Sim });
        _log.Event("sim_state", new JsonObject { ["state"] = _status.Sim });
        // After (re)connect the first values are a fresh start, not pilot actions.
        bool active;
        lock (_gate)
        {
            _lastSeen.Clear();
            _baseline.Clear();
            active = _sessionActive;
        }
        // The server stops comparing while a sim is gone (one amber line instead of missing values).
        if (active) _client.Send("sim_state", new JsonObject { ["connected"] = state == SimState.Connected });
    }

    private void OnSimValue(string name, double value)
    {
        if (!_vars.TryGetValue(name, out var spec))
        {
            if (_unsynced.TryGetValue(name, out var label)) OnUnsyncedValue(name, value, label);
            return;
        }
        var commands = new List<JsonObject>();
        var requestControl = false;
        JsonObject? notice = null;
        string? notice_type = null;
        var resume = false;
        lock (_gate)
        {
            var hadPrev = _lastSeen.TryGetValue(name, out var prev);
            _lastSeen[name] = value;
            if (!hadPrev || prev != value) _lastChangeAt[name] = _clock.NowMs;
            // Deny list (CRO-33): never mirrored, never written. A press is shown to both pilots.
            if (spec.NeverWrite)
            {
                if (hadPrev && _sessionActive && value != prev && DeniedPress(spec, prev, value))
                {
                    var now = _clock.NowMs;
                    if (!_deniedSentAt.TryGetValue(name, out var last) || now - last >= _options.DeniedRepeatMs)
                    {
                        _deniedSentAt[name] = now;
                        (notice_type, notice) = ("denied_input", new JsonObject { ["var"] = name, ["group"] = spec.Group, ["label"] = Labels.Var(name) });
                    }
                }
            }
            else switch (spec.Class)
            {
                case SyncClass.Verified or SyncClass.Analog:
                    if (_echo.TryConsume(name, value)) { LogEcho(name, value); break; }
                    // The local pilot moved a spring switch Crosscheck holds away from rest: it is theirs now.
                    if (_springHeld.Remove(name, out var spring))
                    {
                        spring.Cap.Dispose();
                        _log.Event("mirrored_spring_taken", new JsonObject { ["var"] = name, ["value"] = value });
                    }
                    if (!hadPrev || !_sessionActive || value == prev) break;
                    if (spec.Class == SyncClass.Verified && CounterCheck(spec, "local:", prev, value) is { } suspect)
                        (notice_type, notice) = ("counter_suspect", suspect);
                    if (_counterSuspect.Contains(name)) break; // probably a press counter: not synced
                    commands.Add(NewCommand(spec, value, null));
                    break;
                case SyncClass.Delta:
                    if (!_baseline.TryGetValue(name, out var baseline) || !_sessionActive)
                    {
                        _baseline[name] = value;
                        break;
                    }
                    // Our own write landed (also an in-between value of several quick writes).
                    if (_echo.TryConsume(name, value, _sim.IsFresh(name))) { LogEcho(name, value); break; }
                    if (value == baseline) break;
                    _log.Event("local_delta", new JsonObject { ["var"] = name, ["value"] = value, ["baseline"] = baseline });
                    _baseline[name] = value;
                    commands.Add(NewCommand(spec, null, value - baseline));
                    break;
                case SyncClass.Pulse when IsEdge(spec):
                    if (_echo.TryConsume(name, value)) { LogEcho(name, value); break; }
                    // The local pilot touched a button Crosscheck holds down for the other pilot. Fenix only adds 1
                    // per press and per release, so Crosscheck's down still needs its up: the other pilot's up is
                    // held back until the local click ends, then applied (gate test 2 cause C).
                    if (_mirroredDown.ContainsKey(name))
                        _log.Event("mirrored_button_touched", new JsonObject { ["var"] = name, ["value"] = value });
                    // Not our write: the next mirrored edge is judged against this value, not our last write.
                    _lastEdgeWrite.Remove(name);
                    if (!hadPrev) break;
                    var edges = LocalEdges(name, prev, value, ref resume);
                    if (!_sessionActive) break;
                    if (edges.Count == 0) _log.Event("edge_ignored", new JsonObject { ["var"] = name, ["from"] = prev, ["to"] = value });
                    foreach (var edge in edges)
                    {
                        var now = _clock.NowMs;
                        var gap = _edgeAt.TryGetValue(name, out var last) ? (int)Math.Clamp(now - last, 0, int.MaxValue) : 0;
                        _edgeAt[name] = now;
                        // A takeover press is never written into the flying pilot's sim: as PM it waits for control.
                        if (IsTakeoverVar(name) && !IsPf)
                        {
                            // Only a press starts a request; the release of a press that was dropped is dropped too.
                            if (!_takeoverRequested && edge == "up")
                            {
                                _log.Event("takeover_edge_dropped", new JsonObject { ["var"] = name, ["edge"] = edge });
                                continue;
                            }
                            _takeoverHeld.Add((spec, edge, gap));
                            if (!_takeoverRequested)
                            {
                                _takeoverRequested = true;
                                requestControl = true;
                                _takeoverTimer?.Dispose();
                                _takeoverTimer = new Timer(_ => DropTakeover("control was not granted in time"), null, _options.TakeoverGrantTimeoutMs, Timeout.Infinite);
                            }
                            continue;
                        }
                        commands.Add(NewCommand(spec, edge == "down" ? 1 : 0, null, edge, gap));
                    }
                    break;
                case SyncClass.Pulse:
                    if (_echo.TryConsume(name, value)) { LogEcho(name, value); break; }
                    if (hadPrev && _sessionActive && prev == 0 && value != 0) commands.Add(NewCommand(spec, 1, null));
                    break;
                // observe and owned: never sent, only reported in snapshots.
            }
        }
        foreach (var command in commands) SendCommand(command);
        if (resume) ResumeParked(name, "local_release");
        if (notice is not null && notice_type is not null)
        {
            _log.Event(notice_type, (JsonObject)notice.DeepClone());
            if (notice_type == "denied_input")
                _hub.Publish("denied_input", $"{Labels.Var(name)} pressed in your cockpit – not mirrored (irreversible). Do the same in the other cockpit if intended.", (JsonObject)notice.DeepClone());
            else
                _hub.Publish("counter_suspect", $"{Labels.Var(name)}: probably a press counter – not synced.", (JsonObject)notice.DeepClone());
            _client.Send(notice_type, notice);
        }
        if (requestControl)
        {
            _log.Event("takeover_pressed", new JsonObject { ["var"] = name });
            _hub.Publish("takeover_pressed", "Takeover pushbutton pressed: requesting control. The press is mirrored once you have control.", new JsonObject { ["var"] = name });
            RequestControl();
        }
    }

    private void SendCommand(JsonObject command)
    {
        _client.Send("command", command);
        // Sender side of the effect check: one lamp read per burst, after its last edge (CRO-33).
        if (command["class"]?.GetValue<string>() == "pulse" && _vars.TryGetValue(command["var"]!.GetValue<string>(), out var spec) && spec.Verify?.Lvar is { } lamp)
            ScheduleEffect(spec, lamp, command["effect"] is not null ? command["id"]!.GetValue<string>() : null,
                command["settle_ms"]!.GetValue<int>());
    }

    private static bool IsEdge(VarEntry spec) => spec.Class == SyncClass.Pulse && spec.Press == PressMode.Edge;

    /// <summary>A deny-list button was pressed: a down edge (counter), or any change of a switch.</summary>
    private static bool DeniedPress(VarEntry spec, double prev, double value) =>
        IsEdge(spec) ? Edges(prev, value).Contains("down") : prev != value;

    private static bool Allowed(VarEntry spec, double value) =>
        (spec.Values is not { } values || values.Any(v => Math.Abs(v - value) < 1e-9))
        && (spec.Range is not [var min, var max] || (value >= min - 1e-9 && value <= max + 1e-9));

    /// <summary>
    /// Counter detector (gate test 2 cause A): a verified var whose values keep rising by exactly 1, with a
    /// value outside its allowed set, is probably a Fenix press counter profiled as a switch. It is not
    /// synced from then on, its writes are blocked, and both pilots get one amber line. Lock held.
    /// Returns the counter_suspect body when the var just became suspect.
    /// </summary>
    private JsonObject? CounterCheck(VarEntry spec, string source, double prev, double value)
    {
        var name = spec.Name;
        if (!Allowed(spec, value)) _seenOutside.Add(name);
        var key = source + name;
        var steps = _counterRun.TryGetValue(key, out var run) && run.Last == prev && value == prev + 1 ? run.Steps + 1
            : value == prev + 1 ? 1 : 0;
        _counterRun[key] = (value, steps);
        if (steps < 3 || !_seenOutside.Contains(name) || !_counterSuspect.Add(name)) return null;
        _sim.Guard.Block(name, "probably a press counter (its value keeps rising); not synced");
        return new JsonObject { ["var"] = name, ["group"] = spec.Group, ["label"] = Labels.Var(name) };
    }

    /// <summary>
    /// Whether "Set both" may be offered: proven absolute switches and analog knobs only (CRO-33), and only once
    /// a write of this var has landed in this sim during this session (CRO-40: in gate test 3 it was offered for
    /// flaps and speed brake, which Crosscheck could not write).
    /// </summary>
    public bool CanSetBoth(string name)
    {
        if (!_vars.TryGetValue(name, out var spec) || spec.Class is not (SyncClass.Verified or SyncClass.Analog)) return false;
        if (spec.NeverWrite || spec.Rest is not null || !_sim.Guard.IsWritable(name)) return false;
        lock (_gate) return !_counterSuspect.Contains(name) && !_seenOutside.Contains(name) && _writeConfirmed.Contains(name);
    }

    /// <summary>
    /// SimConnect refused to write this var (gate test 3 cause A). The runtime has blocked it already; this tells
    /// both pilots with one red line ("Crosscheck cannot write …"). Never silent. Any thread.
    /// </summary>
    public void ReportCannotWrite(string name, string why)
    {
        var body = new JsonObject { ["var"] = name, ["group"] = _vars.TryGetValue(name, out var spec) ? spec.Group : null, ["label"] = Labels.Var(name) };
        bool active;
        lock (_gate) active = _sessionActive;
        var logged = (JsonObject)body.DeepClone();
        logged["why"] = why;
        _log.Event("write_blocked", logged);
        _hub.Publish("cannot_write", $"Crosscheck cannot write {Labels.Var(name)}. Set it by hand in both cockpits.", (JsonObject)body.DeepClone());
        if (active) _client.Send("write_blocked", body);
    }

    /// <summary>
    /// One lamp read per burst (gate test 2 cause D): every edge of the button restarts the timer; it reads
    /// the lamp EffectSettleMs after the last edge and reports it for the burst's last down edge.
    /// </summary>
    private void ScheduleEffect(VarEntry spec, string lamp, string? downId, int settleMs)
    {
        var name = spec.Name;
        var delay = Math.Max(settleMs, _options.EffectSettleMs);
        lock (_gate)
        {
            string? id = downId;
            if (_effectTimers.Remove(name, out var old))
            {
                old.Timer.Dispose();
                id ??= old.Id;
            }
            if (id is null) return; // an up edge with no down in this burst: nothing to compare
            var timer = new Timer(_ => ReportEffect(name), null, Timeout.Infinite, Timeout.Infinite);
            _effectTimers[name] = (timer, id, lamp);
            timer.Change(delay, Timeout.Infinite);
        }
    }

    private void ReportEffect(string name)
    {
        string id, lamp;
        lock (_gate)
        {
            if (!_effectTimers.Remove(name, out var t)) return;
            t.Timer.Dispose();
            (id, lamp) = (t.Id, t.Lamp);
        }
        if (_stop.IsCancellationRequested) return;
        double? value = _sim.TryGet(lamp, out var v) ? v : null;
        _log.Event("effect_report", new JsonObject { ["id"] = id, ["var"] = lamp, ["value"] = value });
        _client.Send("effect_report", new JsonObject { ["id"] = id, ["var"] = lamp, ["value"] = value });
    }

    private bool IsTakeoverVar(string name) => _profile.Roles?.Takeover is { } t && (name == t.Captain || name == t.Fo);

    /// <summary>
    /// Fenix push buttons count +1 on press and +1 on release (odd = down). Each step is one edge, so +2
    /// in one frame is down and up. A decrease (a binding that writes 0/1) is one edge if the parity
    /// changed. A jump of more than 8 steps is not a click: one edge from the new parity at most.
    /// </summary>
    public static List<string> Edges(double prev, double value)
    {
        long p = (long)Math.Round(prev), v = (long)Math.Round(value);
        var edges = new List<string>();
        if (v > p && v - p <= 8)
            for (var k = p + 1; k <= v; k++) edges.Add((k & 1) == 1 ? "down" : "up");
        else if (((p ^ v) & 1) == 1)
            edges.Add((v & 1) == 1 ? "down" : "up");
        return edges;
    }

    /// <summary>
    /// The local pilot's edges of one change (lock held). Fenix adds 1 on every press and every release,
    /// so each local step alternates press and release, whatever the counter's parity (it can be odd after
    /// two pilots clicked at once). A decrease or a jump of more than 8 steps falls back to parity (Edges).
    /// Tracks the local press in progress; resume = held-back incoming edges may go now.
    /// </summary>
    private List<string> LocalEdges(string name, double prev, double value, ref bool resume)
    {
        long p = (long)Math.Round(prev), v = (long)Math.Round(value);
        var parity = Edges(prev, value);
        var count = v > p && v - p <= 8 ? (int)(v - p) : parity.Count;
        var edges = new List<string>();
        for (var k = 0; k < count; k++)
        {
            // A step: a release if the local pilot is pressing, else a press (a parity edge says which).
            var press = v > p && v - p <= 8 ? !_localPress.ContainsKey(name) : parity[k] == "down";
            if (press)
            {
                _localPress[name] = _clock.NowMs;
                edges.Add("down");
            }
            else
            {
                if (_localPress.Remove(name) && _parked.ContainsKey(name)) resume = true;
                edges.Add("up");
            }
        }
        return edges;
    }

    /// <summary>Control was granted: the held takeover press goes out as a normal press of the new PF.</summary>
    private void FlushTakeover()
    {
        var commands = new List<JsonObject>();
        lock (_gate)
        {
            if (!_takeoverRequested) return;
            _takeoverRequested = false;
            _takeoverTimer?.Dispose();
            _takeoverTimer = null;
            foreach (var (spec, edge, gap) in _takeoverHeld) commands.Add(NewCommand(spec, edge == "down" ? 1 : 0, null, edge, gap));
            _takeoverHeld.Clear();
        }
        if (commands.Count == 0) return;
        _log.Event("takeover_mirrored", new JsonObject { ["edges"] = commands.Count });
        foreach (var c in commands) SendCommand(c);
    }

    /// <summary>Control was not granted: the takeover press is not mirrored at all.</summary>
    private void DropTakeover(string why)
    {
        int dropped;
        lock (_gate)
        {
            if (!_takeoverRequested) return;
            _takeoverRequested = false;
            _takeoverTimer?.Dispose();
            _takeoverTimer = null;
            dropped = _takeoverHeld.Count;
            _takeoverHeld.Clear();
        }
        _log.Event("takeover_dropped", new JsonObject { ["why"] = why, ["edges"] = dropped });
        _hub.Publish("takeover_dropped", $"Takeover press not mirrored to the other cockpit: {why}.", new JsonObject { ["why"] = why });
    }

    /// <summary>
    /// A control that is not synced yet moved: both pilots get one amber line. Read-only, never
    /// written. The first values after (re)connect are not moves; at most one message per 2 s.
    /// </summary>
    private void OnUnsyncedValue(string name, double value, string label)
    {
        lock (_gate)
        {
            var hadPrev = _lastSeen.TryGetValue(name, out var prev);
            _lastSeen[name] = value;
            if (!hadPrev || !_sessionActive || value == prev) return;
            var now = _clock.NowMs;
            if (_unsyncedSentAt.TryGetValue(name, out var last) && now - last < 2000) return;
            _unsyncedSentAt[name] = now;
        }
        _log.Event("unsynced_input", new JsonObject { ["var"] = name, ["value"] = value });
        _client.Send("unsynced_input", new JsonObject { ["var"] = name, ["label"] = label });
    }

    private void LogEcho(string name, double value) =>
        _log.Event("echo_suppressed", new JsonObject { ["var"] = name, ["value"] = value });

    /// <summary>Builds a command and remembers it until accepted (lock held).</summary>
    private JsonObject NewCommand(VarEntry spec, double? value, double? delta, string? edge = null, int gapMs = 0)
    {
        var id = $"{_client.ClientId}-{++_commandCounter}";
        var body = new JsonObject
        {
            ["id"] = id,
            ["var"] = spec.Name,
            ["class"] = spec.Class.ToString().ToLowerInvariant(),
        };
        if (delta is { } d) body["delta"] = d;
        else body["value"] = value;
        body["group"] = spec.Group;
        body["settle_ms"] = (spec.Verify?.SettleMs ?? _options.DefaultSettleMs) + (spec.Class == SyncClass.Pulse ? spec.PulseMs ?? 0 : 0);
        if (edge is not null)
        {
            body["edge"] = edge;
            body["gap_ms"] = gapMs;
        }
        // A key with an effect lamp: the server compares the lamp of both sims after settle (after the down edge).
        if (spec.Class == SyncClass.Pulse && edge != "up" && spec.Verify?.Lvar is { } lamp)
            body["effect"] = new JsonObject { ["var"] = lamp, ["expect"] = spec.Verify.Expect == VerifyExpect.Parity ? "parity" : "same" };
        _pendingOwn[id] = (spec.Name, Absolute.Contains(spec.Class), body);
        // One line per press in the recent list: the release is not a separate action.
        if (edge != "up") _sentText[id] = Labels.Action(spec.Name, spec.Class, value, delta);
        if (_sentText.Count > 2000) _sentText.Remove(_sentText.Keys.First());
        _status.NoteActivity(spec.Name);
        CommandsSent++;
        return body;
    }

    private void OnResumed(bool fullResync)
    {
        // The server dedupes by id: re-send everything it may not have logged.
        List<JsonObject> resend;
        lock (_gate) resend = _pendingOwn.Values.Select(p => (JsonObject)p.Body.DeepClone()).ToList();
        foreach (var body in resend) _client.Send("command", body);
        List<JsonObject> safety;
        lock (_gate)
        {
            safety = [.. _pendingSafety];
            _pendingSafety.Clear();
        }
        foreach (var body in safety) _client.Send("safety_release", body);
        // The server forgets a lost client's sim state; say again if the sim is gone.
        if (_sim.State != SimState.Connected) _client.Send("sim_state", new JsonObject { ["connected"] = false });
        _hub.Publish("resumed", fullResync ? "Reconnected; full snapshot resync." : "Reconnected; missed changes replayed.",
            new JsonObject { ["full_resync"] = fullResync });
    }

    // ---------------------------------------------------------------- inbound messages

    private void OnMessage(JsonObject msg)
    {
        var type = msg["type"]!.GetValue<string>();
        var body = msg["body"]!.AsObject();
        var seq = msg["seq"] is JsonValue s ? s.GetValue<long>() : 0;
        switch (type)
        {
            case "created" or "joined" or "resumed":
                OnSession(body);
                break;
            case "command":
                OnRemoteCommand(body, seq);
                break;
            case "correction":
                OnCorrection(body);
                break;
            case "accepted":
                OnAccepted(body, seq);
                break;
            case "snapshot_request":
                OnSnapshotRequest(body);
                break;
            case "control_changed":
                SetPf(body["pf_client_id"]!.GetValue<string>());
                // A held takeover press goes out only if this pilot is now PF; otherwise it is dropped.
                if (IsPf) FlushTakeover();
                else DropTakeover("the other pilot has control");
                _client.Send("control_ack", new JsonObject { ["epoch"] = body["epoch"]!.DeepClone() });
                if (IsPf)
                {
                    ControlNotice = (JsonObject)body.DeepClone();
                    ControlNoticeAt = DateTimeOffset.UtcNow;
                }
                else ControlNotice = null;
                _hub.Publish("control_changed", ControlChangedText(body), body);
                break;
            case "control_handover":
                _hub.Publish("control_handover", "Control is changing hands.", body);
                break;
            case "control_denied":
                DropTakeover("control was not granted");
                _hub.Publish("control_denied", body["message"]!.GetValue<string>(), body);
                break;
            case "desync":
                _status.SetDesync(body["var"]!.GetValue<string>(), body);
                _hub.Publish("desync", body["message"]!.GetValue<string>(), body);
                break;
            case "notice":
                var kind = body["kind"]!.GetValue<string>();
                if (kind == "desync_cleared" && body["var"] is JsonValue v) _status.ClearDesync(v.GetValue<string>());
                _hub.Publish(kind, body["message"]!.GetValue<string>(), body);
                break;
            case "peer_status":
                var state = body["state"]!.GetValue<string>();
                _status.PeerState = state;
                if (body["name"] is JsonValue pn) PeerName = pn.GetValue<string>();
                PeerLostAt = state == "lost" ? DateTimeOffset.UtcNow : null;
                // Their "up" cannot arrive in time: release what Crosscheck holds down for them.
                if (state is "lost" or "left") ReleaseAllMirrored("peer_lost");
                _hub.Publish("peer_" + state, $"Other pilot ({body["seat"]}): {state}.", body);
                foreach (var w in body["warnings"] as JsonArray ?? [])
                    _hub.Publish("warning", w!["message"]!.GetValue<string>(), w.AsObject());
                break;
            case "advisory":
                var key = body["key"]!.GetValue<string>();
                if (body["kind"]?.GetValue<string>() == "counter_suspect" && body["var"] is JsonValue cv
                    && _vars.TryGetValue(cv.GetValue<string>(), out var counterSpec) && counterSpec.Class == SyncClass.Verified)
                {
                    lock (_gate) _counterSuspect.Add(counterSpec.Name);
                    _sim.Guard.Block(counterSpec.Name, "probably a press counter (its value keeps rising); not synced");
                }
                if (body["active"]?.GetValue<bool>() == true) _status.SetAdvisory(key, body);
                else _status.RemoveAdvisory(key);
                _hub.Publish("advisory", body["message"]!.GetValue<string>(), body);
                break;
            case "snapshot_diff":
                _hub.Publish("snapshot_diff", $"Snapshot differences in {body["group"]}.", body);
                break;
            case "ack":
            {
                var ok = body["ok"]?.GetValue<bool>() == true;
                if (ok) Interlocked.Increment(ref _acksOk);
                else _hub.Publish("ack_failed", $"Command {body["id"]} was not confirmed by the other sim.", body);
                string? what;
                lock (_gate) _sentText.Remove(body["id"]!.GetValue<string>(), out what);
                if (what is not null && body["superseded"]?.GetValue<bool>() != true)
                    _hub.Publish(ok ? "action_confirmed" : "action_failed",
                        ok ? $"{what} by you. Confirmed." : $"{what} by you. Not confirmed by the {PeerWord}'s sim.", body);
                break;
            }
            case "drift":
                OnDrift(body["group"]!.GetValue<string>(), (JsonArray)body["values"]!.DeepClone());
                break;
            case "stream":
                // Passed on to the physics side (Follow.PhysicsSync) only while this side is not PF.
                if (body["data"] is JsonObject data && body["frame"] is JsonValue fv && fv.TryGetValue<long>(out var frameNo))
                    _sim.Stream.Apply(frameNo, data);
                break;
            case "error":
                _hub.Publish("error", body["message"]!.GetValue<string>(), body);
                break;
        }
    }

    private void OnSession(JsonObject body)
    {
        lock (_gate) _sessionActive = true;
        _status.SessionId = body["session_id"]!.GetValue<string>();
        _status.ClientId = body["client_id"]!.GetValue<string>();
        _status.Seat = body["seat"]!.GetValue<string>();
        var side = _options.SeatSide ?? (_status.Seat == "guest" ? SeatSide.Fo : SeatSide.Captain);
        _status.SeatSide = side == SeatSide.Fo ? "fo" : "captain";
        if (body["join_code"] is JsonValue code) _status.JoinCode = code.GetValue<string>();
        if (body["peer"] is JsonObject peer)
        {
            _status.PeerState = peer["state"]!.GetValue<string>();
            if (peer["name"] is JsonValue pn) PeerName = pn.GetValue<string>();
            PeerLostAt = _status.PeerState == "lost" ? DateTimeOffset.UtcNow : null;
        }
        SetPf(body["pf_client_id"]?.GetValue<string>());
        if (body["advisories"] is JsonArray advisories)
            _status.ResetAdvisories(advisories.OfType<JsonObject>().Where(a => a["active"]?.GetValue<bool>() == true));
        if (_sim.State != SimState.Connected) _client.Send("sim_state", new JsonObject { ["connected"] = false });
        // Vars SimConnect refused earlier stay blocked while the bridge runs: the new session shows them too.
        foreach (var blocked in _sim.Guard.CannotWriteVars)
            _client.Send("write_blocked", new JsonObject { ["var"] = blocked, ["group"] = _vars.TryGetValue(blocked, out var bs) ? bs.Group : null, ["label"] = Labels.Var(blocked) });
        _snapshotTimer?.Dispose();
        _snapshotTimer = new Timer(_ => SendHashes(), null, _client.SnapshotIntervalMs, _client.SnapshotIntervalMs);
    }

    private void SetPf(string? pf)
    {
        _pfClientId = pf;
        _status.PfClientId = pf;
        _status.Role = IsPf ? "pf" : "pm";
        _sim.Stream.SetRole(IsPf);
    }

    private void OnAccepted(JsonObject body, long seq)
    {
        lock (_gate)
        {
            var id = body["id"]!.GetValue<string>();
            if (_pendingOwn.Remove(id, out var own) && own.Absolute)
                _lastSeqByVar[own.Var] = Math.Max(_lastSeqByVar.GetValueOrDefault(own.Var), seq);
            _appliedSeq = Math.Max(_appliedSeq, seq);
        }
    }

    /// <summary>The other pilot's command: seq order, superseded, dedupe by id (protocol.md §6).</summary>
    private void OnRemoteCommand(JsonObject body, long seq)
    {
        var id = body["id"]!.GetValue<string>();
        var name = body["var"]!.GetValue<string>();
        if (Interlocked.Decrement(ref _dropIncoming) >= 0)
        {
            _log.Event("test_dropped_command", new JsonObject { ["id"] = id });
            return;
        }
        Interlocked.CompareExchange(ref _dropIncoming, 0, -1);

        lock (_gate)
        {
            if (_ackCache.TryGetValue(id, out var cached))
            {
                _client.Send("ack", (JsonObject)cached.DeepClone());
                return;
            }
            if (_appliedEvents.Contains(id))
            {
                // Applied before and the ack is no longer cached: never press or click twice.
                _client.Send("ack", new JsonObject { ["id"] = id, ["ok"] = true, ["observed"] = null });
                return;
            }
            if (!_inFlight.Add(id)) return; // re-send while still being applied: the ack follows
            if (!_vars.TryGetValue(name, out var spec))
            {
                _inFlight.Remove(id);
                SendAck(id, false, null);
                return;
            }
            // Defence in depth: a takeover press is never written into the flying pilot's sim (protocol.md §8).
            if (IsTakeoverVar(name) && IsPf)
            {
                _inFlight.Remove(id);
                _log.Event("takeover_refused", new JsonObject { ["id"] = id, ["var"] = name, ["why"] = "this sim is flying" });
                SendAck(id, false, _sim.TryGet(name, out var tv) ? tv : null);
                return;
            }
            // Deny list: never written, whatever the other bridge sends (defence in depth; it never sends these).
            if (spec.NeverWrite)
            {
                _inFlight.Remove(id);
                _log.Event("denied_refused", new JsonObject { ["id"] = id, ["var"] = name });
                SendAck(id, false, _sim.TryGet(name, out var dv) ? dv : null);
                return;
            }
            if (spec.Class == SyncClass.Verified && body["value"]?.GetValue<double>() is { } incoming)
            {
                var last = _counterRun.TryGetValue("remote:" + name, out var run) ? run.Last : _sim.TryGet(name, out var cur) ? cur : incoming;
                if (CounterCheck(spec, "remote:", last, incoming) is { } suspect)
                {
                    _log.Event("counter_suspect", (JsonObject)suspect.DeepClone());
                    _hub.Publish("counter_suspect", $"{Labels.Var(name)}: probably a press counter – not synced.", (JsonObject)suspect.DeepClone());
                    _client.Send("counter_suspect", suspect);
                }
                if (_counterSuspect.Contains(name))
                {
                    _inFlight.Remove(id);
                    _appliedSeq = Math.Max(_appliedSeq, seq);
                    SendAck(id, false, _sim.TryGet(name, out var sv) ? sv : null, reason: "not_synced");
                    return;
                }
            }
            if (Absolute.Contains(spec.Class))
            {
                var ownPending = _pendingOwn.Values.Any(p => p.Absolute && p.Var == name);
                if (ownPending || _lastSeqByVar.GetValueOrDefault(name) > seq)
                {
                    _inFlight.Remove(id);
                    var current = _sim.TryGet(name, out var c) ? (double?)c : null;
                    SendAck(id, true, current, superseded: true);
                    _appliedSeq = Math.Max(_appliedSeq, seq);
                    return;
                }
                _lastSeqByVar[name] = seq;
            }
            _appliedSeq = Math.Max(_appliedSeq, seq);
            if (spec.Class is SyncClass.Pulse or SyncClass.Delta) _appliedEvents.Add(id);
            var settle = body["settle_ms"]!.GetValue<int>();
            _applyQueue.Writer.TryWrite(new ApplyJob(id, spec, false, body["value"]?.GetValue<double>(), body["delta"]?.GetValue<double>(), settle, null)
            {
                Edge = body["edge"]?.GetValue<string>(),
                GapMs = body["gap_ms"]?.GetValue<int>() ?? 0,
            });
        }
    }

    private void OnCorrection(JsonObject body)
    {
        var id = body["id"]!.GetValue<string>();
        var name = body["var"]!.GetValue<string>();
        var source = body["source"]!.GetValue<string>();
        lock (_gate)
        {
            if (_ackCache.TryGetValue(id, out var cached))
            {
                _client.Send("ack", (JsonObject)cached.DeepClone());
                return;
            }
            if (_inFlight.Contains(id)) return;
            var current = _sim.TryGet(name, out var c) ? (double?)c : null;
            // Defence in depth: as PF, only a logged pilot command may ever change this sim.
            if (IsPf && source != "command_log")
            {
                CorrectionsRefused++;
                _log.Event("correction_refused", new JsonObject { ["id"] = id, ["var"] = name, ["source"] = source, ["why"] = "PF accepts only command_log corrections" });
                _hub.Publish("correction_refused", $"Refused a correction of {name}: as PF only a logged pilot command may change this sim.", body);
                SendAck(id, false, current);
                return;
            }
            if (!_vars.TryGetValue(name, out var spec))
            {
                SendAck(id, false, current);
                return;
            }
            // Encoders are never corrected (gate test 2 cause F), and deny-list vars are never written.
            if (spec.Class == SyncClass.Delta || spec.NeverWrite)
            {
                CorrectionsRefused++;
                _log.Event("correction_refused", new JsonObject { ["id"] = id, ["var"] = name, ["source"] = source,
                    ["why"] = spec.NeverWrite ? "deny list" : "encoders are never corrected" });
                SendAck(id, false, current);
                return;
            }
            _inFlight.Add(id);
            _applyQueue.Writer.TryWrite(new ApplyJob(id, spec, true, null, null, body["settle_ms"]!.GetValue<int>(), body["target"]!.GetValue<double>()));
        }
    }

    /// <param name="reason">ok false only: press_lost (a mirrored press could not be applied), not_synced
    /// (a suspected counter), display_check (an encoder click whose proof is its display, no red per click).</param>
    private void SendAck(string id, bool ok, object? observed, bool superseded = false, string? reason = null)
    {
        var ack = new JsonObject { ["id"] = id, ["ok"] = ok, ["observed"] = observed switch { double d => d, string s => s, _ => null } };
        if (superseded) ack["superseded"] = true;
        if (!ok && reason is not null) ack["reason"] = reason;
        lock (_gate)
        {
            _ackCache[id] = ack;
            _ackOrder.Enqueue(id);
            while (_ackOrder.Count > _options.AckCacheSize) _ackCache.Remove(_ackOrder.Dequeue());
            _inFlight.Remove(id);
        }
        _client.Send("ack", (JsonObject)ack.DeepClone());
    }

    // ---------------------------------------------------------------- applying (write, verify, ack)

    private sealed record ApplyJob(string Id, VarEntry Spec, bool IsCorrection, double? Value, double? Delta, int SettleMs, double? Target)
    {
        /// <summary>Edge buttons: "down" or "up" (protocol.md §5).</summary>
        public string? Edge { get; init; }
        /// <summary>Edge buttons: the sender's time since the previous edge of this button.</summary>
        public int GapMs { get; init; }
        /// <summary>Not a write: the held-back edges of this button may go now (the local pilot released it).</summary>
        public bool Resume { get; init; }
        /// <summary>Held back longer than LocalHoldMaxMs: judged as if no local press were in progress (a lost press is red).</summary>
        public bool HoldCapped { get; init; }
    }

    private sealed record MirroredDown(double WrittenAtMs, string Id, double Value, Timer Cap);

    /// <param name="Reason">ack reason when not ok (press_lost).</param>
    /// <param name="Direction">delta: +1/-1, the turn direction (step 1 passes once the counter reached or passed the target).</param>
    /// <param name="Changed">The write changed the value (false when the sim already showed it and nothing was written).</param>
    private sealed record Written(bool Ok, double? StoredTarget, bool StoredSeen, string? Error, string? Reason = null, int Direction = 0, bool Changed = true);

    /// <summary>The local pilot released the button (or held it too long): its held-back edges go next, in order.</summary>
    private void ResumeParked(string name, string why)
    {
        lock (_gate)
        {
            if (_parkTimers.Remove(name, out var t)) t.Dispose();
            if (!_parked.ContainsKey(name)) return;
        }
        _log.Event("edges_resumed", new JsonObject { ["var"] = name, ["why"] = why });
        _applyQueue.Writer.TryWrite(new ApplyJob($"resume:{name}", _vars[name], false, null, null, 0, null) { Resume = true, HoldCapped = why == "local_hold_cap" });
    }

    /// <summary>
    /// An incoming edge for a button the local pilot is pressing right now waits (gate test 2 cause C): Fenix
    /// only adds 1, so mixing both pilots' edges would invert the button. Lock held. true = parked.
    /// </summary>
    private bool ParkIfLocalPress(ApplyJob job)
    {
        var name = job.Spec.Name;
        if (_parked.TryGetValue(name, out var list))
        {
            list.Add(job);
            return true;
        }
        if (!_localPress.TryGetValue(name, out var since)) return false;
        _parked[name] = [job];
        var left = Math.Max(0, since + _options.LocalHoldMaxMs - _clock.NowMs);
        _parkTimers[name] = new Timer(_ => ResumeParked(name, "local_hold_cap"), null, (int)left, Timeout.Infinite);
        _log.Event("edge_held_back", new JsonObject { ["id"] = job.Id, ["var"] = name, ["edge"] = job.Edge });
        return true;
    }

    /// <summary>Writes one at a time, in arrival order (key presses keep their order); verification runs alongside.</summary>
    private async Task ApplyLoop()
    {
        try
        {
            await foreach (var next in _applyQueue.Reader.ReadAllAsync(_stop.Token))
            {
                List<ApplyJob> jobs;
                lock (_gate)
                {
                    if (next.Resume)
                        jobs = _parked.Remove(next.Spec.Name, out var held) ? held.Select(j => j with { HoldCapped = next.HoldCapped }).ToList() : [];
                    else if (next.Edge is not null && ParkIfLocalPress(next)) continue;
                    else jobs = [next];
                }
                foreach (var job in jobs) await ApplyOne(job);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ApplyOne(ApplyJob job)
    {
        Written written;
        try
        {
            written = await Write(job);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            written = new Written(false, null, false, e.Message);
        }
        if (job.Spec.Class == SyncClass.Pulse && job.Spec.Verify?.Lvar is { } lamp)
            ScheduleEffect(job.Spec, lamp, job.Edge is null or "down" ? job.Id : null, job.SettleMs);
        if (!written.Ok)
        {
            _log.Event("apply_failed", new JsonObject { ["id"] = job.Id, ["var"] = job.Spec.Name, ["error"] = written.Error, ["reason"] = written.Reason });
            if (written.Reason == "press_lost")
                _hub.Publish("press_lost", $"A press of {Labels.Var(job.Spec.Name)} by the {PeerWord} could not be applied in your cockpit (the button was already down).",
                    new JsonObject { ["id"] = job.Id, ["var"] = job.Spec.Name });
            SendAck(job.Id, false, _sim.TryGet(job.Spec.Name, out var cur) ? cur : null,
                reason: written.Reason ?? (_sim.Guard.CannotWrite(job.Spec.Name) ? "cannot_write" : null));
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await VerifyAndAck(job, written);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Verifying {Var} failed", job.Spec.Name);
                SendAck(job.Id, false, null);
            }
        });
    }

    private async Task<Written> Write(ApplyJob job)
    {
        var spec = job.Spec;
        var name = spec.Name;
        if (_sim.State != SimState.Connected) return new Written(false, null, false, "sim not connected");
        var window = job.SettleMs + _options.EchoWindowMs;

        switch (spec.Class)
        {
            case SyncClass.Verified or SyncClass.Analog or SyncClass.Owned:
            {
                var target = job.Target ?? job.Value ?? throw new InvalidOperationException("no value");
                // Skip only if the sim already shows it and none of our own writes is still on its way.
                bool already;
                lock (_gate) already = _echo.Pending(name) == 0 && _sim.TryGet(name, out var current) && current == target;
                if (spec.Rest is not null) TrackSpring(spec, job.Id, target);
                if (!already)
                {
                    lock (_gate) _echo.Expect(name, target, window);
                    await WriteGuarded(name, target);
                }
                return new Written(true, target, true, null, Changed: !already);
            }
            case SyncClass.Delta:
            {
                // Encoders are never corrected (gate test 2 cause F): only a pilot's clicks are written.
                if (job.IsCorrection) return new Written(false, null, false, "encoders are never corrected");
                var change = job.Delta ?? throw new InvalidOperationException("no delta");
                // Build on our own last write: the sim's read-back may still be a frame behind.
                double counter, target;
                JsonObject? localTurn = null;
                Task write;
                lock (_gate)
                {
                    if (!_baseline.TryGetValue(name, out counter) && !_sim.TryGet(name, out counter))
                        return new Written(false, null, false, "encoder value unknown");
                    // The pilot turned this knob and that change was not processed yet: send it first, so writing
                    // counter + delta does not overwrite the pilot's click. Not when the sim shows exactly our last
                    // read-back write while a newer one is on its way: an in-between read-back of our own writes is
                    // never a pilot turn (gate test 2 cause F).
                    var inBetween = _sim.TryGet(name, out var shown) && _echo.InBetween(name, shown);
                    if (_sessionActive && !inBetween && _sim.IsFresh(name) && _sim.TryGet(name, out var now) && now != counter)
                    {
                        localTurn = NewCommand(spec, null, now - counter);
                        _echo.Expect(name, now, window, stale: true);
                        counter = now;
                        _baseline[name] = now;
                    }
                    target = counter + change;
                    _baseline[name] = target; // only new - baseline is a pilot delta
                    _echo.Expect(name, target, window);
                    // Started in the same step as the read above, so a pilot click has the smallest possible window
                    // to land in between and be overwritten (it would show as a red display line, never corrected).
                    write = StartWrite(name, target);
                }
                if (localTurn is not null) _client.Send("command", localTurn);
                _log.Event("delta_write", new JsonObject { ["id"] = job.Id, ["var"] = name, ["from"] = counter, ["change"] = change, ["target"] = target, ["flushed"] = localTurn is not null });
                await WriteGuarded(name, target, write);
                return new Written(true, target, true, null, Direction: Math.Sign(change));
            }
            case SyncClass.Pulse when IsEdge(spec):
                return await WriteEdge(job, window);
            case SyncClass.Pulse:
            {
                lock (_gate)
                {
                    _echo.Expect(name, 1, window + (spec.PulseMs ?? 150));
                    _echo.Expect(name, 0, window + (spec.PulseMs ?? 150));
                }
                await WriteGuarded(name, 1);
                await Task.Delay(spec.PulseMs ?? 150);
                var seen = _sim.TryGet(name, out var during) && during == 1;
                await WriteGuarded(name, 0);
                return new Written(true, 0, seen, null);
            }
            default:
                return new Written(false, null, false, $"class {spec.Class} is never written");
        }
    }

    /// <summary>
    /// One mirrored edge (protocol.md section 5). Always own value + 1, never the sender's number.
    /// Each edge first waits for the read-back of the previous edge this bridge wrote, and is judged against
    /// that written value, never a stale read (gate test 2 cause D).
    /// Down: only from up (even). A button the local pilot is pressing is left alone; a button that is down
    /// with no local press seen is a lost press (ack not ok, red), never ok.
    /// Up: only for a button Crosscheck itself pressed down, after its down was read back and never
    /// sooner than max(50 ms, the sender's gap) after it, so a hold is mirrored for as long as it lasts.
    /// </summary>
    private async Task<Written> WriteEdge(ApplyJob job, int window)
    {
        var name = job.Spec.Name;
        double? lastWrite;
        lock (_gate) lastWrite = _lastEdgeWrite.TryGetValue(name, out var lw) ? lw : null;
        if (lastWrite is { } previous) await WaitUntil(name, v => v >= previous - 1e-9, _options.StoredTimeoutMs);
        if (!_sim.TryGet(name, out var read)) return new Written(false, null, false, "button value unknown");
        double own;
        lock (_gate)
        {
            own = read;
            if (_lastEdgeWrite.TryGetValue(name, out var written) && written > read)
            {
                own = written; // our write has not been read back yet
                _log.Event("edge_judged_on_write", new JsonObject { ["id"] = job.Id, ["var"] = name, ["read"] = read, ["written"] = written });
            }
        }
        if (job.Edge == "down")
        {
            if (((long)Math.Round(own) & 1) == 1)
            {
                bool localPress;
                lock (_gate) localPress = _localPress.ContainsKey(name) && !job.HoldCapped;
                if (localPress)
                {
                    _log.Event("edge_already_down", new JsonObject { ["id"] = job.Id, ["var"] = name, ["value"] = own, ["local_press"] = true });
                    return new Written(true, own, true, null);
                }
                _log.Event("press_lost", new JsonObject { ["id"] = job.Id, ["var"] = name, ["value"] = own });
                return new Written(false, own, false, "the button was already down with no local press", "press_lost");
            }
            var target = Math.Round(own) + 1;
            lock (_gate)
            {
                _echo.Expect(name, target, window);
                _lastEdgeWrite[name] = target;
                if (_mirroredDown.Remove(name, out var old)) old.Cap.Dispose();
                var cap = new Timer(_ => SafetyRelease(name, "cap"), null, _options.SafetyReleaseMs, Timeout.Infinite);
                _mirroredDown[name] = new MirroredDown(_clock.NowMs, job.Id, target, cap);
            }
            try
            {
                await WriteGuarded(name, target);
            }
            catch
            {
                lock (_gate)
                {
                    if (_mirroredDown.Remove(name, out var failed)) failed.Cap.Dispose();
                    _lastEdgeWrite.Remove(name);
                }
                throw;
            }
            return new Written(true, target, true, null);
        }

        MirroredDown? down;
        lock (_gate) _mirroredDown.TryGetValue(name, out down);
        if (down is null)
        {
            // Not pressed by Crosscheck (the local pilot holds it, or it was released already): never released here.
            _log.Event("edge_up_skipped", new JsonObject { ["id"] = job.Id, ["var"] = name, ["value"] = own });
            return new Written(true, own, true, null);
        }
        await WaitFor(name, down.Value, _options.StoredTimeoutMs);
        var wait = down.WrittenAtMs + Math.Max(_options.EdgeFloorMs, job.GapMs) - _clock.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(wait, _options.SafetyReleaseMs)));
        double release;
        lock (_gate)
        {
            // The cap or the local pilot may have taken it meanwhile.
            if (!_mirroredDown.TryGetValue(name, out var still) || !ReferenceEquals(still, down))
                return new Written(true, _sim.TryGet(name, out var now) ? now : own, true, null);
            _mirroredDown.Remove(name);
            down.Cap.Dispose();
            if (!_sim.TryGet(name, out var cur)) return new Written(false, null, false, "button value unknown");
            // Our down may still be on its way into the sim: judge against what we wrote.
            if (cur < down.Value && _lastEdgeWrite.GetValueOrDefault(name) == down.Value) cur = down.Value;
            // Only our own down is released: unchanged, or after complete local clicks (+2 each), never while
            // the local pilot is pressing it.
            var steps = (long)Math.Round(cur - down.Value);
            if (steps < 0 || (steps & 1) == 1 || _localPress.ContainsKey(name))
            {
                _log.Event("edge_up_skipped", new JsonObject { ["id"] = job.Id, ["var"] = name, ["value"] = cur, ["mirrored"] = down.Value });
                return new Written(true, cur, true, null);
            }
            release = cur + 1;
            _echo.Expect(name, release, window);
            _lastEdgeWrite[name] = release;
        }
        await WriteGuarded(name, release);
        return new Written(true, release, true, null);
    }

    /// <summary>A spring switch written away from rest is tracked (30 s cap); written back to rest, it is not.</summary>
    private void TrackSpring(VarEntry spec, string id, double target)
    {
        var name = spec.Name;
        lock (_gate)
        {
            if (_springHeld.Remove(name, out var old)) old.Cap.Dispose();
            if (target == spec.Rest) return;
            var cap = new Timer(_ => SpringRelease(name, "cap"), null, _options.SafetyReleaseMs, Timeout.Infinite);
            _springHeld[name] = new MirroredDown(_clock.NowMs, id, target, cap);
        }
    }

    /// <summary>
    /// The same narrow exception for spring-loaded switches (owner decision, gate test 2 prep): the rest
    /// value for a switch Crosscheck itself moved away from rest in this sim, when the other pilot's
    /// release never arrived. Never another value, never a switch the local pilot touched; always red.
    /// </summary>
    private void SpringRelease(string name, string cause)
    {
        MirroredDown? held;
        double rest;
        lock (_gate)
        {
            if (!_springHeld.Remove(name, out held)) return;
            held.Cap.Dispose();
            rest = _vars[name].Rest!.Value;
            if (!_sim.TryGet(name, out var cur) || cur != held.Value)
            {
                _log.Event("safety_release_skipped", new JsonObject { ["var"] = name, ["cause"] = cause, ["value"] = _sim.TryGet(name, out var v) ? v : null });
                return;
            }
            _echo.Expect(name, rest, _options.EchoWindowMs);
        }
        var message = $"Crosscheck returned {Labels.Var(name)} to {Labels.Value(name, rest)} {ReleaseWhy(cause)}.";
        _log.Event("safety_release", new JsonObject { ["var"] = name, ["cause"] = cause, ["id"] = held.Id, ["from"] = held.Value, ["to"] = rest });
        ReportRelease(name, cause, rest, message);
    }

    private string ReleaseWhy(string cause) => cause switch
    {
        "cap" => $"after {_options.SafetyReleaseMs / 1000.0:0.#} s without a release from the {PeerWord}",
        "link_lost" => "because the connection to the server was lost",
        _ => $"because the {PeerWord} was lost",
    };

    /// <summary>Red desync at once (the link may be down), the write, and safety_release to the server.</summary>
    private void ReportRelease(string name, string cause, double value, string message)
    {
        var body = new JsonObject { ["var"] = name, ["group"] = _vars[name].Group, ["cause"] = cause, ["observed"] = value };
        _status.SetDesync(name, new JsonObject
        {
            ["var"] = name, ["reason"] = "safety_release", ["tier"] = "red", ["blocking"] = false, ["group"] = _vars[name].Group,
            ["message"] = message,
        });
        _hub.Publish("safety_release", message, (JsonObject)body.DeepClone());
        _ = WriteSafely(name, value);
        lock (_gate)
        {
            if (_client.State != ServerState.Connected)
            {
                _pendingSafety.Add(body);
                return;
            }
        }
        _client.Send("safety_release", body);
    }

    /// <summary>
    /// The only write no pilot made (CLAUDE.md, protocol.md §5): an "up" for a button Crosscheck itself
    /// pressed down in this sim, when the other pilot's "up" never arrived. Never a down, never another
    /// var, never a button the local pilot touched; always a red desync.
    /// </summary>
    private void SafetyRelease(string name, string cause)
    {
        MirroredDown? down;
        double release;
        lock (_gate)
        {
            if (!_mirroredDown.Remove(name, out down)) return;
            down.Cap.Dispose();
            // Only Crosscheck's own down: unchanged, or after complete local clicks (+2 each); never while the
            // local pilot is pressing it, never to an odd value.
            var steps = _sim.TryGet(name, out var cur) ? (long)Math.Round(cur - down.Value) : -1;
            if (steps < 0 || (steps & 1) == 1 || _localPress.ContainsKey(name) || ((long)Math.Round(cur) & 1) == 0)
            {
                _log.Event("safety_release_skipped", new JsonObject { ["var"] = name, ["cause"] = cause, ["value"] = _sim.TryGet(name, out var v) ? v : null });
                return;
            }
            release = cur + 1;
            _echo.Expect(name, release, _options.EchoWindowMs);
        }
        var message = $"Crosscheck released {Labels.Var(name)} {ReleaseWhy(cause)}.";
        _log.Event("safety_release", new JsonObject { ["var"] = name, ["cause"] = cause, ["id"] = down.Id, ["from"] = down.Value, ["to"] = release });
        ReportRelease(name, cause, release, message);
    }

    private async Task WriteSafely(string name, double value)
    {
        try
        {
            await WriteGuarded(name, value);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Safety release of {Var} failed", name);
        }
    }

    private void ReleaseAllMirrored(string cause)
    {
        List<string> names;
        List<string> springs;
        lock (_gate)
        {
            names = [.. _mirroredDown.Keys];
            springs = [.. _springHeld.Keys];
        }
        foreach (var n in names) SafetyRelease(n, cause);
        foreach (var n in springs) SpringRelease(n, cause);
    }

    private void OnServerState(ServerState state)
    {
        if (state is ServerState.Reconnecting or ServerState.Disconnected) ReleaseAllMirrored("link_lost");
    }

    private Task WriteGuarded(string name, double value) => WriteGuarded(name, value, StartWrite(name, value));

    /// <summary>Starts a write; a refusal by the guard comes back as the task's exception.</summary>
    private Task StartWrite(string name, double value)
    {
        try
        {
            return _sim.WriteAsync(name, value);
        }
        catch (Exception e)
        {
            return Task.FromException(e);
        }
    }

    private async Task WriteGuarded(string name, double value, Task write)
    {
        try
        {
            await write;
        }
        catch (SafetyViolationException e)
        {
            lock (_gate) _echo.Forget(name);
            _log.Event("safety_refused", new JsonObject { ["var"] = name, ["value"] = value, ["error"] = e.Message });
            _hub.Publish("safety_refused", e.Message, new JsonObject { ["var"] = name, ["value"] = value });
            throw;
        }
    }

    /// <summary>
    /// Step 1: the own var shows the value (1-2 frames). Counters (encoders, push buttons) pass once they
    /// reached or passed the target in their direction: fast turns and presses move on (gate test 2 cause E).
    /// Step 2: the effect source after settle_ms.
    /// </summary>
    private async Task VerifyAndAck(ApplyJob job, Written written)
    {
        var spec = job.Spec;
        var name = spec.Name;
        var target = written.StoredTarget ?? 0;
        var stored = written.StoredSeen && await (spec.Class switch
        {
            SyncClass.Delta when written.Direction > 0 => WaitUntil(name, v => v >= target - 1e-9, _options.StoredTimeoutMs),
            SyncClass.Delta when written.Direction < 0 => WaitUntil(name, v => v <= target + 1e-9, _options.StoredTimeoutMs),
            SyncClass.Pulse when IsEdge(spec) => WaitUntil(name, v => v >= target - 1e-9, _options.StoredTimeoutMs),
            _ => WaitFor(name, target, _options.StoredTimeoutMs),
        });
        await Task.Delay(job.SettleMs);

        var ok = stored;
        JsonNode? effect = null;
        if (spec.Verify is { } verify)
        {
            if (verify.Graphql is { } path)
            {
                var value = await _gql.GetAsync(path);
                effect = Node(value);
                if (verify.Scale is { } scale && value is double d && spec.Class != SyncClass.Delta)
                {
                    var expected = (job.Target ?? job.Value ?? 0) * scale;
                    ok &= Math.Abs(d - expected) <= Math.Max(0.5, Math.Abs(expected) * 1e-6);
                }
            }
            else if (verify.Lvar is { } lvar)
            {
                effect = _sim.TryGet(lvar, out var e) ? e : null;
            }
            else if (verify.Mcdu is int cdu)
            {
                var screen = _gql.Mcdu(cdu);
                effect = screen?.Hash;
                if (verify.Contains is { } text) ok &= screen?.Text.Contains(text, StringComparison.Ordinal) == true;
            }
        }

        object? observed = spec.Class switch
        {
            // An encoder without a display var is verified by read-back only: its own counter.
            SyncClass.Delta => spec.Display is { } disp ? (_sim.TryGet(disp, out var dv) ? dv : null) : (_sim.TryGet(name, out var cv) ? cv : null),
            SyncClass.Pulse when IsEdge(spec) => written.StoredTarget,
            SyncClass.Pulse => written.StoredSeen ? (job.Value ?? 1) : null,
            _ => _sim.TryGet(name, out var v) ? v : null,
        };
        CommandsApplied++;
        if (!job.IsCorrection) _status.NoteActivity(name);
        if (!job.IsCorrection && job.Edge != "up")
        {
            var what = Labels.Action(name, spec.Class, job.Value, job.Delta);
            _hub.Publish("action_applied", ok ? $"{what} by {PeerWord}. Confirmed." : $"{what} by {PeerWord}. Not confirmed in your sim.",
                new JsonObject { ["id"] = job.Id, ["var"] = name, ["ok"] = ok });
        }
        _log.Event("applied", new JsonObject
        {
            ["id"] = job.Id, ["var"] = name, ["correction"] = job.IsCorrection, ["stored"] = stored, ["effect"] = effect?.DeepClone(), ["ok"] = ok,
        });
        // A key with an effect lamp: its lamp goes to the server once per burst (effect_report, ScheduleEffect).
        // An encoder with a display: its proof is the display comparison 2 s after the last click, so a
        // click that did not land is no red line of its own (display_check).
        // A write that changed the value and landed: "Set both" may be offered for this var from now on (CRO-40).
        // Writing the value the sim already showed proves nothing (gate test 3: "close ok, open failed").
        if (ok && written.Changed && Absolute.Contains(spec.Class)) lock (_gate) _writeConfirmed.Add(name);
        // A write SimConnect refused (gate test 3 cause A): the reason makes it the red "cannot write" line.
        SendAck(job.Id, ok, observed, reason: !ok && _sim.Guard.CannotWrite(name) ? "cannot_write"
            : spec.Class == SyncClass.Delta && spec.Display is not null ? "display_check" : null);
    }

    private static JsonNode? Node(object? v) => v switch { double d => d, string s => s, _ => null };

    private Task<bool> WaitFor(string name, double target, int timeoutMs) =>
        WaitUntil(name, v => Math.Abs(v - target) < 1e-9, timeoutMs);

    private async Task<bool> WaitUntil(string name, Func<double, bool> done, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (_sim.TryGet(name, out var v) && done(v)) return true;
            if (DateTime.UtcNow > until) return false;
            await Task.Delay(5);
        }
    }

    // ---------------------------------------------------------------- snapshots

    private IEnumerable<string> Groups => _profile.Groups.Where(g => GroupValues(g).Count > 0);

    /// <summary>Values of one group in snapshot form (see protocol.md §7).</summary>
    public List<(string Var, string Class, object? Value)> GroupValues(string group)
    {
        var list = new List<(string, string, object?)>();
        foreach (var v in _profile.Vars.Where(v => v.Group == group))
        {
            // The display of an encoder is an input: it is compared under the encoder name (class delta), so a
            // difference is corrected or red, never a quiet indicator.
            if (v.Class == SyncClass.Observe && _deltaDisplays.Contains(v.Name)) continue;
            // The lamp of an edge button is that button's state: reported as a toggle item next to the button.
            if (v.Class == SyncClass.Observe && _toggleLamps.ContainsKey(v.Name)) continue;
            // Deny list: the raw value (never mirrored, so a different counter is a press in one cockpit only).
            // Compared only: never corrected, never written (CRO-33).
            if (v.NeverWrite)
            {
                list.Add((v.Name, "denied", _sim.TryGet(v.Name, out var nv) ? nv : null));
                continue;
            }
            switch (v.Class)
            {
                case SyncClass.Pulse when IsEdge(v):
                    // Parity only: the counters differ between the sims by design. Odd = held down.
                    list.Add((v.Name, "pulse", _sim.TryGet(v.Name, out var pv) ? (double)((long)Math.Round(pv) & 1) : null));
                    // The lamp is null while its unit is unpowered (context "unpowered", not compared; CRO-34).
                    if (v.Verify?.Lvar is { } lamp && _toggleLamps.GetValueOrDefault(lamp) == v.Name)
                        list.Add((lamp, "toggle", !IsUnpowered(_vars[lamp]) && _sim.TryGet(lamp, out var lv) ? lv : null));
                    break;
                case SyncClass.Observe when v.Alarm is not null:
                    // An indicator of an input Crosscheck cannot sync (yet): compared as an input (red), never quiet.
                    list.Add((v.Name, "mode", !IsUnpowered(v) && _sim.TryGet(v.Name, out var mv) ? mv : null));
                    break;
                case SyncClass.Observe when IsLoose(v):
                    // Tolerance or power gate: compared value by value (snapshot_hash.loose), not through the hash.
                    break;
                case SyncClass.Verified or SyncClass.Analog or SyncClass.Owned or SyncClass.Observe:
                    list.Add((v.Name, v.Class.ToString().ToLowerInvariant(), _sim.TryGet(v.Name, out var x) ? x : null));
                    break;
                case SyncClass.Delta:
                    // Unpowered: no display to compare (null, context "unpowered"; gate test 2 cause F).
                    list.Add((v.Name, "delta", v.Display is { } d && DeltaContext(v) != Unpowered && _sim.TryGet(d, out var dv) ? dv : null));
                    break;
            }
        }
        foreach (var d in (_profile.Datarefs ?? []).Where(d => d.Group == group && d.Class is SyncClass.Owned or SyncClass.Observe))
            list.Add((d.Path, d.Class.ToString().ToLowerInvariant(), _gql.Cached(d.Path)));
        var cdus = _profile.Vars.Where(v => v.Group == group && v.Class == SyncClass.Pulse && v.Verify?.Mcdu is not null)
            .Select(v => v.Verify!.Mcdu!.Value).Distinct().Order();
        foreach (var cdu in cdus)
            list.Add(($"aircraft.mcdu{cdu}.display", "observe", _gql.Mcdu(cdu)?.Hash));
        return list;
    }

    private const string Unpowered = "unpowered";

    /// <summary>
    /// A plain indicator with a tolerance, a power flag or an amber notice: sent in snapshot_hash.loose (CRO-34,
    /// CRO-40).
    /// </summary>
    private static bool IsLoose(VarEntry v) =>
        v is { Class: SyncClass.Observe, Alarm: null } && (v.Tolerance is not null || v.Powered is not null || v.Notice is not null);

    /// <summary>The indicator's unit is off (its power flag reads 0 or is unknown): not compared.</summary>
    private bool IsUnpowered(VarEntry v) => v.Powered is { } p && (!_sim.TryGet(p, out var power) || power == 0);

    /// <summary>The loose indicators of one group, as sent with its hash (protocol.md §7).</summary>
    public List<(string Var, double? Value, double? Tolerance, bool Unpowered, string? Notice)> LooseValues(string group)
    {
        var list = new List<(string, double?, double?, bool, string?)>();
        foreach (var v in _profile.Vars.Where(v => v.Group == group && IsLoose(v)))
        {
            // Lamps of edge buttons and encoder displays are reported under their control, never loose.
            if (_toggleLamps.ContainsKey(v.Name) || _deltaDisplays.Contains(v.Name)) continue;
            var off = IsUnpowered(v);
            list.Add((v.Name, !off && _sim.TryGet(v.Name, out var x) ? x : null, v.Tolerance, off, v.Notice));
        }
        return list;
    }

    /// <summary>Context of a toggle or mode item with a power flag: "unpowered" while its unit is off.</summary>
    private string? ItemContext(string name) => _vars.TryGetValue(name, out var v) && IsUnpowered(v) ? Unpowered : null;

    /// <summary>
    /// compare_when of an encoder: "unpowered" while the unit is off, else the values of its mode vars
    /// ("1,0"); the server compares the display only when both sides report the same context. null = always.
    /// </summary>
    public string? DeltaContext(VarEntry v)
    {
        if (v.CompareWhen is not { } when) return null;
        if (when.Powered is { } p && (!_sim.TryGet(p, out var power) || power == 0)) return Unpowered;
        return string.Join(",", (when.Same ?? []).Select(n => _sim.TryGet(n, out var x) ? CanonicalHash.FormatValue(x) : "null"));
    }

    private Dictionary<string, double>? _driftStepsCache;

    /// <summary>Drift step per owned dataref (drift_step, or half of drift_warn).</summary>
    private Dictionary<string, double> _driftSteps => _driftStepsCache ??= (_profile.Datarefs ?? [])
        .Where(d => d.Class == SyncClass.Owned && d.EffectiveDriftStep is not null)
        .ToDictionary(d => d.Path, d => d.EffectiveDriftStep!.Value, StringComparer.Ordinal);

    public string GroupHash(string group) => CanonicalHash.Compute(GroupValues(group).Select(v => (v.Var, v.Value)));

    private void SendHashes()
    {
        if (_sim.State != SimState.Connected || _client.State != ServerState.Connected) return;
        if (_status.PeerState is "joined" or "resumed" or "connected") LastComparedAt = DateTimeOffset.UtcNow;
        foreach (var group in Groups)
        {
            var message = new JsonObject
            {
                ["group"] = group,
                ["hash"] = GroupHash(group),
                ["applied_seq"] = Interlocked.Read(ref _appliedSeq),
            };
            var loose = LooseValues(group);
            if (loose.Count > 0)
                message["loose"] = new JsonArray(loose.Select(l =>
                {
                    var item = new JsonObject { ["var"] = l.Var, ["value"] = l.Value is { } x ? JsonValue.Create(x) : null };
                    if (l.Tolerance is { } t) item["tolerance"] = t;
                    if (l.Unpowered) item["context"] = Unpowered;
                    // An amber indicator (profile notice): a difference is one amber line with this text (CRO-40).
                    if (l.Notice is { } notice)
                    {
                        item["amber"] = true;
                        item["message"] = notice;
                    }
                    return (JsonNode)item;
                }).ToArray());
            _client.Send("snapshot_hash", message);
        }
    }

    private void OnSnapshotRequest(JsonObject body)
    {
        // A disconnected sim has no values: no answer (the server knows from sim_state; not comparable).
        if (_sim.State != SimState.Connected) return;
        var groups = body["groups"] is JsonArray arr ? arr.Select(g => g!.GetValue<string>()).ToList() : Groups.ToList();
        foreach (var group in groups.Where(g => _profile.Groups.Contains(g)))
        {
            var values = GroupValues(group);
            if (values.Count == 0) continue;
            _client.Send("snapshot_values", new JsonObject
            {
                ["request_id"] = body["request_id"]!.DeepClone(),
                ["group"] = group,
                ["values"] = new JsonArray(values.Select(v =>
                {
                    var item = new JsonObject { ["var"] = v.Var, ["class"] = v.Class, ["value"] = Node(v.Value) };
                    if (v.Class == "owned" && _driftSteps.TryGetValue(v.Var, out var step)) item["step"] = step;
                    if (v.Class == "toggle") item["button"] = _toggleLamps[v.Var];
                    if (v.Class is "toggle" or "mode" && ItemContext(v.Var) is { } off) item["context"] = off;
                    if (v.Class == "delta" && DeltaContext(_vars[v.Var]) is { } context) item["context"] = context;
                    // The profile's limits, so the server never sends a correction outside them (CRO-40).
                    if (v.Class is "verified" or "analog" && _vars.TryGetValue(v.Var, out var limits))
                    {
                        if (limits.Values is { } allowed) item["values"] = new JsonArray(allowed.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
                        if (limits.Range is [var min, var max]) item["range"] = new JsonArray(min, max);
                    }
                    if (v.Class == "mode" && _vars[v.Var].Alarm is { } alarm)
                    {
                        item["message"] = alarm.Text;
                        if (alarm.As is { } at) item["report_as"] = at;
                    }
                    return (JsonNode)item;
                }).ToArray()),
            });
        }
    }

    // ---------------------------------------------------------------- control and stream

    /// <summary>"I have control" (as PM). confirm ["@peer_lost"] only when the PF is not connected.</summary>
    public void RequestControl(IReadOnlyList<string>? confirm = null)
    {
        var body = new JsonObject();
        if (confirm is { Count: > 0 }) body["confirm"] = new JsonArray(confirm.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        _client.Send("control_request", body);
    }

    /// <summary>
    /// "Set both to X" from the UI: a normal pilot command from this seat (logged by the server as
    /// a pilot action, so it clears the desync), and the own sim is set too if it differs. Only
    /// proven absolute switches and analog knobs (CanSetBoth): never push buttons, counters, spring
    /// switches, deny-list vars or a var whose values were seen outside its allowed set (CRO-33). Only
    /// values the profile allows (checked by the adapter's guard).
    /// </summary>
    public async Task PilotSetAsync(string name, double value)
    {
        if (!_vars.TryGetValue(name, out var spec) || spec.Class is not (SyncClass.Verified or SyncClass.Analog))
            throw new SafetyViolationException($"{name} cannot be set from here (only switches and knobs in the profile).");
        if (spec.NeverWrite)
            throw new SafetyViolationException($"{Labels.Var(name)} is never set by Crosscheck (irreversible). Do it in the cockpit if intended.");
        if (!CanSetBoth(name))
            throw new SafetyViolationException($"{Labels.Var(name)} cannot be set from here (no write of it confirmed in this session yet, or it may be a press counter). Set it in the cockpit.");
        if (!_sim.Guard.IsAllowedValue(name, value))
            throw new SafetyViolationException($"{Labels.Number(value)} is not an allowed value of {name}.");
        if (_sim.State != SimState.Connected) throw new InvalidOperationException("The sim is not connected.");
        JsonObject command;
        bool write;
        lock (_gate)
        {
            if (!_sessionActive) throw new InvalidOperationException("Not in a session.");
            command = NewCommand(spec, value, null);
            write = !(_sim.TryGet(name, out var current) && current == value);
            if (write) _echo.Expect(name, value, (spec.Verify?.SettleMs ?? _options.DefaultSettleMs) + _options.EchoWindowMs);
        }
        _log.Event("pilot_set", new JsonObject { ["var"] = name, ["value"] = value, ["source"] = "ui" });
        _client.Send("command", command);
        if (write) await WriteGuarded(name, value);
    }

    /// <summary>
    /// "Press X here only" from the UI, for a toggle button whose light differs between the cockpits
    /// (toggle_mismatch): a press in the cockpit would be mirrored and flip both lights. One click (down,
    /// then up) in this pilot's own sim, not mirrored. A pilot action, never automatic; only edge buttons
    /// whose light is their state, and only while the button is up.
    /// </summary>
    public async Task PressHereOnlyAsync(string name)
    {
        if (!_vars.TryGetValue(name, out var spec) || !IsEdge(spec) || spec.Verify?.Lvar is null || IsTakeoverVar(name) || spec.NeverWrite)
            throw new SafetyViolationException($"{name} cannot be pressed from here (only push buttons whose light shows their state).");
        if (_sim.State != SimState.Connected) throw new InvalidOperationException("The sim is not connected.");
        double down;
        lock (_gate)
        {
            if (!_sessionActive) throw new InvalidOperationException("Not in a session.");
            if (!_sim.TryGet(name, out var own)) throw new InvalidOperationException($"{Labels.Var(name)} cannot be read.");
            if (((long)Math.Round(own) & 1) == 1) throw new InvalidOperationException($"{Labels.Var(name)} is held down right now.");
            down = Math.Round(own) + 1;
            var window = (spec.Verify.SettleMs) + _options.EchoWindowMs + 500;
            _echo.Expect(name, down, window); // our own click: never sent as a pilot press
            _echo.Expect(name, down + 1, window);
        }
        _log.Event("press_here_only", new JsonObject { ["var"] = name, ["from"] = down - 1, ["source"] = "ui" });
        _hub.Publish("press_here_only", $"{Labels.Var(name)} pressed in your cockpit only (not mirrored).", new JsonObject { ["var"] = name });
        await WriteGuarded(name, down);
        await Task.Delay(150);
        await WriteGuarded(name, down + 1);
    }

    /// <summary>
    /// Whether "Release it" may be offered for a button the server reports stuck pressed in this cockpit
    /// (stuck_pressed with this client's id): an edge button, not a takeover or deny-list button.
    /// </summary>
    public bool CanReleaseHere(string name, JsonObject desync) =>
        _vars.TryGetValue(name, out var spec) && IsEdge(spec) && !spec.NeverWrite && !IsTakeoverVar(name)
        && desync["reason"]?.GetValue<string>() == "stuck_pressed"
        && desync["client_id"]?.GetValue<string>() is { } id && id == _client.ClientId;

    /// <summary>
    /// "Release it" from the UI (gate test 2 cause C, owner decision 2026-11-02): one up step (own value + 1,
    /// to even) for a push button that is stuck pressed in this pilot's own cockpit. Pilot-initiated, never
    /// automatic; never a down; only while no pilot holds it on either side: the server raised stuck_pressed
    /// for this cockpit (the other side is up), the button rested unchanged for StuckMs here, and no mirrored
    /// edge is waiting for it. Not mirrored (the other cockpit's button is already up).
    /// </summary>
    public async Task ReleaseItAsync(string name)
    {
        if (!_vars.TryGetValue(name, out var spec) || !IsEdge(spec) || IsTakeoverVar(name) || spec.NeverWrite)
            throw new SafetyViolationException($"{name} cannot be released from here (only push buttons).");
        if (_sim.State != SimState.Connected) throw new InvalidOperationException("The sim is not connected.");
        if (_status.DesyncOf(name) is not { } desync || !CanReleaseHere(name, desync))
            throw new InvalidOperationException($"{Labels.Var(name)} is not reported stuck pressed in your cockpit.");
        double up;
        lock (_gate)
        {
            if (!_sessionActive) throw new InvalidOperationException("Not in a session.");
            if (!_sim.TryGet(name, out var own)) throw new InvalidOperationException($"{Labels.Var(name)} cannot be read.");
            if (((long)Math.Round(own) & 1) == 0) throw new InvalidOperationException($"{Labels.Var(name)} is already up.");
            if (_lastChangeAt.TryGetValue(name, out var at) && _clock.NowMs - at < _options.StuckMs)
                throw new InvalidOperationException($"{Labels.Var(name)} moved in the last {_options.StuckMs / 1000} s: someone may be holding it. Try again when it rests.");
            if (_parked.ContainsKey(name) || _echo.Pending(name) > 0)
                throw new InvalidOperationException($"{Labels.Var(name)} is being pressed by the other pilot right now. Try again in a moment.");
            up = Math.Round(own) + 1;
            if (((long)up & 1) != 0) throw new InvalidOperationException("Crosscheck never writes a down step here.");
            if (_mirroredDown.Remove(name, out var held)) held.Cap.Dispose();
            _localPress.Remove(name);
            _echo.Expect(name, up, _options.EchoWindowMs);
            _lastEdgeWrite[name] = up;
        }
        _log.Event("release_it", new JsonObject { ["var"] = name, ["from"] = up - 1, ["to"] = up, ["source"] = "ui" });
        _hub.Publish("release_it", $"{Labels.Var(name)} released in your cockpit (one up step, not mirrored).", new JsonObject { ["var"] = name });
        await WriteGuarded(name, up);
    }

    /// <summary>"You have control" (as PF): hands control to the other pilot.</summary>
    public void GiveControl() => _client.Send("control_give", new JsonObject());

    /// <summary>The other pilot as the UI names them: "captain" or "first officer".</summary>
    public string PeerWord => _status.SeatSide == "fo" ? "captain" : "first officer";

    /// <summary>What the pilot reads on a control change; open differences are information, not a blocker.</summary>
    private string ControlChangedText(JsonObject body)
    {
        var given = body["kind"]?.GetValue<string>() == "give";
        var text = IsPf
            ? given ? $"The {PeerWord} gave you control. You are pilot flying." : "You took control. You are pilot flying."
            : given ? $"You gave control to the {PeerWord}. You are pilot monitoring." : $"The {PeerWord} took control. You are pilot monitoring.";
        var groups = (body["differences"]?["groups"] as JsonArray ?? []).Select(g => g!.GetValue<string>()).ToList();
        var vars = (body["differences"]?["vars"] as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToList();
        if (groups.Count > 0 || vars.Count > 0)
            text += $" Open differences: {string.Join(", ", groups)}{(vars.Count > 0 ? $" ({string.Join(", ", vars)})" : "")}.";
        return text;
    }

    /// <summary>Sends one stream frame (protocol.md section 9); only while PF. Never logged, never replayed.</summary>
    public bool SendStreamFrame(long frame, IReadOnlyDictionary<string, double> data) =>
        SendStreamFrame(frame, new JsonObject(data.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))));

    /// <summary>Sends one stream frame (protocol.md section 9); only while PF. Never logged, never replayed.</summary>
    public bool SendStreamFrame(long frame, JsonObject data)
    {
        if (!IsPf) return false;
        _client.Send("stream", new JsonObject { ["frame"] = frame, ["data"] = data });
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _snapshotTimer?.Dispose();
        _stop.Cancel();
        _applyQueue.Writer.TryComplete();
        if (_applyLoop is not null) await _applyLoop;
        _sim.ValueChanged -= OnSimValue;
        _sim.StateChanged -= OnSimState;
        _client.MessageReceived -= OnMessage;
        _client.Resumed -= OnResumed;
        _client.StateChanged -= OnServerState;
        lock (_gate)
        {
            foreach (var d in _mirroredDown.Values.Concat(_springHeld.Values)) d.Cap.Dispose();
            foreach (var t in _effectTimers.Values) t.Timer.Dispose();
            _effectTimers.Clear();
            foreach (var t in _parkTimers.Values) t.Dispose();
            _parkTimers.Clear();
            _mirroredDown.Clear();
            _springHeld.Clear();
            _takeoverTimer?.Dispose();
        }
    }
}
