using Crosscheck.Profiles;

if (args.Length != 2 || args[0] != "validate")
{
    Console.Error.WriteLine("Usage: validate <profile.yaml>");
    return 2;
}

var file = args[1];
if (!File.Exists(file))
{
    Console.Error.WriteLine($"File not found: {file}");
    return 2;
}

var result = ProfileLoader.LoadFile(file);

foreach (var line in result.FormatIssues())
    Console.WriteLine(line);

if (!result.IsValid)
{
    Console.WriteLine();
    Console.WriteLine($"{result.Issues.Count} problem(s) found. Profile not valid.");
    return 1;
}

var profile = result.Profile!;
var summary = ProfileSummary.Of(profile);

Console.WriteLine($"OK: {profile.Aircraft.Name} ({profile.Aircraft.Id}), format {profile.Format}");
Console.WriteLine($"{summary.VarCount} vars, {summary.DatarefCount} datarefs, {profile.Exclude?.Count ?? 0} exclude patterns");
Console.WriteLine();
Console.WriteLine("By class:");
foreach (var (cls, count) in summary.PerClass.Where(p => p.Value > 0))
    Console.WriteLine($"  {cls.ToString().ToLowerInvariant(),-10}{count,5}");
Console.WriteLine("By group:");
foreach (var (group, count) in summary.PerGroup.Where(p => p.Value > 0))
    Console.WriteLine($"  {group,-16}{count,5}");
return 0;
