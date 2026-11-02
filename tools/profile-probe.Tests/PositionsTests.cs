using ProfileProbe;

namespace ProfileProbe.Tests;

public class PositionsTests
{
    static readonly HashSet<string> ScanNames =
        File.ReadLines(RepoPaths.Find().Scan).Where(l => !l.StartsWith('#')).Select(l => l.Split(',')[0]).ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void Every_var_exists_in_the_scan_and_fits_the_reader()
    {
        Assert.All(PositionsVars.All, v => Assert.Contains(v, ScanNames));
        Assert.True(PositionsVars.All.Count <= ObserveReader.MaxVars, $"{PositionsVars.All.Count} vars");
        Assert.Equal(PositionsVars.Controls.Length, PositionsVars.Controls.Select(c => c.Id).Distinct().Count());
        Assert.All(SafetyVars.Lvars, v => Assert.Contains(v, PositionsVars.All));
    }

    [Fact]
    public void The_session_fits_in_20_minutes()
    {
        Assert.True(PositionsVars.TotalSeconds(PositionsVars.Controls) <= 20 * 60,
            $"{PositionsVars.TotalSeconds(PositionsVars.Controls)} s");
    }

    [Fact]
    public void Never_asks_for_controls_that_must_not_be_pressed_in_tests()
    {
        // CLAUDE.md: never fire buttons/agents, IDG disconnect, ENG MASTER, EVAC, oxygen MAN ON or ditching.
        string[] never = ["FIRE", "AGENT", "IDG", "ENG_MASTER", "EVAC", "MASK_MAN_ON", "DITCHING", "RAT_MAN_ON", "EMER_GEN", "INST_DISCONNECT"];
        foreach (var c in PositionsVars.Controls)
            foreach (var v in c.Vars)
                Assert.DoesNotContain(never, n => v.Contains(n, StringComparison.Ordinal));
    }

    // --- classification on scripted frame streams ---

    static readonly PositionControl Strobe = PositionsVars.Controls.Single(c => c.Id == "STROBE");
    static readonly PositionControl Apu = PositionsVars.Controls.Single(c => c.Id == "APU_START");
    static readonly PositionControl Spd = PositionsVars.Controls.Single(c => c.Id == "FCU_SPD");
    static readonly PositionControl Gain = PositionsVars.Controls.Single(c => c.Id == "WX_GAIN");

    sealed class Script
    {
        public readonly ObserveRecorder Rec = new(PositionsVars.All);
        readonly double[] _v = new double[PositionsVars.All.Count];
        long _frame;
        public double T;

        public Script() => Frame();
        public void Frame(params (string Var, double Value)[] set)
        {
            foreach (var (var, value) in set) _v[PositionsVars.All.ToList().IndexOf(var)] = value;
            Rec.OnFrame(new ObserveFrame(++_frame, T, (double[])_v.Clone()));
            T += 10;
        }
        public void Idle(int frames) { for (var i = 0; i < frames; i++) Frame(); }
        public List<PositionResult> Step(PositionControl c, Action body, bool skip = false)
        {
            var name = c.Id + (Rec.Steps.Any(x => x.Control?.Id == c.Id) ? "-2" : "");
            Rec.BeginStep(name, c.AsObserve(), null, T);
            body();
            if (skip) Rec.SkipCurrent();
            Rec.BeginStep(name + "/after", null, null, T);
            return PositionsAnalysis.Results(Rec.Steps[^2], c, Rec);
        }
        public string Text(PositionControl c) => string.Join("\n", PositionsAnalysis.Summarize(Rec.Steps.Last(s => s.Control?.Id == c.Id), c, Rec));
    }

    [Fact]
    public void A_sweep_and_back_gives_the_positions()
    {
        var s = new Script();
        var r = s.Step(Strobe, () =>
        {
            foreach (var v in new[] { 1, 2, 1, 0 }) { s.Frame((Strobe.Var, v)); s.Idle(20); }
        }).Single();
        Assert.Equal(PositionClass.Positions, r.Class);
        Assert.Equal([0, 1, 2], r.Values);
        Assert.True(r.BackToStart);
        Assert.Contains("POSITIONS 0, 1, 2", s.Text(Strobe));
    }

    [Fact]
    public void A_counting_click_is_a_push_button()
    {
        var s = new Script();
        s.Frame((Apu.Var, 6));
        var r = s.Step(Apu, () => { s.Frame((Apu.Var, 7)); s.Idle(6); s.Frame((Apu.Var, 8)); s.Idle(3); }).Single();
        Assert.Equal(PositionClass.PushButton, r.Class);
        Assert.Contains("2 edges, ends up (even)", r.Notes);
    }

    [Fact]
    public void One_change_is_a_latch_not_a_push_button()
    {
        var s = new Script();
        var r = s.Step(Apu, () => { s.Frame((Apu.Var, 1)); s.Idle(3); }).Single();
        Assert.Equal(PositionClass.Positions, r.Class);
        Assert.Contains(r.Notes, n => n.StartsWith("one change only"));
    }

    [Fact]
    public void A_spring_loaded_push_pull_knob_shows_both_sides_and_how_long_each_was_held()
    {
        var s = new Script();
        var r = s.Step(Spd, () =>
        {
            s.Frame((Spd.Var, 1)); s.Idle(9);   // push: 100 ms
            s.Frame((Spd.Var, 0)); s.Idle(30);
            s.Frame((Spd.Var, -1)); s.Idle(14); // pull: 150 ms
            s.Frame((Spd.Var, 0)); s.Idle(5);
        }).Single();
        Assert.Equal(PositionClass.Positions, r.Class);
        Assert.Equal([-1, 0, 1], r.Values);
        Assert.Contains("longest hold: -1 150 ms, 1 100 ms", r.Notes);
    }

