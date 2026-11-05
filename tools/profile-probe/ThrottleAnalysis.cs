// The `throttle` command's analysis: stats per step, plateaus (detents) in a sweep, left vs right, and a
// proposed range for overrides.yaml. Pure: no I/O, no sim. The proposal is only text in the report;
// nothing here writes the profile.

using System.Globalization;
using System.Text;

namespace ProfileProbe;

/// <summary>One sim frame: the values in the order of the run's var list.</summary>
sealed record ThrottleSample(double TMs, double[] Values);

/// <summary>One recorded step. Samples are added while its window is open.</summary>
sealed class ThrottleRecord
{
    public required ThrottleStep Step { get; init; }
    /// <summary>1 for the first try, 2 after one redo, ...</summary>
    public int Attempt { get; init; } = 1;
    public List<ThrottleSample> Samples { get; } = [];
    /// <summary>The window closed normally (not cut short by Ctrl+C or q).</summary>
    public bool Complete { get; set; }
    /// <summary>Redone later: kept in the CSV, left out of the analysis.</summary>
    public bool Replaced { get; set; }
    public string Name => Attempt > 1 ? $"{Step.Id}-{Attempt}" : Step.Id;
}

sealed record ThrottleStat(int N, double Min, double Max, double Mean, double Median, double StdDev)
{
    public double Spread => Max - Min;
    public static readonly ThrottleStat Empty = new(0, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN);
}

sealed record Plateau(double StartMs, double EndMs, int N, double Median, double Min, double Max)
{
    public double DurationMs => EndMs - StartMs;
}

/// <summary>A sweep plateau and the held detent it matches (null: no detent within reach).</summary>
sealed record PlateauMatch(Plateau Plateau, string? Detent, double? HoldMedian);

sealed record LeftRight(string Detent, double Left, double Right)
{
    public double Diff => Right - Left;
}

sealed record ThrottleProposal(double Min, double Max, IReadOnlyList<(string Detent, double Value, double? GateLeft, double? GateRight)> Detents, IReadOnlyList<string> MissingDetents);

