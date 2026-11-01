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

    public string? SessionId { get; set; }
    public string? JoinCode { get; set; }
    public string? ClientId { get; set; }
    public string? Seat { get; set; }
    public string? Role { get; set; }
    public string? PfClientId { get; set; }
    public string Server { get; set; } = "disconnected";
    public string Sim { get; set; } = "disconnected";
    public double? RttMs { get; set; }
    public string? PeerState { get; set; }
    public string? PackageVersion { get; set; }
    public string? AppVersion { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = [];

    public void SetDesync(string var, JsonObject body)
    {
        lock (_gate) _desyncs[var] = body;
    }

    public void ClearDesync(string var)
    {
        lock (_gate) _desyncs.Remove(var);
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
