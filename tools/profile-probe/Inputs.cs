// Everything the probe reads from the repo: the draft profile, the review list, the scan and
// the probe deny list. Nothing here touches the sim.

using System.Globalization;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;
using ProfileGen;
using YamlDotNet.RepresentationModel;

namespace ProfileProbe;

sealed record ReviewItem(string Name, string ScanValue, string Kind, string? ProvisionalClass, string Reason);

sealed record DenyRule(string Pattern, string Reason, bool CoverAllowed);

/// <summary>probe-deny.yaml: patterns the automated probe never writes, plus the breaker sample.</summary>
sealed class DenyFile
{
    public required IReadOnlyList<DenyRule> Rules { get; init; }
    public required IReadOnlyList<string> Breakers { get; init; }

    /// <summary>The rule that denies this var, or null. Same rule as tools/profile-gen DenyList.Denies.</summary>
    public DenyRule? Denies(string name) =>
        Rules.FirstOrDefault(r => new ExcludeFilter([r.Pattern]).IsExcluded(name) && !(r.CoverAllowed && name.EndsWith("_Cover", StringComparison.Ordinal)));

    public static DenyFile Load(string path)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path)) yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var rules = new List<DenyRule>();
        foreach (var item in ((YamlSequenceNode)root["deny"]).Children.Cast<YamlMappingNode>())
        {
            string Get(string key) => item.Children.TryGetValue(key, out var v) ? ((YamlScalarNode)v).Value ?? "" : "";
            rules.Add(new DenyRule(Get("pattern"), Get("reason"), Get("cover_allowed") == "true"));
        }
        var breakers = ((YamlSequenceNode)((YamlMappingNode)root["probe_allow"])["breakers"]).Children
            .Select(c => ((YamlScalarNode)c).Value!).ToList();
        if (rules.Count == 0) throw new InvalidDataException("probe-deny.yaml has no deny rules");
        return new DenyFile { Rules = rules, Breakers = breakers };
    }
}

/// <summary>Paths of the files the probe reads and writes.</summary>
sealed class RepoPaths
{
    public required string Root { get; init; }
    public string ProfileDir => Path.Combine(Root, "profiles", "fenix-a320");
    public string Profile => Path.Combine(ProfileDir, "profile.yaml");
    public string Review => Path.Combine(ProfileDir, "draft-review.csv");
    public string Deny => Path.Combine(ProfileDir, "probe-deny.yaml");
    public string Scan => Path.Combine(ProfileDir, "scan", "lvars-2026-11-01.csv");
    /// <summary>Working files (checkpoint, raw results). Gitignored.</summary>
    public string WorkDir => Path.Combine(ProfileDir, ".probe");
    public string Checkpoint => Path.Combine(WorkDir, "checkpoint.json");
    public string Details => Path.Combine(WorkDir, "details.jsonl");
    public string Noisy => Path.Combine(WorkDir, "noisy.json");
    public string Release => Path.Combine(WorkDir, "release");
    public string ResultsCsv => Path.Combine(ProfileDir, "probe-results.csv");
    public string Report => Path.Combine(ProfileDir, "probe-report.md");
    public string Proposed => Path.Combine(ProfileDir, "profile.proposed.yaml");

    public static RepoPaths Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !(Directory.Exists(Path.Combine(dir.FullName, "profiles")) && Directory.Exists(Path.Combine(dir.FullName, "bridge"))))
            dir = dir.Parent;
        return new RepoPaths { Root = dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found (profiles/ and bridge/)") };
    }
}

sealed class Inputs
{
    public required Profile Profile { get; init; }
    public required string ProfileText { get; init; }
    public required IReadOnlyList<ReviewItem> Review { get; init; }
    public required DenyFile Deny { get; init; }
    /// <summary>Fenix-named scan vars that the profile does not exclude, with their scan value.</summary>
    public required IReadOnlyDictionary<string, string> Scan { get; init; }

    public IEnumerable<string> AllNames => Profile.Vars.Select(v => v.Name).Concat(Review.Select(r => r.Name)).Distinct();

    public VarEntry? Entry(string name) => Profile.Vars.FirstOrDefault(v => v.Name == name);

    public bool Known(string name) => Scan.ContainsKey(name) || Profile.Vars.Any(v => v.Name == name);

    public static Inputs Load(RepoPaths paths)
    {
        var load = ProfileLoader.LoadFile(paths.Profile);
        if (!load.IsValid || load.Profile is null) throw new InvalidDataException("profile.yaml is not valid:\n" + string.Join("\n", load.FormatIssues()));
        var exclude = new ExcludeFilter(load.Profile.Exclude);
        var scan = ScanReader.Read(paths.Scan)
            .Where(v => Rules.IsFenix(v.Name) && !exclude.IsExcluded(v.Name))
            .GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
        return new Inputs
        {
            Profile = load.Profile,
            ProfileText = File.ReadAllText(paths.Profile),
            Review = ReadReview(paths.Review),
            Deny = DenyFile.Load(paths.Deny),
            Scan = scan,
        };
    }

    public static List<ReviewItem> ReadReview(string path)
    {
        var rows = new List<ReviewItem>();
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (line.Trim().Length == 0) continue;
            var cols = Csv.Split(line);
            if (cols.Count < 5) continue;
            rows.Add(new ReviewItem(cols[0], cols[1], cols[2], cols[3].Length == 0 ? null : cols[3], cols[4]));
        }
        return rows;
    }
}

static class Csv
{
    public static List<string> Split(string line)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cur.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { result.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(c);
        }
        result.Add(cur.ToString());
        return result;
    }

    public static string Field(string s) =>
        s.IndexOfAny([',', '"', '\n']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    public static string Num(double? d) => d is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : "";
}
