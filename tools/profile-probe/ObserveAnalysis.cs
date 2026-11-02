// CRO-29 part 1: turns the frame stream of `observe` into change rows, per-step summaries and
// the CSV. Pure logic, no sim: tested in ObserveTests.

using System.Globalization;
using System.Text;

namespace ProfileProbe;

/// <param name="Old">null for text rows (MCDU screen) and for the first value of a var.</param>
sealed record ChangeRow(double TMs, long Frame, string Step, string Control, string Var, double? Old, double? New, string Note = "");

sealed class ObserveStep
{
    public required string Name { get; init; }
    public required ObserveControl? Control { get; init; }
    public required StepKind? Kind { get; init; }
    public required double StartMs { get; init; }
    public double EndMs { get; set; } = double.NaN;
    public bool Skipped { get; set; }
}

sealed class ObserveRecorder
{
    readonly IReadOnlyList<string> _vars;
    readonly object _lock = new();
    readonly List<ChangeRow> _rows = [];
    readonly List<ObserveStep> _steps = [];
    double[]? _start, _last;
    double _firstMs = double.NaN, _lastMs = double.NaN, _maxGapMs;
    long _lastFrame;

    public ObserveRecorder(IReadOnlyList<string> vars)
    {
        _vars = vars;
        _steps.Add(new ObserveStep { Name = "setup", Control = null, Kind = null, StartMs = 0 });
    }

    public IReadOnlyList<string> Vars => _vars;
    public long Frames { get { lock (_lock) return _lastFrame; } }
    public double LastMs { get { lock (_lock) return _lastMs; } }
    /// <summary>Longest time between two frames, to spot a lost link or a stutter.</summary>
    public double MaxGapMs { get { lock (_lock) return _maxGapMs; } }
    public IReadOnlyList<ChangeRow> Rows { get { lock (_lock) return _rows.ToList(); } }
    public IReadOnlyList<ObserveStep> Steps { get { lock (_lock) return _steps.ToList(); } }
    ObserveStep Current => _steps[^1];

    public bool TryGet(string var, out double value)
    {
        lock (_lock)
        {
            var i = IndexOf(var);
            value = i >= 0 && _last is not null ? _last[i] : double.NaN;
            return i >= 0 && _last is not null;
        }
    }

    int IndexOf(string var)
    {
        for (var i = 0; i < _vars.Count; i++) if (_vars[i] == var) return i;
        return -1;
    }

    public void BeginStep(string name, ObserveControl? control, StepKind? kind, double tMs)
    {
        lock (_lock)
        {
            Current.EndMs = tMs;
            _steps.Add(new ObserveStep { Name = name, Control = control, Kind = kind, StartMs = tMs });
        }
    }

    public void EndSteps(double tMs) { lock (_lock) Current.EndMs = tMs; }

    public void SkipCurrent() { lock (_lock) Current.Skipped = true; }

    public void OnFrame(ObserveFrame f)
    {
        lock (_lock)
        {
            if (!double.IsNaN(_lastMs)) _maxGapMs = Math.Max(_maxGapMs, f.TMs - _lastMs);
            if (double.IsNaN(_firstMs)) _firstMs = f.TMs;
            _lastMs = f.TMs;
            _lastFrame = f.Frame;
            if (_last is null)
            {
                _start = (double[])f.Values.Clone();
                _last = (double[])f.Values.Clone();
                return;
            }
            for (var i = 0; i < _vars.Count; i++)
            {
                if (f.Values[i] == _last[i]) continue;
                _rows.Add(new ChangeRow(f.TMs, f.Frame, Current.Name, Current.Control?.Id ?? "", _vars[i], _last[i], f.Values[i]));
                _last[i] = f.Values[i];
            }
        }
    }

    /// <summary>A text value changed (MCDU screen); logged with the time it arrived.</summary>
    public void OnText(double tMs, string var, string text)
    {
        lock (_lock) _rows.Add(new ChangeRow(tMs, _lastFrame, Current.Name, Current.Control?.Id ?? "", var, null, null, text));
    }