    [Fact]
    public void A_gap_or_a_jump_is_flagged()
    {
        var s = new Script();
        var r = s.Step(Strobe, () => { s.Frame((Strobe.Var, 2)); s.Idle(5); s.Frame((Strobe.Var, 0)); s.Idle(5); }).Single();
        Assert.Contains("gap: 1 not seen", r.Notes);
        Assert.Contains(r.Notes, n => n.Contains("jump"));
    }

    [Fact]
    public void Fractions_are_an_analog_range()
    {
        var s = new Script();
        var r = s.Step(Gain, () =>
        {
            foreach (var v in new[] { 0.1, 0.25, 0.5, 0.25, 0 }) { s.Frame((Gain.Var, v)); s.Idle(2); }
        }).First();
        Assert.Equal(PositionClass.Analog, r.Class);
        Assert.Contains("ANALOG 0..0.5", s.Text(Gain));
    }

    [Fact]
    public void A_neighbour_that_moves_is_reported_when_the_label_points_at_the_wrong_var()
    {
        var s = new Script();
        var tcas = PositionsVars.Controls.Single(c => c.Id == "TCAS_MODE");
        var results = s.Step(tcas, () => { s.Frame(("S_XPDR_MODE", 1)); s.Idle(5); s.Frame(("S_XPDR_MODE", 0)); s.Idle(5); });
        Assert.Contains(results, r => r.Var == "S_XPDR_MODE" && r.Class == PositionClass.Positions);
        Assert.Contains("S_XPDR_OPERATION did not move, but a neighbour did", s.Text(tcas));
    }

    [Fact]
    public void Results_csv_uses_the_last_valid_attempt_and_lists_skipped_controls()
    {
        var s = new Script();
        s.Step(Strobe, () => { s.Frame((Strobe.Var, 1)); s.Idle(3); }, skip: true); // a redo
        s.Frame((Strobe.Var, 0));
        s.Step(Strobe, () => { s.Frame((Strobe.Var, 1)); s.Idle(3); s.Frame((Strobe.Var, 2)); s.Idle(3); s.Frame((Strobe.Var, 0)); s.Idle(3); });
        s.Step(Apu, () => s.Idle(2), skip: true);
        var csv = PositionsAnalysis.ResultsCsv(s.Rec, [Strobe, Apu, Spd]).TrimEnd().Split('\n');
        Assert.Equal("control,label,kind,var,class,values,start,end,changes,back_to_start,notes", csv[0]);
        Assert.StartsWith("STROBE,STROBE switch,Move,S_OH_EXT_LT_STROBE,positions,0 1 2,0,0,3,yes,", csv[1]);
        Assert.StartsWith("APU_START,APU START button,Click,S_OH_ELEC_APU_START,skipped", csv[2]);
        Assert.StartsWith("FCU_SPD,FCU SPD knob,PushPull,S_FCU_SPEED,not-reached", csv[3]);
    }

    // --- the guided session with fakes ---

    [Fact]
    public async Task Guided_run_records_and_classifies_and_writes_the_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "positions-test-" + Guid.NewGuid().ToString("N"));
        await using var source = new FakeObserveSource(PositionsVars.All);
        var answers = 0;
        async Task<string?> Pilot(CancellationToken ct)
        {
            if (answers++ == 1)
                foreach (var v in new[] { 1, 2, 1, 0 }) { source.Set(Strobe.Var, v); await Task.Delay(40, ct); }
            return "";
        }
        var output = new StringWriter();
        var settings = new PositionsSettings
        {
            ResultsPath = Path.Combine(dir, "positions-results.csv"),
            ChangesPath = Path.Combine(dir, "positions-changes.csv"),
            SummaryPath = Path.Combine(dir, "summary.txt"),
            RawDir = Path.Combine(dir, "raw"),
            Only = "STROBE",
            SettleMs = 30,
            ConnectSeconds = 5,
        };
        var phase = new PositionsPhase(source, new FakeSimvars(), output, Pilot, settings);
        Assert.Equal(0, await phase.Run(default));

        Assert.Contains("STROBE,STROBE switch,Move,S_OH_EXT_LT_STROBE,positions,0 1 2,0,0,4,yes,", File.ReadAllText(settings.ResultsPath));
        Assert.Contains("S_OH_EXT_LT_STROBE,0,1,1,", File.ReadAllText(settings.ChangesPath));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, "raw"), "positions-*.csv").Length);
        Assert.Contains("POSITIONS 0, 1, 2", output.ToString());
        Assert.Contains("all steps done", output.ToString());
    }

    [Fact]
    public async Task Wrong_setup_stops_before_any_step_and_writes_nothing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "positions-test-" + Guid.NewGuid().ToString("N"));
        await using var source = new FakeObserveSource(PositionsVars.All);
        source.Set("S_ENG_MASTER_1", 1);
        var settings = new PositionsSettings
        {
            ResultsPath = Path.Combine(dir, "r.csv"), ChangesPath = Path.Combine(dir, "c.csv"), SummaryPath = Path.Combine(dir, "s.txt"),
            SettleMs = 30, ConnectSeconds = 5,
        };
        var phase = new PositionsPhase(source, new FakeSimvars(), new StringWriter(), _ => Task.FromResult<string?>(""), settings);
        var e = await Assert.ThrowsAsync<SafetyStopException>(() => phase.Run(default));
        Assert.Contains("S_ENG_MASTER_1", e.Message);
        Assert.False(File.Exists(settings.ResultsPath));
    }
}
