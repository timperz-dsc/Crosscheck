using System.Reflection;
using Crosscheck.Bridge.Sim;
using ProfileProbe;

namespace ProfileProbe.Tests;

/// <summary>Emits a frame every few ms from values the test sets.</summary>
sealed class FakeObserveSource : IObserveSource
{
    readonly IReadOnlyList<string> _vars;
    readonly double[] _values;
    readonly object _lock = new();
    readonly CancellationTokenSource _stop = new();
    long _frame;
    Task? _loop;

    public event Action<ObserveFrame>? FrameReceived;

    public FakeObserveSource(IReadOnlyList<string> vars)
    {
        _vars = vars;
        _values = new double[vars.Count];
        Set(SafetyVars.ParkingBrake, 1);
        foreach (var p in SafetyVars.Power) Set(p, 1);
    }

    public void Set(string var, double value) { lock (_lock) _values[IndexOf(var)] = value; }
    int IndexOf(string var) => _vars.ToList().IndexOf(var) is var i and >= 0 ? i : throw new ArgumentException(var);

    public Task StartAsync(CancellationToken ct)
    {
        _loop = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                double[] copy;
                lock (_lock) copy = (double[])_values.Clone();
                FrameReceived?.Invoke(new ObserveFrame(++_frame, ObserveClock.NowMs, copy));
                try { await Task.Delay(5, _stop.Token); } catch (OperationCanceledException) { return; }
            }
        });
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null) await _loop;
    }
}

public class ObserveTests
{
    static readonly string[] ObserveFiles =
        ["ObserveReader.cs", "ObservePhase.cs", "ObserveVars.cs", "ObserveAnalysis.cs", "PositionsPhase.cs", "PositionsVars.cs", "PositionsAnalysis.cs", "SimVars.cs",
         "ThrottleVars.cs", "ThrottlePhase.cs", "ThrottleAnalysis.cs"];

    [Fact]
    public void Observe_sources_contain_no_write_calls()
    {
        var dir = Path.Combine(RepoPaths.Find().Root, "tools", "profile-probe");
        string[] forbidden =
        [
            "SetDataOnSimObject", "TransmitClientEvent", "MapClientEventToSimEvent", "SetClientData", "SetInputGroup",
            "SimConnectAdapter", "ISimAdapter", "WriteAsync", "mutation",
        ];
        foreach (var file in ObserveFiles)
        {
            var text = File.ReadAllText(Path.Combine(dir, file));
            foreach (var word in forbidden)
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{file} contains {word}");
        }
    }

