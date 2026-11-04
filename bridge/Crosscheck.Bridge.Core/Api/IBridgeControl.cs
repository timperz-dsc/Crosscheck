using System.Text.Json.Nodes;

namespace Crosscheck.Bridge.Api;

/// <summary>Result of a UI request. Error is a sentence the UI shows as is.</summary>
public sealed record ApiResult(bool Ok, string? Error = null)
{
    /// <summary>join_rejected body when the server refused a join (headless mode reports it).</summary>
    public JsonObject? Rejected { get; init; }

    /// <summary>The request needs the pilot's confirmation first (e.g. "release_in_air"); the UI asks and sends it again.</summary>
    public string? Confirm { get; init; }

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
    /// <summary>Leave; <paramref name="releaseInAir"/> = the pilot confirmed releasing the aircraft in the air.</summary>
    Task<ApiResult> LeaveAsync(bool releaseInAir);
    /// <summary>"Release aircraft": switches the sim's freezes off (crashed bridge, or holding after the PF was lost).</summary>
    ApiResult ReleaseAircraft();
    ApiResult TakeControl(bool confirmPeerLost);
    ApiResult GiveControl();
    Task<ApiResult> PilotSetAsync(string name, double value);
    /// <summary>One click of a toggle button in this pilot's own sim only, not mirrored (toggle_mismatch).</summary>
    Task<ApiResult> PressHereOnlyAsync(string name);
    /// <summary>One up step for a button stuck pressed in this pilot's own cockpit (stuck_pressed), not mirrored.</summary>
    Task<ApiResult> ReleaseItAsync(string name);
    /// <summary>Installs the downloaded update and restarts. Refused during a session.</summary>
    ApiResult ApplyUpdate();
}
