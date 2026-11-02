using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;

namespace Crosscheck.Bridge;

/// <summary>The installer side of updating (Velopack in the real program, a fake in tests).</summary>
public interface IUpdateSource
{
    /// <summary>False when running from a build folder instead of the installed program: then nothing is checked.</summary>
    bool IsInstalled { get; }

    /// <summary>Version of an update that is already downloaded and waits for a restart, if any.</summary>
    string? PendingVersion { get; }

    /// <summary>Looks for a newer version and downloads it. Returns its version, or null if there is none.</summary>
    Task<string?> CheckAndDownloadAsync(CancellationToken ct);

    /// <summary>Closes the program, installs the downloaded update and starts the program again.</summary>
    void ApplyAndRestart();
}

public enum UpdateState { None, Checking, Ready, Error }

/// <summary>
/// Keeps the program up to date. Checks and downloads in the background; installs only when the
/// pilot asks, or at the next start, and never while a session is going on.
/// </summary>
public sealed class Updater(IUpdateSource source, Func<bool> sessionActive)
{
    private readonly object _gate = new();
    private Task? _running;

    public UpdateState State { get; private set; } = UpdateState.None;
    public string? Version { get; private set; }
    /// <summary>Why the check was started when it was not the routine one: "unsupported_version".</summary>
    public string? Reason { get; private set; }

    public bool Enabled => source.IsInstalled;

    /// <summary>
    /// On start: if an update was downloaded earlier, install it now (no session can be running yet);
    /// otherwise check and download in the background.
    /// </summary>
    public Task StartAsync(CancellationToken ct)
    {
        if (!Enabled) return Task.CompletedTask;
        if (source.PendingVersion is { } pending)
        {
            Version = pending;
            State = UpdateState.Ready;
            if (!sessionActive())
            {
                source.ApplyAndRestart();
                return Task.CompletedTask;
            }
        }
        return CheckAsync(null, ct);
    }

    /// <summary>The server said unsupported_version: look for a newer version right now.</summary>
    public Task CheckNowAsync(string reason, CancellationToken ct = default) => Enabled ? CheckAsync(reason, ct) : Task.CompletedTask;

    private Task CheckAsync(string? reason, CancellationToken ct)
    {
        lock (_gate)
        {
            if (reason is not null) Reason = reason;
            if (_running is { IsCompleted: false } || State == UpdateState.Ready) return _running ?? Task.CompletedTask;
            State = UpdateState.Checking;
            return _running = Run(ct);
        }
    }

    private async Task Run(CancellationToken ct)
    {
        try
        {
            var version = await source.CheckAndDownloadAsync(ct);
            Version = version;
            State = version is null ? UpdateState.None : UpdateState.Ready;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Offline, GitHub down, rate limit: not a reason to bother the pilot. The next start tries again.
            State = UpdateState.Error;
        }
    }

    /// <summary>Installs the downloaded update and restarts. Refused while a session is going on.</summary>
    public ApiResult ApplyAndRestart()
    {
        if (State != UpdateState.Ready) return ApiResult.Fail("There is no update ready.");
        if (sessionActive()) return ApiResult.Fail("Leave the session first. Crosscheck never restarts during a session.");
        source.ApplyAndRestart();
        return ApiResult.Success;
    }

    public JsonObject ToJson() => new()
    {
        ["state"] = State.ToString().ToLowerInvariant(),
        ["version"] = Version,
        ["reason"] = Reason,
    };
}