    /// <summary>The value a var had just before this time (start value plus earlier changes).</summary>
    public double? ValueBefore(string var, double tMs)
    {
        lock (_lock)
        {
            var i = IndexOf(var);
            if (i < 0 || _start is null) return null;
            var v = _start[i];
            foreach (var r in _rows) if (r.Var == var && r.TMs < tMs && r.New is { } n) v = n;
            return v;
        }
    }

    /// <summary>Vars whose value now differs from the first frame: the pilot may want to set them back by hand.</summary>
    public List<(string Var, double Start, double Now)> NotBackToStart()
    {
        lock (_lock)
        {
            if (_start is null || _last is null) return [];
            return Enumerable.Range(0, _vars.Count).Where(i => _start[i] != _last[i]).Select(i => (_vars[i], _start[i], _last[i])).ToList();
        }
    }

    public string Csv()
    {
        lock (_lock)
        {
            var origin = double.IsNaN(_firstMs) ? 0 : _firstMs;
            var sb = new StringBuilder("t_ms,frame,step,control,var,old,new,delta,note\n");
            foreach (var r in _rows.OrderBy(r => r.TMs))
            {
                var delta = r.Old is { } o && r.New is { } n ? ProfileProbe.Csv.Num(n - o) : "";
                sb.Append(string.Join(",",
                    (r.TMs - origin).ToString("0.0", CultureInfo.InvariantCulture), r.Frame.ToString(CultureInfo.InvariantCulture),
                    ProfileProbe.Csv.Field(r.Step), ProfileProbe.Csv.Field(r.Control), ProfileProbe.Csv.Field(r.Var),
                    ProfileProbe.Csv.Num(r.Old), ProfileProbe.Csv.Num(r.New), delta, ProfileProbe.Csv.Field(r.Note))).Append('\n');
            }
            return sb.ToString();
        }
    }
}

