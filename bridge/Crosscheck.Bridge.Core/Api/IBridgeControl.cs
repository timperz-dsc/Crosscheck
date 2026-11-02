using System.Text.Json.Nodes;

namespace Crosscheck.Bridge.Api;

/// <summary>Result of a UI request. Error is a sentence the UI shows as is.</summary>
public sealed record ApiResult(bool Ok, string? Error = null)
{
    /// <summary>join_rejected body when the server refused a join (headless mode reports it).</summary>
    public JsonObject? Rejected { get; init; }

    public static ApiResult Success { get; } = new(true);
    public static ApiResult Fail(string error) => new(false, error);
}

/// <summary>What the local API can ask the bridge to do. Implemented by BridgeRuntime.</summary>
public interface IBridgeControl
{
    JsonObject StatusJson();
    JsonArray Recent();
    ApiResult SetServer(string? address);
    Task<ApiResult> CreateAsync();
    Task<ApiResult> CheckAsync(string code);
    Task<ApiResult> JoinAsync(string code);
    ApiResult CancelPreflight();
    Task<ApiResult> LeaveAsync();
    ApiResult TakeControl(bool confirmPeerLost);
    ApiResult GiveControl();
    Task<ApiResult> PilotSetAsync(string name, double value);
    /// <summary>One click of a toggle button in this pilot's own sim only, not mirrored (toggle_mismatch).</summary>
    Task<ApiResult> PressHereOnlyAsync(string name);
}
