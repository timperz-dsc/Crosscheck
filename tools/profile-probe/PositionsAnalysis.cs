// The `positions` command: turns the change rows of one step into "what kind of control is this,
// and which values does it have". Pure logic, no sim: tested in PositionsTests.

using System.Globalization;
using System.Text;

namespace ProfileProbe;

enum PositionClass { None, Positions, PushButton, Analog, Mixed }

/// <param name="Values">Every distinct value seen in the step, the start value included, sorted.</param>
/// <param name="Sequence">The value after each change, in order, starting with the start value.</param>
sealed record PositionResult(
    string Step, string Control, string Var, PositionClass Class, IReadOnlyList<double> Values, double Start, double End,
    IReadOnlyList<double> Sequence, IReadOnlyList<string> Notes)
{
    public bool BackToStart => End == Start;
}

static class PositionsAnalysis
{
    /// <summary>More distinct values than this, or any fraction, is a stepless knob.</summary>
    public const int MaxPositions = 12;

    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Ms(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Classifies one var in one step. Null if the var was not read.</summary>
    public static PositionResult? Classify(ObserveStep step, string var, IReadOnlyList<ChangeRow> allRows, double? before, PositionKind kind = PositionKind.Click)
    {
        var rows = allRows.Where(r => r.Step == step.Name && r.Var == var && r.New is not null).OrderBy(r => r.TMs).ToList();
        var start = before ?? rows.FirstOrDefault()?.Old;
        if (start is not { } s0) return null;
        var seq = rows.Select(r => r.New!.Value).Prepend(s0).ToList();
        var values = seq.Distinct().Order().ToList();
        var end = seq[^1];
        var notes = new List<string>();
        if (rows.Count == 0)
            return new PositionResult(step.Name, step.Control!.Id, var, PositionClass.None, values, s0, end, seq, ["no change"]);

        var deltas = seq.Zip(seq.Skip(1), (a, b) => b - a).ToList();
        PositionClass cls;
        if (values.Any(v => v != Math.Round(v)) || values.Count > MaxPositions)
        {
            cls = PositionClass.Analog;
            notes.Add($"range {N(values[0])}..{N(values[^1])} ({values.Count} values)");
        }
        else if (rows.Count == 1)
        {
            // A counter button always gives two edges per click; one change is a latch, or a switch not moved back.
            cls = PositionClass.Positions;
            notes.Add("one change only: a latching button (two states), or the control was not moved back");
        }
        else if (deltas.All(d => d > 0))
        {
            // Counts up: a Fenix push button (+1 press, +1 release; odd = down).
            cls = PositionClass.PushButton;
            if (deltas.Any(d => d != 1)) notes.Add($"a jump of {N(deltas.Max())} in one frame (two edges inside one frame?)");
            notes.Add(end % 2 == 0 ? $"{rows.Count} edges, ends up (even)" : $"{rows.Count} edges, ends DOWN (odd): still held?");
            if (kind == PositionKind.Move)
                notes.Add("this was a move step: if it is a selector, it was not moved back (r = redo)");
        }
        else if (deltas.Any(d => d > 0) && deltas.Any(d => d < 0))
        {
            cls = PositionClass.Positions;
            var ints = values.Select(v => (long)v).ToList();
            var missing = Enumerable.Range(0, (int)(ints[^1] - ints[0] + 1)).Select(i => ints[0] + i).Except(ints).ToList();
            if (missing.Count > 0) notes.Add($"gap: {string.Join(", ", missing)} not seen");
            var jumps = deltas.Where(d => Math.Abs(d) > 1).ToList();
            if (jumps.Count > 0) notes.Add($"{jumps.Count} jump(s) of more than one position in one frame (a position may have been skipped)");
            if (end != s0) notes.Add($"did not end where it started ({N(s0)} -> {N(end)})");
            // How long each value was held, to tell a spring-loaded position from a latching one.
            var stepEnd = double.IsNaN(step.EndMs) ? rows[^1].TMs : step.EndMs;
            var dwell = new Dictionary<double, double>();
            for (var i = 0; i < rows.Count; i++)
            {
                var until = i + 1 < rows.Count ? rows[i + 1].TMs : stepEnd;
                var v = rows[i].New!.Value;
                dwell[v] = Math.Max(dwell.GetValueOrDefault(v), until - rows[i].TMs);
            }
            var held = values.Where(v => v != s0 && dwell.ContainsKey(v)).Select(v => $"{N(v)} {Ms(dwell[v])} ms");
            notes.Add("longest hold: " + string.Join(", ", held));
        }
        else
        {
            // Only decreases (a binding that writes 0/1, or a reset): not enough to decide.
            cls = PositionClass.Mixed;
            notes.Add("only went down; not enough to decide");
        }
        return new PositionResult(step.Name, step.Control!.Id, var, cls, values, s0, end, seq, notes);
    }

    /// <summary>Every var of the control that was read in this step; vars without change only if they are the main var.</summary>
    public static List<PositionResult> Results(ObserveStep step, PositionControl c, ObserveRecorder rec)
    {
        var rows = rec.Rows;
        var list = new List<PositionResult>();
        foreach (var v in c.Vars)
        {
            var r = Classify(step, v, rows, rec.ValueBefore(v, step.StartMs), c.Kind);
            if (r is null) continue;
            if (r.Class == PositionClass.None && v != c.Var) continue;
            list.Add(r);
        }
        return list;
    }

    public static string Verdict(PositionResult r) => r.Class switch
    {
        PositionClass.None => "no change",
        PositionClass.Positions => $"POSITIONS {string.Join(", ", r.Values.Select(N))}",
        PositionClass.PushButton => "PUSH BUTTON (counts +1/+1)",
        PositionClass.Analog => $"ANALOG {N(r.Values[0])}..{N(r.Values[^1])}",
        _ => "UNCLEAR",
    };

    /// <summary>What the step showed, as plain text lines (first line: the instruction).</summary>
    public static List<string> Summarize(ObserveStep step, PositionControl c, ObserveRecorder rec)
    {
        var lines = new List<string> { $"{step.Name}: {PositionsVars.Instruction(c)}{(step.Skipped ? "  (SKIPPED)" : "")}" };
        var results = Results(step, c, rec);
        foreach (var r in results)
        {
            var seq = r.Sequence.Count > 24 ? string.Join(" -> ", r.Sequence.Take(24).Select(N)) + " ..." : string.Join(" -> ", r.Sequence.Select(N));
            lines.Add($"  {r.Var}: {Verdict(r)}   [{seq}]");
            foreach (var n in r.Notes.Where(n => n != "no change")) lines.Add($"    {n}");
        }
        if (results.All(r => r.Class == PositionClass.None))
            lines.Add("  nothing moved: wrong control, or the click was not seen (r = redo, s = skip)");
        else if (results.FirstOrDefault(r => r.Var == c.Var) is { Class: PositionClass.None })
            lines.Add($"  note: {c.Var} did not move, but a neighbour did (see above)");

        var rows = rec.Rows.Where(r => r.Step == step.Name).ToList();
        foreach (var e in c.Effects ?? [])
        {
            var eff = rows.Where(r => r.Var == e && r.New is not null).ToList();
            lines.Add(eff.Count == 0 ? $"  {e}: no change" : $"  {e}: {N(eff[0].Old ?? double.NaN)} -> {string.Join(" -> ", eff.Take(12).Select(r => N(r.New!.Value)))}");
        }
        var mine = c.Watched.ToHashSet(StringComparer.Ordinal);
        var others = rows.Where(r => !mine.Contains(r.Var) && r.New is not null).Select(r => r.Var).Distinct().ToList();
        if (others.Count > 0)
            lines.Add($"  also changed: {string.Join(", ", others.Take(8))}{(others.Count > 8 ? $" (+{others.Count - 8})" : "")}");
        return lines;
    }

    /// <summary>The last attempt of each control that was not skipped or redone.</summary>
    public static List<(ObserveStep Step, PositionControl Control)> Valid(ObserveRecorder rec, IEnumerable<PositionControl> controls)
    {
        var byId = controls.ToDictionary(c => c.Id);
        return rec.Steps.Where(s => s.Control is not null && !s.Skipped && byId.ContainsKey(s.Control.Id))
            .GroupBy(s => s.Control!.Id).Select(g => (g.Last(), byId[g.Key])).ToList();
    }

    public static string ResultsCsv(ObserveRecorder rec, IReadOnlyList<PositionControl> controls)
    {
        static string F(string s) => Csv.Field(s);
        var sb = new StringBuilder("control,label,kind,var,class,values,start,end,changes,back_to_start,notes\n");
        var valid = Valid(rec, controls).ToDictionary(v => v.Control.Id, v => v.Step);
        foreach (var c in controls)
        {
            if (!valid.TryGetValue(c.Id, out var step))
            {
                var skipped = rec.Steps.Any(s => s.Control?.Id == c.Id);
                sb.Append(string.Join(",", F(c.Id), F(c.Label), c.Kind, F(c.Var), skipped ? "skipped" : "not-reached", "", "", "", "", "", "")).Append('\n');
                continue;
            }
            foreach (var r in Results(step, c, rec))
                sb.Append(string.Join(",", F(c.Id), F(c.Label), c.Kind, F(r.Var), r.Class.ToString().ToLowerInvariant(),
                    F(string.Join(" ", r.Values.Select(N))), N(r.Start), N(r.End), (r.Sequence.Count - 1).ToString(CultureInfo.InvariantCulture),
                    r.BackToStart ? "yes" : "no", F(string.Join("; ", r.Notes)))).Append('\n');
        }
        return sb.ToString();
    }

    public static string Report(ObserveRecorder rec, IReadOnlyList<PositionControl> controls)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"frames {rec.Frames}, longest gap between frames {Ms(rec.MaxGapMs)} ms");
        var valid = Valid(rec, controls);
        foreach (var (step, c) in valid)
            foreach (var l in Summarize(step, c, rec)) sb.AppendLine(l);
        var done = valid.Select(v => v.Control.Id).ToHashSet();
        var open = controls.Where(c => !done.Contains(c.Id)).Select(c => c.Id).ToList();
        if (open.Count > 0) sb.AppendLine($"not done (skipped or not reached; stay for CRO-24): {string.Join(" ", open)}");
        var notBack = rec.NotBackToStart();
        sb.AppendLine(notBack.Count == 0
            ? "every watched var is back at its start value"
            : "NOT back at the start value (push button counters never are; put switches and lamps back by hand if you like; the tool writes nothing):");
        foreach (var (v, a, b) in notBack) sb.AppendLine($"  {v}: {N(a)} -> {N(b)}");
        return sb.ToString();
    }
}
