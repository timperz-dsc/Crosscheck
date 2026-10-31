// Crosscheck LVar scanner client: lists every L: var in the running sim (via the read-only WASM
// module) and writes profiles/fenix-a320/scan/lvars-<date>.csv plus a summary README.
//
//   LvarScanner [--samples 3] [--interval 2] [--verify 20] [--coverage <profile.yaml>]
//
// --coverage only reports how many of a profile's L: names exist, as a number. No name from that
// file is written anywhere.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

const string ScannerVersion = "0.1.0";
int samples = 3, interval = 2, verify = 20;
string? coverage = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--samples": samples = int.Parse(args[++i]); break;
        case "--interval": interval = int.Parse(args[++i]); break;
        case "--verify": verify = int.Parse(args[++i]); break;
        case "--coverage": coverage = args[++i]; break;
        default: Console.Error.WriteLine($"Unknown argument {args[i]}"); return 1;
    }
}

Scanner scanner;
try { scanner = new Scanner(); }
catch (System.Runtime.InteropServices.COMException) { Console.Error.WriteLine("Could not connect to MSFS (is it running?)."); return 1; }
using var _ = scanner;
Thread.Sleep(300);

var ping = scanner.Ping();
if (ping == null)
{
    Console.Error.WriteLine("The scanner module did not answer. Is crosscheck-lvar-scanner installed in the Community folder and the sim restarted? See tools/lvar-scanner/README.md.");
    return 1;
}
Console.WriteLine($"MSFS {scanner.SimVersion}, module {ping.Value.Version}, {ping.Value.Count} L: vars");

// Several scans: names whose value changes without input are stream candidates.
var scans = new List<List<LVar>>();
for (int s = 0; s < samples; s++)
{
    if (s > 0) Thread.Sleep(TimeSpan.FromSeconds(interval));
    var scan = scanner.Scan();
    Console.WriteLine($"Scan {s + 1}/{samples}: {scan.Count} vars");
    scans.Add(scan);
}
var after = scanner.Ping();

var latest = scans[^1];
var changed = new HashSet<string>(StringComparer.Ordinal);
foreach (var scan in scans.Take(scans.Count - 1))
{
    var values = scan.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Value);
    foreach (var v in latest)
        if (values.TryGetValue(v.Name, out var old) && !old.Equals(v.Value)) changed.Add(v.Name);
}
var sameNames = scans.All(s => s.Select(v => v.Name).ToHashSet().SetEquals(latest.Select(v => v.Name)));

// Spot check: read random vars directly through SimConnect and compare.
var rng = new Random(1);
var spot = latest.Where(v => !changed.Contains(v.Name)).OrderBy(_ => rng.Next()).Take(verify).ToList();
var direct = scanner.ReadDirect(spot.Select(v => v.Name).ToList());
int spotOk = spot.Count(v => direct.TryGetValue(v.Name, out var d) && Math.Abs(d - v.Value) < 1e-9);

// Versions, without reading any binaries.
var fenixPackage = FenixPackageVersion();
var fenixApp = await FenixAppVersion();

var rows = latest
    .GroupBy(v => v.Name).Select(g => g.First())
    .OrderBy(v => v.Name, StringComparer.Ordinal)
    .Select(v =>
    {
        var (cls, reason) = Classifier.Classify(v.Name, changed.Contains(v.Name));
        return (v.Name, v.Value, Prefix: Classifier.Prefix(v.Name), Class: cls, Reason: reason);
    })
    .ToList();

var repo = RepoRoot();
var outDir = Path.Combine(repo, "profiles", "fenix-a320", "scan");
Directory.CreateDirectory(outDir);
var date = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
var csvPath = Path.Combine(outDir, $"lvars-{date}.csv");
var header = new[]
{
    $"# scanned {DateTime.Now:yyyy-MM-dd HH:mm:ss zzz}",
    $"# MSFS {scanner.SimVersion}",
    $"# Fenix package fnx-aircraft-320 {fenixPackage}",
    $"# Fenix app {fenixApp}",
    $"# scanner {ScannerVersion}, module {ping.Value.Version}, {samples} scans {interval} s apart",
    $"# total {rows.Count}",
};
var csv = new StringBuilder();
foreach (var line in header) csv.Append(line).Append('\n');
csv.Append("name,value,prefix,class,class_reason\n");
foreach (var r in rows)
    csv.Append(CultureInfo.InvariantCulture, $"{Csv(r.Name)},{r.Value.ToString("R", CultureInfo.InvariantCulture)},{r.Prefix},{r.Class},{Csv(r.Reason)}\n");
File.WriteAllText(csvPath, csv.ToString());