    [Fact]
    public void Observe_types_have_no_way_to_write()
    {
        var allowed = new[] { "FrameReceived", "add_FrameReceived", "remove_FrameReceived", "StartAsync", "DisposeAsync" };
        foreach (var m in typeof(IObserveSource).GetMembers().Concat(typeof(IAsyncDisposable).GetMembers()))
            Assert.Contains(m.Name, allowed);
        var ctor = typeof(ObservePhase).GetConstructors().Single();
        Assert.All(ctor.GetParameters(), p => Assert.False(typeof(ISimAdapter).IsAssignableFrom(p.ParameterType), p.Name));
        var throttleCtor = typeof(ThrottlePhase).GetConstructors().Single();
        Assert.All(throttleCtor.GetParameters(), p => Assert.False(typeof(ISimAdapter).IsAssignableFrom(p.ParameterType), p.Name));
        var positionsCtor = typeof(PositionsPhase).GetConstructors().Single();
        Assert.All(positionsCtor.GetParameters(), p => Assert.False(typeof(ISimAdapter).IsAssignableFrom(p.ParameterType), p.Name));
        foreach (var t in new[] { typeof(ObservePhase), typeof(PositionsPhase), typeof(ThrottlePhase), typeof(ThrottleSampler), typeof(ObserveReader), typeof(ObserveRecorder) })
            Assert.All(t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public),
                f => Assert.False(typeof(ISimAdapter).IsAssignableFrom(f.FieldType), $"{t.Name}.{f.Name}"));
    }

    [Fact]
    public void Every_observed_var_exists_in_the_scan_and_fits_the_reader()
    {
        var scan = File.ReadLines(RepoPaths.Find().Scan).Where(l => !l.StartsWith('#')).Select(l => l.Split(',')[0]).ToHashSet(StringComparer.Ordinal);
        Assert.All(ObserveVars.All, v => Assert.Contains(v, scan));
        Assert.True(ObserveVars.All.Count <= ObserveReader.Slots);
        Assert.Equal(ObserveVars.Controls.Length, ObserveVars.Controls.Select(c => c.Id).Distinct().Count());
        // never the fire pushbuttons, agents or engine masters
        Assert.DoesNotContain(ObserveVars.Controls, c => c.Button is "S_OH_FIRE_ENG1_BUTTON" or "S_OH_FIRE_ENG2_BUTTON" || c.Button.Contains("AGENT") || c.Button.StartsWith("S_ENG_MASTER"));
    }

    // --- analysis on scripted frame streams ---

    static readonly ObserveControl Fd = ObserveVars.Controls.Single(c => c.Id == "FD1");
    static readonly string[] Vars = [Fd.Button, .. Fd.Effects, "S_OTHER"];

    sealed class Script
    {
        public readonly ObserveRecorder Rec = new(Vars);
        readonly double[] _v = new double[Vars.Length];
        long _frame;
        public double T;

        public Script() => Frame();
        public void Frame(params (string Var, double Value)[] set)
        {
            foreach (var (var, value) in set) _v[Array.IndexOf(Vars, var)] = value;
            Rec.OnFrame(new ObserveFrame(++_frame, T, (double[])_v.Clone()));
            T += 10;
        }
        public void Idle(int frames) { for (var i = 0; i < frames; i++) Frame(); }
        public string Step(StepKind kind, Action body)
        {
            var name = $"FD1/{kind}";
            Rec.BeginStep(name, Fd, kind, T);
            body();
            Rec.EndSteps(T);
            return string.Join("\n", ObserveAnalysis.Summarize(Rec.Steps[^1], Rec.Rows, Rec.ValueBefore(Fd.Button, Rec.Steps[^1].StartMs)));
        }
    }

    [Fact]
    public void Counter_and_reset_key_are_told_apart()
    {
        var s = new Script();
        var counter = s.Step(StepKind.Double, () =>
        {
            foreach (var v in new[] { 1, 2, 3, 4 }) { s.Frame((Fd.Button, v)); s.Idle(3); }
        });
        Assert.Contains("0 -> 1 -> 2 +40ms/4f -> 3 +40ms/4f -> 4 +40ms/4f", counter);
        Assert.Contains("deltas +1 +1 +1 +1; counts up, ends 4 (net 4)", counter);
        Assert.Contains("shortest gap between two changes: 40 ms / 4 frames", counter);
        Assert.DoesNotContain("JUMP", counter);

        var reset = s.Step(StepKind.Once, () => { s.Frame((Fd.Button, 0)); s.Idle(2); });
        Assert.Contains("4 -> 0", reset);
        Assert.Contains("back to 0", reset);
    }

    [Fact]
    public void Two_edges_in_one_frame_show_as_a_jump()
    {
        var s = new Script();
        var text = s.Step(StepKind.Double, () => { s.Frame((Fd.Button, 2)); s.Idle(2); });
        Assert.Contains("JUMP: 2 in one frame", text);
    }

    [Fact]
    public void Hold_shows_the_held_value_and_lamp_latency()
    {
        var s = new Script();
        var text = s.Step(StepKind.Hold, () =>
        {
            s.Frame((Fd.Button, 1));
            s.Idle(4);
            s.Frame(("I_FCU_EFIS1_FD", 1)); // 5 frames after the press
            s.Idle(54);
            s.Frame((Fd.Button, 2));
            s.Idle(3);
        });
        Assert.Contains("longest value before the last change: 1 for 600 ms", text);
        Assert.Contains("I_FCU_EFIS1_FD: 0->1 (+50ms/5f)", text);
        Assert.Contains("I_FCU_EFIS2_FD: no change", text);
    }

    [Fact]
    public void Report_lists_lamps_not_back_and_side_effects()
    {
        var s = new Script();
        var text = s.Step(StepKind.Once, () => { s.Frame((Fd.Button, 1), ("I_FCU_EFIS1_FD", 1), ("S_OTHER", 5)); s.Idle(2); });
        // a lamp that goes on and off again shows every value, not "0->0"
        var blink = s.Step(StepKind.Double, () => { s.Frame(("S_OTHER", 6)); s.Frame(("S_OTHER", 5)); });
        Assert.Contains("also changed: S_OTHER 5->6->5", blink);
        Assert.Contains("also changed: S_OTHER 0->5", text);
        var report = ObserveAnalysis.Report(s.Rec);
        Assert.Contains("NOT back at the start value", report);
        Assert.Contains("I_FCU_EFIS1_FD: 0 -> 1", report);
        Assert.Contains("button vars that did not return to their start value: S_FCU_EFIS1_FD 0->1", report);
    }

    [Fact]
    public void Csv_has_one_row_per_change_with_relative_time()
    {
        var s = new Script();
        s.Idle(2);
        s.Step(StepKind.Once, () => { s.Frame((Fd.Button, 1)); s.Rec.OnText(s.T, ObservePhase.McduScreenVar, "DATA INDEX | S, E"); });
        var lines = s.Rec.Csv().TrimEnd().Split('\n');
        Assert.Equal("t_ms,frame,step,control,var,old,new,delta,note", lines[0]);
        Assert.Equal("30.0,4,FD1/Once,FD1,S_FCU_EFIS1_FD,0,1,1,", lines[1]);
        Assert.Equal("40.0,4,FD1/Once,FD1,MCDU1_SCREEN,,,,\"DATA INDEX | S, E\"", lines[2]);
    }

    // --- the guided session with fakes ---

    static ObserveSettings Settings(string dir, string? only = "FD1") => new()
    {
        CsvPath = Path.Combine(dir, "observe-results.csv"),
        SummaryPath = Path.Combine(dir, "summary.txt"),
        RawDir = Path.Combine(dir, "raw"),
        Only = only,
        SettleMs = 30,
        ConnectSeconds = 5,
    };

    [Fact]
    public async Task Guided_run_records_the_pilot_clicks_and_writes_the_csv()
    {
        var dir = Path.Combine(Path.GetTempPath(), "observe-test-" + Guid.NewGuid().ToString("N"));
        await using var source = new FakeObserveSource(ObserveVars.All);
        var answers = 0;
        // Each Enter: the "pilot" clicked just before pressing it.
        async Task<string?> Pilot(CancellationToken ct)
        {
            var n = answers++;
            if (n >= 1) { source.Set(Fd.Button, n); await Task.Delay(40, ct); }
            return n == 2 ? "s" : "";
        }
        var output = new StringWriter();
        var phase = new ObservePhase(source, new FakeSimvars(), null, output, Pilot, Settings(dir));
        Assert.Equal(0, await phase.Run(default));

        var csv = File.ReadAllText(Path.Combine(dir, "observe-results.csv"));
        Assert.Contains("FD1/once,FD1,S_FCU_EFIS1_FD,0,1,1,", csv);
        Assert.Contains("FD1/hold,FD1,S_FCU_EFIS1_FD,2,3,1,", csv);
        Assert.Contains("(SKIPPED)", File.ReadAllText(Path.Combine(dir, "summary.txt")));
        Assert.Single(Directory.GetFiles(Path.Combine(dir, "raw"), "observe-*.csv"));
        Assert.Contains("all steps done", output.ToString());
    }

    [Fact]
    public async Task Wrong_setup_stops_before_any_step_and_writes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "observe-test-" + Guid.NewGuid().ToString("N"));
        await using var source = new FakeObserveSource(ObserveVars.All);
        source.Set(SafetyVars.ParkingBrake, 0);
        var phase = new ObservePhase(source, new FakeSimvars(), null, new StringWriter(), _ => Task.FromResult<string?>(""), Settings(dir));
        var e = await Assert.ThrowsAsync<SafetyStopException>(() => phase.Run(default));
        Assert.Contains("parking brake", e.Message);
        Assert.False(File.Exists(Path.Combine(dir, "observe-results.csv")));
    }

    [Fact]
    public async Task Ctrl_C_saves_what_was_recorded()
    {
        var dir = Path.Combine(Path.GetTempPath(), "observe-test-" + Guid.NewGuid().ToString("N"));
        await using var source = new FakeObserveSource(ObserveVars.All);
        using var cts = new CancellationTokenSource();
        var answers = 0;
        async Task<string?> Pilot(CancellationToken ct)
        {
            if (answers++ == 0) return "";
            source.Set(Fd.Button, 1);
            await Task.Delay(40, ct);
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return "";
        }
        var phase = new ObservePhase(source, new FakeSimvars(), null, new StringWriter(), Pilot, Settings(dir));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => phase.Run(cts.Token));
        Assert.Contains("S_FCU_EFIS1_FD,0,1,1,", File.ReadAllText(Path.Combine(dir, "observe-results.csv")));
    }
}
