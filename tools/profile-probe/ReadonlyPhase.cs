// Item 7 and supervised c/d/e: no writes at all. Samples every watched var for 30 s with no
// input (vars that change on their own would raise false desync alarms), reads TITLE, the fuel
// datarefs and AIRAC candidates.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Crosscheck.Profiles.Model;

namespace ProfileProbe;

static class ReadonlyPhase
{
    public static readonly string[] FuelPaths =
    [
        "aircraft.fuel.left.amount.kg", "aircraft.fuel.right.amount.kg", "aircraft.fuel.center.amount.kg",
        "aircraft.fuel.ACT1.amount.kg", "aircraft.fuel.ACT2.amount.kg", "aircraft.fuel.total.amount.kg",
        "aircraft.fuel.left.capacity", "aircraft.fuel.left.amount",
    ];

    /// <summary>Guesses for the nav data cycle. GraphQL has no list API, so names can only be tried.</summary>
    public static readonly string[] AiracPaths =
    [
        "aircraft.fms.navdata", "aircraft.fms.navdata.cycle", "aircraft.fms.navData", "aircraft.fms.navData.cycle",
        "aircraft.fms.navDatabase", "aircraft.fms.navdb", "aircraft.fms.database", "aircraft.fms.database.cycle",
        "aircraft.fms.airac", "aircraft.fms.cycle", "aircraft.fms.activeNavDatabase", "aircraft.fms.navDataCycle",
        "aircraft.navdata.cycle", "aircraft.navdata.airac", "aircraft.navdata", "aircraft.airac", "aircraft.airac.cycle",
        "aircraft.navigraph.cycle", "aircraft.navigraph.airac", "aircraft.fmgc.navdata", "aircraft.fmgc.airac",
        "system.navdata.cycle", "navdata.cycle", "airac.cycle", "aircraft.efb.navdata.cycle",
    ];

    public static async Task Run(ProbeSession s, Inputs inputs, ISimvarReader simvars, string noisyPath, CancellationToken ct)
    {
        var t = s.Timing;
        s.Out.WriteLine($"Sampling {s.Watched.Count} vars for {t.NoiseSeconds} s, no input...");
        var start = s.Snapshot();
        var last = new Dictionary<string, double>(start, StringComparer.Ordinal);
        var changes = new Dictionary<string, int>(StringComparer.Ordinal);
        var min = new Dictionary<string, double>(start, StringComparer.Ordinal);
        var max = new Dictionary<string, double>(start, StringComparer.Ordinal);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < t.NoiseSeconds)
        {
            await Task.Delay(100, ct);
            foreach (var n in s.Watched)
            {
                if (s.Value(n) is not { } v) continue;
                if (last.TryGetValue(n, out var old) && Math.Abs(old - v) > 1e-9)
                {
                    changes[n] = changes.GetValueOrDefault(n) + 1;
                    min[n] = Math.Min(min.GetValueOrDefault(n, v), v);
                    max[n] = Math.Max(max.GetValueOrDefault(n, v), v);
                }
                last[n] = v;
            }
            if ((int)sw.Elapsed.TotalSeconds % 5 == 0) s.Safety.Check(null);
        }
        var noisy = changes.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(noisyPath)!);
        File.WriteAllText(noisyPath, JsonSerializer.Serialize(noisy, new JsonSerializerOptions { WriteIndented = true }));
        s.Out.WriteLine($"  {noisy.Count} vars changed on their own (saved, excluded from the effect diffs)");

        string F(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
        string Range(string n) => changes.ContainsKey(n) ? $"{changes[n]} changes, {F(min[n])}..{F(max[n])}" : $"stable at {F(start.GetValueOrDefault(n))}";

        // The 6 noisy review vars, and every observe var of the profile.
        var reviewNoisy = inputs.Review.Where(r => r.Reason.Contains("value changes without input")).Select(r => r.Name);
        foreach (var name in reviewNoisy)
        {
            var moving = changes.ContainsKey(name);
            s.Log.Add(new ProbeResult
            {
                Var = name, Phase = Phase.Readonly, Test = "noise-30s", Result = moving ? "noisy" : "stable",
                ProposedClass = moving ? "ignore" : "observe", Notes = Range(name) + (moving ? "; changes with no input: ignore (an observe would raise false alarms)" : "; did not move: observe"),
            });
        }
        foreach (var v in inputs.Profile.Vars.Where(v => v.Class == SyncClass.Observe && changes.ContainsKey(v.Name)))
            s.Log.Add(new ProbeResult
            {
                Var = v.Name, Phase = Phase.Readonly, Test = "noise-30s", Result = "noisy",
                ProposedClass = "ignore", Notes = Range(v.Name) + "; observe var that changes with no input",
            });

        // e: TITLE
        var title = simvars.Title;
        for (var i = 0; i < 50 && title is null; i++) { await Task.Delay(100, ct); title = simvars.Title; }
        Info(s, "TITLE", "simvar", title ?? "(not read)");

        // d: fuel
        foreach (var p in FuelPaths)
            Info(s, p, "graphql-fuel", Show(await s.Gql.GetAsync(p, ct)));

        // c: AIRAC candidates
        foreach (var p in AiracPaths)
        {
            var v = await s.Gql.GetAsync(p, ct);
            Info(s, p, "graphql-airac", Show(v), v is null ? "not found" : "found");
        }
        Info(s, "softwareVersion", "graphql", await s.Gql.SoftwareVersionAsync(ct) ?? "(none)");
    }

    static string Show(object? v) => v switch
    {
        null => "(empty or not found)",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    static void Info(ProbeSession s, string var, string test, string value, string result = "info")
    {
        s.Log.Add(new ProbeResult { Var = var, Phase = Phase.Readonly, Test = test, Result = result, Notes = value });
        s.Out.WriteLine($"  {var,-48} {value}");
    }
}
