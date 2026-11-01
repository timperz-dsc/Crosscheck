// Draft profile generator for the Fenix A320 (CRO-8 step 1). Offline: reads the scan CSV, never touches the sim.
//   dotnet run --project tools/profile-gen [-- --scan <csv>] [--profile <yaml>]
// Adds generated entries to profiles/<aircraft>/profile.yaml between markers, writes probe-deny.yaml,
// draft-report.md and draft-review.csv next to it, then validates the result.
// Results from the sim and owner decisions come from overrides.yaml next to the profile
// (see Overrides.cs): change that file and re-run, never edit the generated block by hand.

using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;
using ProfileGen;

var repo = FindRepoRoot();
var dir = Path.Combine(repo, "profiles", "fenix-a320");
string? scanPath = null, profilePath = null;
for (var i = 0; i < args.Length - 1; i += 2)
{
    if (args[i] == "--scan") scanPath = args[i + 1];
    else if (args[i] == "--profile") profilePath = args[i + 1];
    else { Console.Error.WriteLine($"Unknown option {args[i]}"); return 2; }
}
scanPath ??= Directory.GetFiles(Path.Combine(dir, "scan"), "lvars-*.csv").OrderBy(f => f, StringComparer.Ordinal).Last();
profilePath ??= Path.Combine(dir, "profile.yaml");
var outDir = Path.GetDirectoryName(Path.GetFullPath(profilePath))!;

// 1. The hand-written part must be valid on its own.
var original = File.ReadAllText(profilePath);
var handWritten = ProfileWriter.StripBlock(original);
var hand = ProfileLoader.LoadText(handWritten, Path.GetFileName(profilePath));
if (!hand.IsValid)
{
    foreach (var line in hand.FormatIssues()) Console.Error.WriteLine(line);
    Console.Error.WriteLine("The hand-written part of the profile is not valid, fix it first.");
    return 1;
}
var handProfile = hand.Profile!;
var handNames = handProfile.Vars.Select(v => v.Name).ToHashSet();
var exclude = new ExcludeFilter(handProfile.Exclude);

// 2. Classify every scanned var.
var scan = ScanReader.Read(scanPath);
var entries = new List<Entry>();
var reviews = new List<Review>();
var excluded = 0;
var alreadyThere = 0;
foreach (var v in scan)
{
    if (exclude.IsExcluded(v.Name)) { excluded++; continue; }
    if (handNames.Contains(v.Name)) { alreadyThere++; continue; }
    var (entry, review) = Rules.Classify(v);
    if (entry is not null) entries.Add(entry);
    if (review is not null) reviews.Add(review);
}

// 3. Encoders: the display var must be exactly one N_ var that will exist as observe.
var observeNames = entries.Where(e => e.Class == "observe").Select(e => e.Name)
    .Concat(handProfile.Vars.Where(v => v.Class == SyncClass.Observe).Select(v => v.Name)).ToHashSet();
foreach (var e in entries.Where(e => e.Class == "delta").ToList())
{
    var stem = e.Name[2..];
    var candidates = observeNames.Where(n => n == "N_" + stem || n.StartsWith("N_" + stem + "_")).ToList();
    if (candidates.Contains("N_" + stem)) candidates = ["N_" + stem];
    if (candidates.Count == 1) e.Display = candidates[0];
    else
    {
        entries.Remove(e);
        var why = candidates.Count == 0 ? "no N_ display var found" : $"{candidates.Count} possible N_ display vars ({string.Join(", ", candidates.Take(3))})";
        reviews.Add(new Review(e.Name, e.Value, "class", $"encoder: {why}", "delta"));
    }
}

// 4. Effect pairing (lamps = observe vars in the final profile).
var pairings = EffectPairer.Pair(entries, observeNames);

// 4b. overrides.yaml: results from the sim and owner decisions (CRO-8 step 3). They patch
// generated entries and promote review vars, so they survive every re-run of this tool.
var overrides = OverrideFile.Load(Path.Combine(outDir, "overrides.yaml"));
var scanValues = scan.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Value, StringComparer.Ordinal);
var problems = overrides.Apply(entries, reviews, scanValues, handNames, exclude.IsExcluded);
if (problems.Count > 0)
{
    foreach (var p in problems) Console.Error.WriteLine("overrides.yaml: " + p);
    Console.Error.WriteLine($"{problems.Count} override(s) could not be applied; nothing was written.");
    return 1;
}

// 5. Labels: drop the system token unless that makes two labels in a group equal; if they still clash, name the kind.
var labelled = entries.Select(e => (e, l: Rules.LabelOf(e.Name))).ToList();
var shortClash = labelled.GroupBy(x => (x.e.Group, x.l.Short)).Where(g => g.Count() > 1).SelectMany(g => g).Select(x => x.e).ToHashSet();
foreach (var (e, l) in labelled) e.Label = shortClash.Contains(e) ? l.Long : l.Short;
var longClash = entries.GroupBy(e => (e.Group, e.Label)).Where(g => g.Count() > 1).SelectMany(g => g).ToList();
foreach (var e in longClash) e.Label += e.Name[0] switch { 'E' => " encoder", 'A' => " knob", 'S' when e.Class == "pulse" => " button", 'S' => " switch", _ => "" };

// 6. Write the profile, then validate it.
var groupOrder = handProfile.Groups.ToList();
var ordered = entries.OrderBy(e => e.Class == "ignore" ? 1 : 0).ThenBy(e => groupOrder.IndexOf(e.Group))
    .ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
var block = ProfileWriter.Block(ordered);
File.WriteAllText(profilePath, ProfileWriter.Insert(handWritten, block));

var result = ProfileLoader.LoadFile(profilePath);
foreach (var line in result.FormatIssues().Take(40)) Console.WriteLine(line);
if (!result.IsValid)
{
    Console.WriteLine($"{result.Issues.Count} problem(s): the generated profile is not valid.");
    return 1;
}
var profile = result.Profile!;

// 7. Probe deny list, report, review list.
var fenixNames = scan.Where(v => !exclude.IsExcluded(v.Name)).Select(v => v.Name).ToList();
var profileNames = profile.Vars.Select(v => v.Name).ToList();
var breakers = profileNames.Where(n => n.StartsWith("E_CB_")).ToList();
var sample = DenyList.BreakerSample(breakers);
File.WriteAllText(Path.Combine(outDir, "probe-deny.yaml"), DenyWriter.Write(sample));
File.WriteAllText(Path.Combine(outDir, "draft-review.csv"), ReportWriter.ReviewCsv(reviews));
File.WriteAllText(Path.Combine(outDir, "unsynced.csv"), ReportWriter.UnsyncedCsv(reviews));
File.WriteAllText(Path.Combine(outDir, "draft-report.md"),
    ReportWriter.Report(profile, entries, reviews, pairings, sample, fenixNames, profileNames, scanPath, excluded, alreadyThere, overrides));

var summary = ProfileSummary.Of(profile);
Console.WriteLine($"OK: {entries.Count} generated ({overrides.Overrides.Count} overrides applied), {reviews.Count} on the review list, {excluded} excluded, {alreadyThere} hand-written kept.");
Console.WriteLine($"Profile now has {summary.VarCount} vars. Pairings: {pairings.Count}.");
return 0;

static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "CLAUDE.md"))) return d.FullName;
    throw new DirectoryNotFoundException("Run from inside the repository (CLAUDE.md not found).");
}
