using System.Text.Json.Nodes;

namespace Crosscheck.Bridge.Protocol;

/// <summary>What the sync engine needs from the protocol client (tests use an in-memory channel).</summary>
public interface IProtocolChannel
{
    string? ClientId { get; }
    ServerState State { get; }
    int SnapshotIntervalMs { get; }
    void Send(string type, JsonObject body);
    event Action<JsonObject>? MessageReceived;
    /// <summary>Raised after a resume; true = full resync.</summary>
    event Action<bool>? Resumed;
}
