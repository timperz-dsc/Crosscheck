using System.Text.Json;

namespace Crosscheck.Bridge.Fenix;

public sealed record AircraftVersions(string PackageVersion, string AppVersion, IReadOnlyList<string> Warnings)
{
    public const string Unknown = "unknown";
}

/// <summary>Where the aircraft package version comes from (real: the package manifest).</summary>
public interface IPackageVersionSource
{
    string? Read();
}

/// <summary>
/// Reads fnx-aircraft-320/manifest.json through InstalledPackagesPath in UserCfg.opt, like
/// tools/lvar-scanner does. Only reads; never writes anything in the Community folder.
/// </summary>
public sealed class FenixManifestReader(string packageName = "fnx-aircraft-320") : IPackageVersionSource
{
    public string? Read()
    {
        try
        {
            var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt");
            if (!File.Exists(cfg))
                cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft Flight Simulator 2024\UserCfg.opt");
            var line = File.ReadLines(cfg).First(l => l.StartsWith("InstalledPackagesPath", StringComparison.Ordinal));
            var root = line[(line.IndexOf('"') + 1)..line.LastIndexOf('"')];
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Community", packageName, "manifest.json")));
            var version = doc.RootElement.GetProperty("package_version").GetString();
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException
                                      or KeyNotFoundException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}

/// <summary>A fixed package version (fake mode: the profile's first build).</summary>
public sealed class FixedPackageVersion(string? version) : IPackageVersionSource
{
    public string? Read() => version;
}

/// <summary>
/// Reads the running aircraft build for the preflight. Never substitutes the profile's
/// builds list: a value that cannot be read is sent as "unknown" with a clear warning, and
/// builds is only used to warn when the running version is not one the profile was made for.
/// </summary>
public static class AircraftVersionReader
{
    public static async Task<AircraftVersions> ReadAsync(
        IFenixGraphQl gql, IPackageVersionSource package, IReadOnlyList<string> profileBuilds,
        TimeSpan timeout, TimeSpan retryEvery, CancellationToken ct)
    {
        string? pkg = null, app = null;
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            pkg ??= package.Read();
            app ??= await gql.SoftwareVersionAsync(ct);
            if ((pkg is not null && app is not null) || DateTime.UtcNow + retryEvery > until) break;
            await Task.Delay(retryEvery, ct);
        }

        var warnings = new List<string>();
        if (pkg is null)
            warnings.Add("Aircraft package version could not be read (fnx-aircraft-320/manifest.json). Sent as 'unknown': the preflight cannot compare aircraft builds.");
        else if (profileBuilds.Count > 0 && !profileBuilds.Contains(pkg))
            warnings.Add($"Aircraft package version {pkg} is not one this profile was made for ({string.Join(", ", profileBuilds)}). Some vars may behave differently.");
        if (app is null)
            warnings.Add("Fenix app version could not be read (Fenix EFB backend on localhost:8083 not reachable). Sent as 'unknown': the preflight cannot compare aircraft builds.");
        return new AircraftVersions(pkg ?? AircraftVersions.Unknown, app ?? AircraftVersions.Unknown, warnings);
    }
}