// Summary
string? coverageText = null; // "x of n (y %)": a count only, never names
if (coverage != null)
{
    var profileNames = File.ReadLines(coverage)
        .Where(l => !l.TrimStart().StartsWith('#'))
        .SelectMany(l => Regex.Matches(l, @"L:([A-Za-z][A-Za-z0-9_]*)").Select(m => m.Groups[1].Value))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var scanned = rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
    int covered = profileNames.Count(scanned.Contains);
    double pct = profileNames.Count == 0 ? 0 : 100.0 * covered / profileNames.Count;
    coverageText = $"{covered} of {profileNames.Count} ({pct.ToString("F1", CultureInfo.InvariantCulture)} %)";
}

var md = new StringBuilder();
md.Append($"# Fenix A320 L: var scan\n\nGenerated by `tools/lvar-scanner` from [{Path.GetFileName(csvPath)}]({Path.GetFileName(csvPath)}).\n\n");
foreach (var line in header) md.Append($"- {line[2..]}\n");
md.Append($"\n## Counts\n\n| prefix | count |\n|---|---|\n");
foreach (var g in rows.GroupBy(r => r.Prefix).OrderBy(g => g.Key == "other").ThenBy(g => g.Key)) md.Append($"| {g.Key} | {g.Count()} |\n");
md.Append($"\n| class (first guess) | count |\n|---|---|\n");
foreach (var g in rows.GroupBy(r => r.Class).OrderByDescending(g => g.Count())) md.Append($"| {g.Key} | {g.Count()} |\n");
var nonFenix = rows.Where(r => !Classifier.IsFenix(r.Name)).ToList();
md.Append($"\n## Not Fenix naming ({nonFenix.Count} vars)\n\nNames that do not match `{Classifier.FenixPattern}`, grouped by leading token. These come from the sim itself and other add-ons and are candidates for a filter.\n\n| group | count | examples |\n|---|---|---|\n");
foreach (var g in nonFenix.GroupBy(r => Classifier.Group(r.Name)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
    md.Append($"| `{g.Key}` | {g.Count()} | {string.Join(", ", g.Take(3).Select(r => $"`{r.Name}`"))} |\n");
md.Append($"\n## Checks\n\n");
md.Append($"- Module count {ping.Value.Count} before and {after?.Count.ToString() ?? "?"} after scanning (read-only: no vars created).\n");
md.Append($"- Same names in all {samples} scans: {(sameNames ? "yes" : "no")}. {changed.Count} vars changed value between scans.\n");
md.Append($"- Direct SimConnect read of {spot.Count} random vars matches the scan: {spotOk}/{spot.Count}.\n");
if (coverageText != null) md.Append($"- Coverage of a third-party community profile: {coverageText} of its L: names found.\n");
File.WriteAllText(Path.Combine(outDir, "README.md"), md.ToString());

Console.WriteLine();
Console.WriteLine($"Wrote {csvPath} ({rows.Count} vars)");
Console.WriteLine($"Fenix package {fenixPackage}, app {fenixApp}");
foreach (var g in rows.GroupBy(r => r.Prefix).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key,-6} {g.Count(),6}");
Console.WriteLine($"  not Fenix naming: {nonFenix.Count}");
Console.WriteLine($"Read-only check: {ping.Value.Count} -> {after?.Count} vars; same names in all scans: {sameNames}; {changed.Count} changed");
Console.WriteLine($"Spot check: {spotOk}/{spot.Count} match a direct SimConnect read");
if (coverageText != null) Console.WriteLine($"Coverage: {coverageText}");
return 0;

static string Csv(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;

static string RepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}

static string FenixPackageVersion()
{
    try
    {
        var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            @"Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt");
        if (!File.Exists(cfg))
            cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft Flight Simulator 2024\UserCfg.opt");
        var line = File.ReadLines(cfg).First(l => l.StartsWith("InstalledPackagesPath"));
        var root = line[(line.IndexOf('"') + 1)..line.LastIndexOf('"')];
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "Community", "fnx-aircraft-320", "manifest.json")));
        return doc.RootElement.GetProperty("package_version").GetString() ?? "unknown";
    }
    catch (Exception e) when (e is IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
    {
        return "unknown";
    }
}

static async Task<string> FenixAppVersion()
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var body = new StringContent("{\"query\":\"query GetSoftwareVersion { settings { softwareVersion } }\"}", Encoding.UTF8, "application/json");
        var resp = await http.PostAsync("http://localhost:8083/graphql", body);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").GetProperty("settings").GetProperty("softwareVersion").GetString() ?? "unknown";
    }
    catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
    {
        return "unknown (EFB backend not reachable)";
    }
}
