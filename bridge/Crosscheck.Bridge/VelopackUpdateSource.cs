using Velopack;
using Velopack.Sources;

namespace Crosscheck.Bridge.App;

/// <summary>Updates from the GitHub releases of this repository (public, no token).</summary>
internal sealed class VelopackUpdateSource : IUpdateSource
{
    public const string RepoUrl = "https://github.com/timperz-dsc/Crosscheck";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, null, false));
    private VelopackAsset? _downloaded;

    public bool IsInstalled => _manager.IsInstalled;

    public string? PendingVersion => _manager.UpdatePendingRestart?.Version?.ToString();

    public async Task<string?> CheckAndDownloadAsync(CancellationToken ct)
    {
        var info = await _manager.CheckForUpdatesAsync();
        if (info is null) return null;
        await _manager.DownloadUpdatesAsync(info, null, ct);
        _downloaded = info.TargetFullRelease;
        return _downloaded.Version?.ToString();
    }

    public void ApplyAndRestart() => _manager.ApplyUpdatesAndRestart(_downloaded ?? _manager.UpdatePendingRestart);
}
