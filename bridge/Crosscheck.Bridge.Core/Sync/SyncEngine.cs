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
    private readonly Dictionary<string, double> _pressCounter = new(StringComparer.Ordinal);
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
    /// <summary>The takeover pushbutton on this pilot's own seat (profile roles.takeover), once the seat is known.</summary>
    private string? _takeoverVar;

    public SyncEngine(Profile profile, ISimAdapter sim, IFenixGraphQl gql, IProtocolChannel client, EventHub hub,
        BridgeLog log, BridgeStatus status, ILogger logger, IClock? clock = null, EngineOptions? options = null)
    {
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

    /// <summary>Only profile vars, and never ignore/stream.</summary>
    public SimPlan Plan
    {
        get
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var v in _profile.Vars.Where(v => Watched.Contains(v.Class)))
            {
                names.Add(v.Name);
                if (v.Display is { } d && _vars.ContainsKey(d)) names.Add(d);
                if (v.Verify?.Lvar is { } e && _vars.ContainsKey(e)) names.Add(e);
            }
            return new SimPlan(names.Order(StringComparer.Ordinal).ToList());
        }
    }

    /// <summary>GraphQL paths to poll: datarefs and verify sources.</summary>
    public IReadOnlyCollection<string> GraphQlPaths =>
        (_profile.Datarefs ?? []).Where(d => d.Class != SyncClass.Ignore).Select(d => d.Path)
            .Concat(_profile.Vars.Select(v => v.Verify?.Graphql).OfType<string>())
            .Distinct().ToList();

    /// <summary>Tests: drop the next n incoming commands as if lost on the way.</summary>
    public void DropIncomingCommands(int n) => Interlocked.Exchange(ref _dropIncoming, n);

    public void Start()
    {
        _sim.ValueChanged += OnSimValue;
        _sim.StateChanged += OnSimState;
        _client.MessageReceived += OnMessage;
        _client.Resumed += OnResumed;
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
        lock (_gate)
        {
            _lastSeen.Clear();
            _baseline.Clear();
        }
    }

    private void OnSimValue(string name, double value)
    {
        if (!_vars.TryGetValue(name, out var spec)) return;
        JsonObject? command = null;
        var takeover = false;
        lock (_gate)
        {
            var hadPrev = _lastSeen.TryGetValue(name, out var prev);
            _lastSeen[name] = value;
            switch (spec.Class)
            {
                case SyncClass.Verified or SyncClass.Analog:
                    if (_echo.TryConsume(name, value)) { LogEcho(name, value); break; }
                    if (hadPrev && _sessionActive && value != prev)
                    {
                        command = NewCommand(spec, value, null);
                        takeover = name == _takeoverVar && prev == 0;
                    }
                    break;
                case SyncClass.Delta:
                    if (!_baseline.TryGetValue(name, out var baseline) || !_sessionActive)
                    {
                        _baseline[name] = value;
                        break;
                    }
                    // Our own write landed (also an in-between value of several quick writes).
                    if (_echo.TryConsume(name, value)) { LogEcho(name, value); break; }
                    if (value == baseline) break;
                    _log.Event("local_delta", new JsonObject { ["var"] = name, ["value"] = value, ["baseline"] = baseline });
                    _baseline[name] = value;
                    command = NewCommand(spec, null, value - baseline);
                    break;
                case SyncClass.Pulse:
                    if (_echo.TryConsume(name, value)) { LogEcho(name, value); break; }
                    if (!hadPrev || !_sessionActive) break;
                    var pressed = spec.Press == PressMode.Counter
                        ? value > prev && ((long)value & 1) == 1
                        : prev == 0 && value != 0;
                    if (pressed)
                    {
                        command = NewCommand(spec, 1, null);
                        takeover = name == _takeoverVar;
                    }
                    break;
                // observe and owned: never sent, only reported in snapshots.
            }
        }
        if (command is not null) _client.Send("command", command);
        // The press is synced like any other var (above). On the PM's own seat it also means
        // "I have control". Only pilot presses get here: our own writes are echoes.
        if (takeover && !IsPf)
        {
            _log.Event("takeover_pressed", new JsonObject { ["var"] = name });
            _hub.Publish("takeover_pressed", "Takeover pushbutton pressed: requesting control.", new JsonObject { ["var"] = name });
            RequestControl();
        }
    }

    private void LogEcho(string name, double value) =>
        _log.Event("echo_suppressed", new JsonObject { ["var"] = name, ["value"] = value });

    /// <summary>Builds a command and remembers it until accepted (lock held).</summary>
    private JsonObject NewCommand(VarEntry spec, double? value, double? delta)
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
        _pendingOwn[id] = (spec.Name, Absolute.Contains(spec.Class), body);
        CommandsSent++;
        return body;
    }

    private void OnResumed(bool fullResync)
    {
        // The server dedupes by id: re-send everything it may not have logged.
        List<JsonObject> resend;
        lock (_gate) resend = _pendingOwn.Values.Select(p => (JsonObject)p.Body.DeepClone()).ToList();
        foreach (var body in resend) _client.Send("command", body);
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
                _client.Send("control_ack", new JsonObject { ["epoch"] = body["epoch"]!.DeepClone() });
                _hub.Publish("control_changed", ControlChangedText(body), body);
                break;
            case "control_handover":
                _hub.Publish("control_handover", "Control is changing hands.", body);
                break;
            case "control_denied":
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
                _hub.Publish("peer_" + state, $"Other pilot ({body["seat"]}): {state}.", body);
                foreach (var w in body["warnings"] as JsonArray ?? [])
                    _hub.Publish("warning", w!["message"]!.GetValue<string>(), w.AsObject());
                break;
            case "snapshot_diff":
                _hub.Publish("snapshot_diff", $"Snapshot differences in {body["group"]}.", body);
                break;
            case "ack":
                if (body["ok"]?.GetValue<bool>() == true) Interlocked.Increment(ref _acksOk);
                else
                    _hub.Publish("ack_failed", $"Command {body["id"]} was not confirmed by the other sim.", body);
                break;
            case "stream":
                if (body["data"] is JsonObject data)
                    _sim.Stream.Apply(data.ToDictionary(p => p.Key, p => p.Value!.GetValue<double>()));
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
        if (_profile.Roles?.Takeover is { } t)
            lock (_gate) _takeoverVar = side == SeatSide.Fo ? t.Fo : t.Captain;
        if (body["join_code"] is JsonValue code) _status.JoinCode = code.GetValue<string>();
        if (body["peer"] is JsonObject peer) _status.PeerState = peer["state"]!.GetValue<string>();
        SetPf(body["pf_client_id"]?.GetValue<string>());
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
            if (!_inFlight.Add(id)) return; // re-send while still being applied: the ack follows
            if (!_vars.TryGetValue(name, out var spec))
            {
                _inFlight.Remove(id);
                SendAck(id, false, null);
                return;
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
            var settle = body["settle_ms"]!.GetValue<int>();
            _applyQueue.Writer.TryWrite(new ApplyJob(id, spec, false, body["value"]?.GetValue<double>(), body["delta"]?.GetValue<double>(), settle, null));
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
            _inFlight.Add(id);
            _applyQueue.Writer.TryWrite(new ApplyJob(id, spec, true, null, null, body["settle_ms"]!.GetValue<int>(), body["target"]!.GetValue<double>()));
        }
    }

    private void SendAck(string id, bool ok, object? observed, bool superseded = false)
    {
        var ack = new JsonObject { ["id"] = id, ["ok"] = ok, ["observed"] = observed switch { double d => d, string s => s, _ => null } };
        if (superseded) ack["superseded"] = true;
        lock (_gate)
        {
            _ackCache[id] = ack;
            _ackOrder.Enqueue(id);
            while (_ackOrder.Count > 5000) _ackCache.Remove(_ackOrder.Dequeue());
            _inFlight.Remove(id);
        }
        _client.Send("ack", (JsonObject)ack.DeepClone());
    }

    // ---------------------------------------------------------------- applying (write, verify, ack)

    private sealed record ApplyJob(string Id, VarEntry Spec, bool IsCorrection, double? Value, double? Delta, int SettleMs, double? Target);

    private sealed record Written(bool Ok, double? StoredTarget, bool StoredSeen, string? Error);

    /// <summary>Writes one at a time, in arrival order (key presses keep their order); verification runs alongside.</summary>
    private async Task ApplyLoop()
    {
        try
        {
            await foreach (var job in _applyQueue.Reader.ReadAllAsync(_stop.Token))
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
                if (!written.Ok)
                {
                    _log.Event("apply_failed", new JsonObject { ["id"] = job.Id, ["var"] = job.Spec.Name, ["error"] = written.Error });
                    SendAck(job.Id, false, _sim.TryGet(job.Spec.Name, out var cur) ? cur : null);
                    continue;
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
        }
        catch (OperationCanceledException)
        {
        }
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
                if (!already)
                {
                    lock (_gate) _echo.Expect(name, target, window);
                    await WriteGuarded(name, target);
                }
                return new Written(true, target, true, null);
            }
            case SyncClass.Delta:
            {
                // Build on our own last write: the sim's read-back may still be a frame behind.
                double counter;
                JsonObject? localTurn = null;
                lock (_gate)
                {
                    if (!_baseline.TryGetValue(name, out counter) && !_sim.TryGet(name, out counter))
                        return new Written(false, null, false, "encoder value unknown");
                    // The pilot turned this knob and that change was not processed yet: send it first,
                    // so writing counter + delta does not overwrite the pilot's click.
                    if (_sessionActive && _sim.IsFresh(name) && _sim.TryGet(name, out var now) && now != counter)
                    {
                        localTurn = NewCommand(spec, null, now - counter);
                        _echo.Expect(name, now, window);
                        counter = now;
                        _baseline[name] = now;
                    }
                }
                if (localTurn is not null) _client.Send("command", localTurn);
                double change;
                if (job.IsCorrection)
                {
                    if (spec.Display is null || !_sim.TryGet(spec.Display, out var display)) return new Written(false, null, false, "display value unknown");
                    change = job.Target!.Value - display;
                    if (Math.Abs(change) < 1e-9) return new Written(true, counter, true, null);
                }
                else
                {
                    change = job.Delta ?? throw new InvalidOperationException("no delta");
                }
                var target = counter + change;
                lock (_gate)
                {
                    _baseline[name] = target; // only new − baseline is a pilot delta
                    _echo.Expect(name, target, window);
                }
                _log.Event("delta_write", new JsonObject { ["id"] = job.Id, ["var"] = name, ["from"] = counter, ["change"] = change, ["target"] = target, ["flushed"] = localTurn is not null });
                await WriteGuarded(name, target);
                return new Written(true, target, true, null);
            }
            case SyncClass.Pulse:
            {
                var counter = spec.Press == PressMode.Counter;
                _sim.TryGet(name, out var c);
                lock (_gate)
                {
                    if (counter && _pressCounter.TryGetValue(name, out var last)) c = Math.Max(c, last);
                    if (counter) _pressCounter[name] = c + 2;
                }
                var press = counter ? c + 1 : 1;
                var release = counter ? c + 2 : 0;
                lock (_gate)
                {
                    _echo.Expect(name, press, window + (spec.PulseMs ?? 150));
                    _echo.Expect(name, release, window + (spec.PulseMs ?? 150));
                }
                await WriteGuarded(name, press);
                await Task.Delay(spec.PulseMs ?? 150);
                var seen = _sim.TryGet(name, out var during) && during == press;
                await WriteGuarded(name, release);
                return new Written(true, release, seen, null);
            }
            default:
                return new Written(false, null, false, $"class {spec.Class} is never written");
        }
    }

    private async Task WriteGuarded(string name, double value)
    {
        try
        {
            await _sim.WriteAsync(name, value);
        }
        catch (SafetyViolationException e)
        {
            lock (_gate) _echo.Forget(name);
            _log.Event("safety_refused", new JsonObject { ["var"] = name, ["value"] = value, ["error"] = e.Message });
            _hub.Publish("safety_refused", e.Message, new JsonObject { ["var"] = name, ["value"] = value });
            throw;
        }
    }

    /// <summary>Step 1: the own var shows the value (1–2 frames). Step 2: the effect source after settle_ms.</summary>
    private async Task VerifyAndAck(ApplyJob job, Written written)
    {
        var spec = job.Spec;
        var name = spec.Name;
        var stored = written.StoredSeen && await WaitFor(name, written.StoredTarget!.Value, _options.StoredTimeoutMs);
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
            SyncClass.Delta => spec.Display is { } disp && _sim.TryGet(disp, out var dv) ? dv : null,
            SyncClass.Pulse => written.StoredSeen ? (job.Value ?? 1) : null,
            _ => _sim.TryGet(name, out var v) ? v : null,
        };
        CommandsApplied++;
        _log.Event("applied", new JsonObject
        {
            ["id"] = job.Id, ["var"] = name, ["correction"] = job.IsCorrection, ["stored"] = stored, ["effect"] = effect?.DeepClone(), ["ok"] = ok,
        });
        SendAck(job.Id, ok, observed);
    }

    private static JsonNode? Node(object? v) => v switch { double d => d, string s => s, _ => null };

    private async Task<bool> WaitFor(string name, double target, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (_sim.TryGet(name, out var v) && Math.Abs(v - target) < 1e-9) return true;
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
            switch (v.Class)
            {
                case SyncClass.Verified or SyncClass.Analog or SyncClass.Owned or SyncClass.Observe:
                    list.Add((v.Name, v.Class.ToString().ToLowerInvariant(), _sim.TryGet(v.Name, out var x) ? x : null));
                    break;
                case SyncClass.Delta:
                    list.Add((v.Name, "delta", v.Display is { } d && _sim.TryGet(d, out var dv) ? dv : null));
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

    public string GroupHash(string group) => CanonicalHash.Compute(GroupValues(group).Select(v => (v.Var, v.Value)));

    private void SendHashes()
    {
        if (_sim.State != SimState.Connected || _client.State != ServerState.Connected) return;
        foreach (var group in Groups)
        {
            _client.Send("snapshot_hash", new JsonObject
            {
                ["group"] = group,
                ["hash"] = GroupHash(group),
                ["applied_seq"] = Interlocked.Read(ref _appliedSeq),
            });
        }
    }

    private void OnSnapshotRequest(JsonObject body)
    {
        var groups = body["groups"] is JsonArray arr ? arr.Select(g => g!.GetValue<string>()).ToList() : Groups.ToList();
        foreach (var group in groups.Where(g => _profile.Groups.Contains(g)))
        {
            var values = GroupValues(group);
            if (values.Count == 0) continue;
            _client.Send("snapshot_values", new JsonObject
            {
                ["request_id"] = body["request_id"]!.DeepClone(),
                ["group"] = group,
                ["values"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject
                {
                    ["var"] = v.Var, ["class"] = v.Class, ["value"] = Node(v.Value),
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

    /// <summary>"You have control" (as PF): hands control to the other pilot.</summary>
    public void GiveControl() => _client.Send("control_give", new JsonObject());

    /// <summary>What the pilot reads on a control change; open differences are information, not a blocker.</summary>
    private string ControlChangedText(JsonObject body)
    {
        var given = body["kind"]?.GetValue<string>() == "give";
        var text = IsPf
            ? given ? "You have control (PF): the other pilot gave it to you." : "You have control (PF)."
            : given ? "You gave control; the other pilot is PF (you are PM)." : "The other pilot took control (you are PM).";
        var groups = (body["differences"]?["groups"] as JsonArray ?? []).Select(g => g!.GetValue<string>()).ToList();
        var vars = (body["differences"]?["vars"] as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToList();
        if (groups.Count > 0 || vars.Count > 0)
            text += $" Open differences: {string.Join(", ", groups)}{(vars.Count > 0 ? $" ({string.Join(", ", vars)})" : "")}.";
        return text;
    }

    /// <summary>Sends one stream frame; only while PF (the field set is a placeholder until CRO-17).</summary>
    public bool SendStreamFrame(long frame, IReadOnlyDictionary<string, double> data)
    {
        if (!IsPf) return false;
        _client.Send("stream", new JsonObject
        {
            ["frame"] = frame,
            ["data"] = new JsonObject(data.Select(p => KeyValuePair.Create(p.Key, (JsonNode?)p.Value))),
        });
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
    }
}