static class ThrottleAnalysis
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The smallest band a plateau may use, even on a perfectly quiet axis.</summary>
    public const double MinBand = 0.005;
    public const double DefaultMinPlateauMs = 250;

    public static ThrottleStat Stats(IEnumerable<double> values)
    {
        var v = values.Where(x => !double.IsNaN(x)).ToArray();
        if (v.Length == 0) return ThrottleStat.Empty;
        var mean = v.Average();
        var sd = Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Length);
        return new ThrottleStat(v.Length, v.Min(), v.Max(), mean, Median(v), sd);
    }

    public static double Median(IEnumerable<double> values)
    {
        var s = values.Order().ToArray();
        if (s.Length == 0) return double.NaN;
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }

    /// <summary>The largest change between two frames: a jump means the value is not continuous there.</summary>
    public static double LargestStep(IReadOnlyList<double> values) =>
        values.Count < 2 ? 0 : values.Zip(values.Skip(1)).Max(p => Math.Abs(p.Second - p.First));

    /// <summary>
    /// Plateau band from the hands-off noise of the input vars: 4 × the largest stddev, at least the largest
    /// spread, at least <see cref="MinBand"/>.
    /// </summary>
    public static double Band(IEnumerable<ThrottleStat> noise)
    {
        var n = noise.Where(s => s.N > 0).ToList();
        return n.Count == 0 ? MinBand : Math.Max(MinBand, Math.Max(4 * n.Max(s => s.StdDev), n.Max(s => s.Spread)));
    }

    /// <summary>
    /// Runs of consecutive samples whose max − min stays within <paramref name="band"/> for at least
    /// <paramref name="minMs"/>. Neighbouring plateaus whose medians are within the band merge.
    /// </summary>
    public static List<Plateau> Plateaus(IReadOnlyList<(double TMs, double V)> s, double band, double minMs = DefaultMinPlateauMs)
    {
        var found = new List<Plateau>();
        var i = 0;
        while (i < s.Count)
        {
            double lo = s[i].V, hi = s[i].V;
            var j = i;
            while (j + 1 < s.Count && Math.Max(hi, s[j + 1].V) - Math.Min(lo, s[j + 1].V) <= band)
            {
                j++;
                lo = Math.Min(lo, s[j].V);
                hi = Math.Max(hi, s[j].V);
            }
            if (s[j].TMs - s[i].TMs >= minMs)
            {
                found.Add(Make(s, i, j));
                i = j + 1;
            }
            else i++;
        }
        // Merge neighbours that are the same level (a short wobble split one plateau in two).
        var merged = new List<Plateau>();
        foreach (var p in found)
        {
            if (merged.Count > 0 && Math.Abs(merged[^1].Median - p.Median) <= band)
            {
                var a = merged[^1];
                var values = s.Where(x => x.TMs >= a.StartMs && x.TMs <= p.EndMs).ToList();
                merged[^1] = new Plateau(a.StartMs, p.EndMs, values.Count, Median(values.Select(x => x.V)), values.Min(x => x.V), values.Max(x => x.V));
            }
            else merged.Add(p);
        }
        return merged;
    }

    static Plateau Make(IReadOnlyList<(double TMs, double V)> s, int i, int j)
    {
        var v = Enumerable.Range(i, j - i + 1).Select(k => s[k].V).ToList();
        return new Plateau(s[i].TMs, s[j].TMs, v.Count, Median(v), v.Min(), v.Max());
    }

    /// <summary>Each plateau with the nearest held detent within <paramref name="reach"/>, or none.</summary>
    public static List<PlateauMatch> MatchDetents(IEnumerable<Plateau> plateaus, IReadOnlyDictionary<string, double> holds, double reach) =>
        plateaus.Select(p =>
        {
            var best = holds.Where(h => !double.IsNaN(h.Value)).OrderBy(h => Math.Abs(h.Value - p.Median)).Select(h => (KeyValuePair<string, double>?)h).FirstOrDefault();
            return best is { } b && Math.Abs(b.Value - p.Median) <= reach ? new PlateauMatch(p, b.Key, b.Value) : new PlateauMatch(p, null, null);
        }).ToList();

    /// <summary>How far a sweep plateau may be from a held detent and still be that detent.</summary>
    public static double Reach(double band) => Math.Max(2 * band, 0.02);

    public static List<LeftRight> LeftVsRight(IReadOnlyDictionary<string, double> left, IReadOnlyDictionary<string, double> right) =>
        ThrottleVars.Detents.Where(d => left.ContainsKey(d) && right.ContainsKey(d)).Select(d => new LeftRight(d, left[d], right[d])).ToList();

    public static double Floor2(double v) => Math.Floor(Math.Round(v * 100, 6)) / 100;
    public static double Ceiling2(double v) => Math.Ceiling(Math.Round(v * 100, 6)) / 100;

    /// <summary>The analysis input: the records still valid (complete, not redone).</summary>
    public static IEnumerable<ThrottleRecord> Valid(IEnumerable<ThrottleRecord> records) => records.Where(r => r.Complete && !r.Replaced && r.Samples.Count > 0);

    public static IReadOnlyList<double> Values(ThrottleRecord r, IReadOnlyList<string> vars, string var)
    {
        var i = IndexOf(vars, var);
        return r.Samples.Select(s => s.Values[i]).ToList();
    }

    static int IndexOf(IReadOnlyList<string> vars, string var)
    {
        for (var i = 0; i < vars.Count; i++) if (vars[i] == var) return i;
        throw new ArgumentException($"{var} is not recorded");
    }

    /// <summary>Per detent, the median of the held value of <paramref name="var"/>.</summary>
    public static Dictionary<string, double> Holds(IEnumerable<ThrottleRecord> records, IReadOnlyList<string> vars, string var) =>
        Valid(records).Where(r => r.Step.Kind == ThrottleStepKind.Hold)
            .ToDictionary(r => r.Step.Detent!, r => Median(Values(r, vars, var)));

    public static ThrottleProposal Proposal(IReadOnlyList<ThrottleRecord> records, IReadOnlyList<string> vars)
    {
        var used = Valid(records).Where(r => r.Step.Kind is ThrottleStepKind.Hold or ThrottleStepKind.Sweep).ToList();
        var all = used.SelectMany(r => ThrottleVars.Levers.SelectMany(l => Values(r, vars, l.Input))).ToList();
        var left = Holds(records, vars, ThrottleVars.Left.Input);
        var right = Holds(records, vars, ThrottleVars.Right.Input);
        double? Gate(string detent, string var)
        {
            var r = Valid(records).FirstOrDefault(x => x.Step.Detent == detent);
            if (r is null) return null;
            var s = Stats(Values(r, vars, var));
            return s.Spread == 0 ? s.Min : null;
        }
        var detents = ThrottleVars.Detents.Where(d => left.ContainsKey(d) && right.ContainsKey(d))
            .Select(d => (d, (left[d] + right[d]) / 2, Gate(d, ThrottleVars.Left.Gate), Gate(d, ThrottleVars.Right.Gate))).ToList();
        var missing = ThrottleVars.Detents.Where(d => !left.ContainsKey(d)).ToList();
        return all.Count == 0
            ? new ThrottleProposal(double.NaN, double.NaN, detents, missing)
            : new ThrottleProposal(Floor2(all.Min()), Ceiling2(all.Max()), detents, missing);
    }

    public sealed record RunInfo(DateTime At, string? Title, double FramesPerSecond, double MaxGapMs, IReadOnlyList<string> MissingSimVars);

    static string F(double v) => double.IsNaN(v) ? "–" : v.ToString("0.####", Inv);
    static string Ms(double v) => v.ToString("0", Inv);

    public static string Report(IReadOnlyList<ThrottleRecord> records, IReadOnlyList<string> vars, RunInfo info)
    {
        var sb = new StringBuilder();
        void L(string line = "") => sb.Append(line).Append('\n');
        var valid = Valid(records).ToList();
        var leverVars = ThrottleVars.LeverVars;

        L("# Fenix A320 thrust lever range (read-only probe)");
        L();
        L($"Run {info.At:yyyy-MM-dd HH:mm:ss}, aircraft title `{info.Title ?? "?"}`, `profile-probe throttle` (flight-1 diagnosis item 7).");
        L("Read only: nothing was written to the sim. Setup checked before every step: on the ground, parking brake set,");
        L("ENG MASTERs off, no engine running, external power.");
        L();
        L($"About {info.FramesPerSecond:0} frames per second; longest gap between two frames {Ms(info.MaxGapMs)} ms.");
        if (info.MissingSimVars.Count > 0)
            L($"Sim vars the sim never sent (rejected name, column is 0, ignore them): {string.Join(", ", info.MissingSimVars.Select(v => $"`{v}`"))}.");
        var notDone = ThrottleVars.Steps.Where(s => !valid.Any(r => r.Step.Id == s.Id)).Select(s => s.Id).ToList();
        if (notDone.Count > 0) L($"Steps not recorded (skipped, stopped or cut short): {string.Join(", ", notDone)}.");
        L();

        // --- noise
        var noise = valid.FirstOrDefault(r => r.Step.Kind == ThrottleStepKind.Noise);
        var noiseStats = noise is null ? [] : ThrottleVars.Levers.Select(l => Stats(Values(noise, vars, l.Input))).ToList();
        var band = Band(noiseStats);
        L("## Noise (hands off, levers at IDLE)");
        L();
        if (noise is null) L($"No noise step recorded; plateau band uses the minimum {F(MinBand)}.");
        else
        {
            L($"{noise.Samples.Count} frames over {Ms(Duration(noise))} ms.");
            L();
            StatTable(noise);
            L($"Plateau band (4 × the largest input stddev, at least its spread, at least {F(MinBand)}): **{F(band)}**.");
        }
        L();

        // --- detents
        var leftHolds = Holds(records, vars, ThrottleVars.Left.Input);
        var rightHolds = Holds(records, vars, ThrottleVars.Right.Input);
        L("## Detents (both levers held, 2 s each)");
        L();
        L("| Detent | left INPUT median | right INPUT median | right − left | left GATE | right GATE | left spread | right spread |");
        L("|---|---|---|---|---|---|---|---|");
        foreach (var d in ThrottleVars.Detents)
        {
            var r = valid.FirstOrDefault(x => x.Step.Detent == d);
            if (r is null) { L($"| {ThrottleVars.Label(d)} | not recorded | | | | | | |"); continue; }
            var sl = Stats(Values(r, vars, ThrottleVars.Left.Input));
            var sr = Stats(Values(r, vars, ThrottleVars.Right.Input));
            var gl = Stats(Values(r, vars, ThrottleVars.Left.Gate));
            var gr = Stats(Values(r, vars, ThrottleVars.Right.Gate));
            L($"| {ThrottleVars.Label(d)} | {F(sl.Median)} | {F(sr.Median)} | {F(sr.Median - sl.Median)} | {GateText(gl)} | {GateText(gr)} | {F(sl.Spread)} | {F(sr.Spread)} |");
        }
        L();
        var lr = LeftVsRight(leftHolds, rightHolds);
        if (lr.Count > 0)
        {
            var worst = lr.MaxBy(x => Math.Abs(x.Diff))!;
            L(Math.Abs(worst.Diff) <= band
                ? $"Left and right agree within the noise band ({F(band)}) at every recorded detent."
                : $"Left and right differ: largest difference {F(worst.Diff)} at {ThrottleVars.Label(worst.Detent)} (band {F(band)}).");
            L();
        }
        L("Every lever var per detent:");
        L();
        foreach (var d in ThrottleVars.Detents)
        {
            var r = valid.FirstOrDefault(x => x.Step.Detent == d);
            if (r is null) continue;
            L($"### {ThrottleVars.Label(d)} ({r.Samples.Count} frames)");
            L();
            StatTable(r);
        }

        // --- full range
        L("## Full range (all holds and sweeps)");
        L();
        L("| Var | min | max |");
        L("|---|---|---|");
        var travel = valid.Where(r => r.Step.Kind is ThrottleStepKind.Hold or ThrottleStepKind.Sweep).ToList();
        foreach (var v in leverVars)
        {
            var s = Stats(travel.SelectMany(r => Values(r, vars, v)));
            L($"| `{v}` | {F(s.Min)} | {F(s.Max)} |");
        }
        L();

        // --- sweeps
        L("## Sweeps (one lever, MAX REV → TOGA)");
        L();
        foreach (var lever in ThrottleVars.Levers)
        {
            var r = valid.FirstOrDefault(x => x.Step.Kind == ThrottleStepKind.Sweep && x.Step.Lever == lever);
            L($"### {lever.Name} lever");
            L();
            if (r is null) { L("Not recorded."); L(); continue; }
            var input = Values(r, vars, lever.Input);
            var s = Stats(input);
            var other = ThrottleVars.Levers.Single(l => l != lever);
            var os = Stats(Values(r, vars, other.Input));
            L($"{r.Samples.Count} frames over {Ms(Duration(r))} ms; `{lever.Input}` {F(s.Min)} … {F(s.Max)}; largest change between two frames " +
              $"{F(LargestStep(input))}; the other lever (`{other.Input}`) moved {F(os.Spread)}.");
            L();
            var series = r.Samples.Select((x, k) => (x.TMs, input[k])).ToList();
            var holds = lever == ThrottleVars.Left ? leftHolds : rightHolds;
            var matches = MatchDetents(Plateaus(series, band), holds, Reach(band));
            if (matches.Count == 0) L($"No plateau of at least {DefaultMinPlateauMs:0} ms within the band {F(band)}: the value moves continuously.");
            else
            {
                L($"Plateaus of at least {DefaultMinPlateauMs:0} ms within the band {F(band)} (a detent shows as a plateau even on a steady sweep):");
                L();
                L("| from ms | to ms | frames | median | min | max | matches |");
                L("|---|---|---|---|---|---|---|");
                var t0 = r.Samples[0].TMs;
                foreach (var m in matches)
                    L($"| {Ms(m.Plateau.StartMs - t0)} | {Ms(m.Plateau.EndMs - t0)} | {m.Plateau.N} | {F(m.Plateau.Median)} | {F(m.Plateau.Min)} | {F(m.Plateau.Max)} | " +
                      (m.Detent is { } d ? $"{ThrottleVars.Label(d)} (held {F(m.HoldMedian!.Value)})" : "no held detent (extra plateau)") + " |");
                var notSeen = holds.Keys.Where(d => matches.All(m => m.Detent != d)).Select(ThrottleVars.Label).ToList();
                if (notSeen.Count > 0) { L(); L($"Held detents with no plateau in the sweep: {string.Join(", ", notSeen)}."); }
            }
            L();
        }

        // --- proposal
        var p = Proposal(records, vars);
        L("## Proposal for overrides.yaml (not applied: to be confirmed by the owner)");
        L();
        if (double.IsNaN(p.Min)) { L("Nothing recorded; no proposal."); return sb.ToString(); }
        if (p.MissingDetents.Count > 0) L($"Detents not recorded, so not in the list: {string.Join(", ", p.MissingDetents.Select(ThrottleVars.Label))}.");
        var detentText = string.Join(", ", p.Detents.Select(d => $"{ThrottleVars.Label(d.Detent)} {F(d.Value)}"));
        var gateText = string.Join(", ", p.Detents.Where(d => d.GateLeft is not null && d.GateLeft == d.GateRight).Select(d => $"{ThrottleVars.Label(d.Detent)} {F(d.GateLeft!.Value)}"));
        L($"Range = lowest and highest INPUT value of both levers over every hold and sweep, rounded outward to 0.01. " +
          $"Detents = mean of the left and right hold medians. Noise band {F(band)}.");
        L();
        L("```yaml");
        foreach (var lever in ThrottleVars.Levers)
        {
            L($"  - name: {lever.Input}");
            L("    class: stream");
            L("    tolerance: 0.2");
            L($"    range: [{F(p.Min)}, {F(p.Max)}]");
            L($"    note: \"thrust lever: the PF streams it (CRO-13); detents {detentText}\"");
            L($"    evidence: \"read-only throttle probe {info.At:yyyy-MM-dd}: holds {detentText}; full travel {F(p.Min)} … {F(p.Max)} (both levers, holds and sweeps); " +
              $"noise band {F(band)}{(gateText.Length > 0 ? $"; GATE {gateText}" : "")}; owner confirmation pending\"");
        }
        L("```");
        return sb.ToString();

        void StatTable(ThrottleRecord r)
        {
            L("| Var | n | min | max | mean | median | stddev |");
            L("|---|---|---|---|---|---|---|");
            foreach (var v in leverVars)
            {
                var s = Stats(Values(r, vars, v));
                L($"| `{v}` | {s.N} | {F(s.Min)} | {F(s.Max)} | {F(s.Mean)} | {F(s.Median)} | {F(s.StdDev)} |");
            }
            L();
        }
    }

    static string GateText(ThrottleStat s) => s.N == 0 ? "–" : s.Spread == 0 ? F(s.Min) : $"{F(s.Min)} … {F(s.Max)}";

    static double Duration(ThrottleRecord r) => r.Samples.Count < 2 ? 0 : r.Samples[^1].TMs - r.Samples[0].TMs;

    /// <summary>Every sample of every record (redone and cut-short ones too), one row per frame.</summary>
    public static string Csv(IEnumerable<ThrottleRecord> records, IReadOnlyList<string> vars)
    {
        var sb = new StringBuilder();
        sb.Append("step,complete,t_ms,").Append(string.Join(",", vars.Select(v => v.Contains(',') ? $"\"{v}\"" : v))).Append('\n');
        foreach (var r in records)
            foreach (var s in r.Samples)
                sb.Append(r.Name).Append(',').Append(r.Complete && !r.Replaced ? "1" : "0").Append(',').Append(s.TMs.ToString("0.0", Inv)).Append(',')
                  .Append(string.Join(",", s.Values.Select(v => v.ToString("0.######", Inv)))).Append('\n');
        return sb.ToString();
    }
}
