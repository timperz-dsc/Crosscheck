using System.Text.Json.Nodes;

namespace Crosscheck.Bridge.Api;

/// <summary>Something the pilot (UI) should see: desync, overridden, peer lost, control change, ...</summary>
public sealed record BridgeEvent(string Type, string Message, JsonObject? Data, DateTimeOffset At)
{
    public JsonObject ToJson() => new()
    {
        ["type"] = Type,
        ["message"] = Message,
        ["at"] = At.ToString("O"),
        ["data"] = Data?.DeepClone(),
    };
}

public sealed class EventHub
{
    private readonly object _gate = new();
    private readonly List<BridgeEvent> _recent = [];

    public event Action<BridgeEvent>? Published;

    public IReadOnlyList<BridgeEvent> Recent
    {
        get { lock (_gate) return _recent.ToList(); }
    }

    public void Publish(string type, string message, JsonObject? data = null)
    {
        var e = new BridgeEvent(type, message, data, DateTimeOffset.UtcNow);
        lock (_gate)
        {
            _recent.Add(e);
            if (_recent.Count > 500) _recent.RemoveAt(0);
        }
        Published?.Invoke(e);
    }
}

/// <summary>What /status shows. Updated by the runtime, engine and protocol client.</summary>
public sealed class BridgeStatus
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JsonObject> _desyncs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _desyncSince = new(StringComparer.Ordinal);
    /// <summary>Active amber lines from the server (advisory messages), by key.</summary>
    private readonly Dictionary<string, JsonObject> _advisories = new(StringComparer.Ordinal);
    /// <summary>Last pilot activity per var (a local input sent, or the other pilot's input applied here; CRO-34).</summary>
    private readonly Dictionary<string, DateTimeOffset> _activity = new(StringComparer.Ordinal);

    public string? SessionId { get; set; }
    public string? JoinCode { get; set; }
    public string? ClientId { get; set; }
    public string? Seat { get; set; }
    /// <summary>Cockpit seat: captain or fo.</summary>
    public string? SeatSide { get; set; }
    public string? Role { get; set; }
    public string? PfClientId { get; set; }
    public string Server { get; set; } = "disconnected";
    public string Sim { get; set; } = "disconnected";
    public double? RttMs { get; set; }
    public string? PeerState { get; set; }
    public string? PackageVersion { get; set; }
    public string? AppVersion { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];

    public void SetDesync(string var, JsonObject body, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            _desyncs[var] = body;
            _desyncSince.TryAdd(var, at ?? DateTimeOffset.UtcNow);
        }
    }

    /// <summary>A pilot moved or pressed this var (either cockpit): the panel waits 2 s before it is red (CRO-34).</summary>
    public void NoteActivity(string var, DateTimeOffset? at = null)
    {
        lock (_gate) _activity[var] = at ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Last pilot activity per var.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Activity
    {
        get { lock (_gate) return new Dictionary<string, DateTimeOffset>(_activity, StringComparer.Ordinal); }
    }

    /// <summary>Open desyncs with the time this bridge first saw them.</summary>
    public IReadOnlyList<(JsonObject Body, DateTimeOffset Since)> Desyncs
    {
        get { lock (_gate) return _desyncs.Select(d => ((JsonObject)d.Value.DeepClone(), _desyncSince[d.Key])).ToList(); }
    }

    /// <summary>The open desync of one var, or null.</summary>
    public JsonObject? DesyncOf(string var)
    {
        lock (_gate) return _desyncs.TryGetValue(var, out var d) ? (JsonObject)d.DeepClone() : null;
    }

    public void SetAdvisory(string key, JsonObject body)
    {
        lock (_gate) _advisories[key] = (JsonObject)body.DeepClone();
    }

    public void RemoveAdvisory(string key)
    {
        lock (_gate) _advisories.Remove(key);
    }

    /// <summary>After joined/resumed: exactly the advisories the server says are active.</summary>
    public void ResetAdvisories(IEnumerable<JsonObject> active)
    {
        lock (_gate)
        {
            _advisories.Clear();
            foreach (var a in active) _advisories[a["key"]!.GetValue<string>()] = (JsonObject)a.DeepClone();
        }
    }

    public IReadOnlyList<JsonObject> Advisories
    {
        get { lock (_gate) return _advisories.Values.Select(a => (JsonObject)a.DeepClone()).ToList(); }
    }

    /// <summary>Back to "not in a session": forget everything session-specific.</summary>
    public void ResetSession()
    {
        lock (_gate)
        {
            _desyncs.Clear();
            _desyncSince.Clear();
            _advisories.Clear();
            _activity.Clear();
        }
        SessionId = JoinCode = ClientId = Seat = SeatSide = Role = PfClientId = PeerState = null;
        RttMs = null;
        Server = "disconnected";
    }

    public void ClearDesync(string var)
    {
        lock (_gate)
        {
            _desyncs.Remove(var);
            _desyncSince.Remove(var);
        }
    }

    public IReadOnlyCollection<string> OpenDesyncs
    {
        get { lock (_gate) return _desyncs.Keys.ToList(); }
    }

    public JsonObject ToJson(Func<JsonObject>? counters = null)
    {
        var counterValues = counters?.Invoke(); // may update fields (RTT), so first
        lock (_gate)
        {
            return new JsonObject
            {
                ["session_id"] = SessionId,
                ["join_code"] = JoinCode,
                ["client_id"] = ClientId,
                ["seat"] = Seat,
                ["seat_side"] = SeatSide,
                ["role"] = Role,
                ["pf_client_id"] = PfClientId,
                ["server"] = Server,
                ["sim"] = Sim,
                ["rtt_ms"] = RttMs,
                ["peer"] = PeerState,
                ["aircraft"] = new JsonObject { ["package_version"] = PackageVersion, ["app_version"] = AppVersion },
                ["warnings"] = new JsonArray(Warnings.Select(w => (JsonNode)JsonValue.Create(w)!).ToArray()),
                ["open_desyncs"] = new JsonArray(_desyncs.Values.Select(d => (JsonNode)d.DeepClone()).ToArray()),
                ["counters"] = counterValues,
            };
        }
    }
}