static class ObserveAnalysis
{
    static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    static string Ms(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>What the button and its effects did in one step, as plain text lines.</summary>
    public static List<string> Summarize(ObserveStep step, IReadOnlyList<ChangeRow> allRows, double? buttonBefore)
    {
        var lines = new List<string>();
        var c = step.Control!;
        lines.Add($"{step.Name}: {ObserveVars.Instruction(c, step.Kind!.Value)}{(step.Skipped ? "  (SKIPPED)" : "")}");
        var rows = allRows.Where(r => r.Step == step.Name).OrderBy(r => r.TMs).ToList();
        var button = rows.Where(r => r.Var == c.Button && r.New is not null).ToList();
        var start = buttonBefore ?? button.FirstOrDefault()?.Old;

        if (button.Count == 0)
        {
            lines.Add($"  {c.Button}: no change{(start is { } s ? $" (stays {N(s)})" : "")}");
        }
        else
        {
            var seq = new StringBuilder(start is { } s0 ? N(s0) : "?");
            for (var i = 0; i < button.Count; i++)
            {
                var gap = i == 0 ? "" : $" +{Ms(button[i].TMs - button[i - 1].TMs)}ms/{button[i].Frame - button[i - 1].Frame}f";
                seq.Append($" -> {N(button[i].New!.Value)}{gap}");
            }
            lines.Add($"  {c.Button}: {seq}");
            var deltas = button.Select(r => r.New!.Value - (r.Old ?? 0)).ToList();
            var end = button[^1].New!.Value;
            var counts = deltas.All(d => d > 0);
            lines.Add($"  changes {button.Count}, deltas {string.Join(" ", deltas.Select(d => (d > 0 ? "+" : "") + N(d)))}; " +
                      (end == 0 ? "back to 0" : counts ? $"counts up, ends {N(end)} (net {N(end - (start ?? 0))})" : $"ends {N(end)}"));
            var maxJump = deltas.Max(Math.Abs);
            if (maxJump >= 2) lines.Add($"  JUMP: {N(maxJump)} in one frame (two edges inside one frame?)");
            if (button.Count > 1)
            {
                var minGap = button.Zip(button.Skip(1), (a, b) => (Ms: b.TMs - a.TMs, F: b.Frame - a.Frame)).MinBy(g => g.Ms);
                lines.Add($"  shortest gap between two changes: {Ms(minGap.Ms)} ms / {minGap.F} frames");
            }
            // How long each value stayed; the longest one is the held value in a hold step.
            var endMs = double.IsNaN(step.EndMs) ? button[^1].TMs : step.EndMs;
            var held = button.Select((r, i) => (Value: r.New!.Value, Ms: (i + 1 < button.Count ? button[i + 1].TMs : endMs) - r.TMs))
                .Where((_, i) => i + 1 < button.Count).ToList();
            if (held.Count > 0)
            {
                var longest = held.MaxBy(h => h.Ms);
                lines.Add($"  longest value before the last change: {N(longest.Value)} for {Ms(longest.Ms)} ms");
            }
        }

        foreach (var e in c.Effects)
        {
            var eff = rows.Where(r => r.Var == e).ToList();
            if (eff.Count == 0) { lines.Add($"  {e}: no change"); continue; }
            var parts = eff.Select(r =>
            {
                var cause = button.LastOrDefault(b => b.TMs <= r.TMs);
                var lag = cause is null ? "before any button change" : $"+{Ms(r.TMs - cause.TMs)}ms/{r.Frame - cause.Frame}f";
                return $"{N(r.Old ?? double.NaN)}->{N(r.New ?? double.NaN)} ({lag})";
            });
            lines.Add($"  {e}: {string.Join(", ", parts)}");
        }
        if (c.McduScreen)
        {
            var screens = rows.Where(r => r.Var == ObservePhase.McduScreenVar).ToList();
            lines.Add(screens.Count == 0
                ? "  MCDU1 screen: no change seen"
                : $"  MCDU1 screen: {screens.Count} change(s), first " +
                  (button.FirstOrDefault(b => b.TMs <= screens[0].TMs) is { } b0 ? $"+{Ms(screens[0].TMs - b0.TMs)} ms after the first button change" : "before any button change") +
                  $"; last: {screens[^1].Note}");
        }
        var others = rows.Where(r => r.Var != c.Button && !c.Effects.Contains(r.Var) && r.Var != ObservePhase.McduScreenVar)
            .GroupBy(r => r.Var).Select(g => $"{g.Key} {N(g.First().Old ?? double.NaN)}->{string.Join("->", g.Select(r => N(r.New ?? double.NaN)))}").ToList();
        if (others.Count > 0) lines.Add($"  also changed: {string.Join("; ", others)}");
        return lines;
    }

    public static string Report(ObserveRecorder rec)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"frames {rec.Frames}, longest gap between frames {Ms(rec.MaxGapMs)} ms");
        var rows = rec.Rows;
        var steps = rec.Steps.Where(s => s.Control is not null).ToList();
        foreach (var s in steps)
        {
            foreach (var l in Summarize(s, rows, rec.ValueBefore(s.Control!.Button, s.StartMs))) sb.AppendLine(l);
        }
        // Button vars that count up never return to their start value; only lamps and values matter here.
        var buttons = ObserveVars.Controls.Select(c => c.Button).ToHashSet(StringComparer.Ordinal);
        var notBack = rec.NotBackToStart();
        var lamps = notBack.Where(n => !buttons.Contains(n.Var)).ToList();
        sb.AppendLine(lamps.Count == 0
            ? "every observed lamp and value is back at its start value"
            : "NOT back at the start value (put these back by hand in the cockpit; the tool writes nothing):");
        foreach (var (v, a, b) in lamps) sb.AppendLine($"  {v}: {N(a)} -> {N(b)}");
        var counters = notBack.Where(n => buttons.Contains(n.Var)).ToList();
        if (counters.Count > 0) sb.AppendLine("button vars that did not return to their start value: " + string.Join(", ", counters.Select(n => $"{n.Var} {N(n.Start)}->{N(n.Now)}")));
        return sb.ToString();
    }
}
