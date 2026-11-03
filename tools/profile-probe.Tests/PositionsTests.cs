using ProfileProbe;

namespace ProfileProbe.Tests;

public class PositionsTests
{
    static readonly HashSet<string> ScanNames =
        File.ReadLines(RepoPaths.Find().Scan).Where(l => !l.StartsWith('#')).Select(l => l.Split(',')[0]).ToHashSet(StringComparer.Ordinal);

    public static TheoryData<string> Runs { get { var d = new TheoryData<string>(); foreach (var k in PositionsVars.Runs.Keys) d.Add(k); return d; } }

    [Theory]
    [MemberData(nameof(Runs))]
    public void Every_var_exists_in_the_scan_or_is_a_known_sim_var_and_fits_the_reader(string run)
    {
        var controls = PositionsVars.Runs[run];
        var all = PositionsVars.AllOf(controls);
        Assert.All(all.Where(v => !SimVars.IsSimVar(v)), v => Assert.Contains(v, ScanNames));
        Assert.All(all.Where(SimVars.IsSimVar), v => Assert.NotEmpty(SimVars.Unit(v)));
        Assert.True(all.Count <= ObserveReader.MaxVars, $"{all.Count} vars");
        Assert.Equal(controls.Length, controls.Select(c => c.Id).Distinct().Count());
        Assert.All(SafetyVars.Lvars, v => Assert.Contains(v, all));
    }

    [Fact]
    public void Each_session_fits_its_time()
    {
        Assert.True(PositionsVars.TotalSeconds(PositionsVars.GateTest2) <= 20 * 60, $"{PositionsVars.TotalSeconds(PositionsVars.GateTest2)} s");
        // Gate test 3 prep: under 25 minutes (the FMGS setup before the FCU steps is the pilot's own time).
        Assert.True(PositionsVars.TotalSeconds(PositionsVars.GateTest3) <= 25 * 60, $"{PositionsVars.TotalSeconds(PositionsVars.GateTest3)} s");
        Assert.Equal("gate3", PositionsVars.DefaultRun);
    }

    [Theory]
    [MemberData(nameof(Runs))]
    public void Never_asks_for_controls_that_must_not_be_pressed_in_tests(string run)
    {
        // CLAUDE.md: never fire buttons/agents, IDG disconnect, ENG MASTER, EVAC, oxygen MAN ON or ditching.
        string[] never = ["FIRE", "AGENT", "IDG", "ENG_MASTER", "EVAC", "MASK_MAN_ON", "DITCHING", "RAT_MAN_ON", "EMER_GEN", "INST_DISCONNECT"];
        foreach (var c in PositionsVars.Runs[run])
            foreach (var v in c.Vars.Concat(c.Effects ?? []))
                Assert.DoesNotContain(never, n => v.Contains(n, StringComparison.Ordinal));
    }

    [Fact]
    public void Gate_test_3_run_covers_the_list_in_order_of_importance()
    {
        var ids = PositionsVars.GateTest3.Select(c => c.Id).ToList();
        // The writes that did not stick come first, then the FCU knobs (after the FMGS setup), the review-list
        // buttons, the knobs, and the comfort items last.
        Assert.Equal(["FLAPS", "SPOILERS", "ANTI_SKID", "RUD_TRIM", "RUD_TRIM_RESET", "DOOR_SWITCH", "DOOR"], ids.Take(7));
        Assert.True(ids.IndexOf("FCU_SPD") < ids.IndexOf("N1_MODE_1"));
        Assert.True(ids.IndexOf("ASP2_VOLUME") < ids.IndexOf("TRAY_TABLES"));
        Assert.Equal("GRAVITY_GEAR", ids[^1]);
        foreach (var id in new[] { "FCU_HDG", "FCU_ALT", "FCU_VS", "BARO_STD_CAPT", "BARO_STD_FO", "N1_MODE_2", "AUTOBRK_MED", "AUTOBRK_MAX",
                     "RMP1_KEYS", "CLOCK_SET", "DOOR_VIDEO", "ECAM_BRT", "CAPT_BRT", "FO_BRT", "DCDU_BRT", "SEATS", "ARMRESTS",
                     "SUNSHADES", "JUMPSEAT", "MASK_HATCH" })
            Assert.Contains(id, ids);
        // Each write that did not stick also watches candidate sim vars.
        foreach (var id in new[] { "FLAPS", "SPOILERS", "ANTI_SKID", "RUD_TRIM", "RUD_TRIM_RESET", "DOOR" })
            Assert.Contains(PositionsVars.GateTest3.Single(c => c.Id == id).Vars, SimVars.IsSimVar);
        // The FMGS is set up before the first FCU knob, and only there.
        Assert.Equal(["FCU_SPD"], PositionsVars.GateTest3.Where(c => c.Before is not null).Select(c => c.Id));
        // The RMP keys: HF1, HF2, AM, NAV, VOR, LS (ILS), GLS (MLS), ADF, BFO.
        var rmp = PositionsVars.GateTest3.Single(c => c.Id == "RMP1_KEYS").Vars;
        foreach (var k in new[] { "HF1", "HF2", "AM", "NAV", "VOR", "ILS", "MLS", "ADF", "BFO" }) Assert.Contains($"S_PED_RMP1_{k}", rmp);
        // The FCU family is watched in every step, so any var that moves with a push or pull shows up.
        Assert.Contains("I_FCU_SPEED_MODE", PositionsVars.AllOf(PositionsVars.GateTest3));
    }

    // --- classification on scripted frame streams ---

    // The classification tests use the gate test 2 run's steps (its results are in positions-results.csv).
    static readonly PositionControl[] Gate2 = PositionsVars.GateTest2;
    static readonly IReadOnlyList<string> Gate2Vars = PositionsVars.AllOf(Gate2);
    static readonly PositionControl Strobe = Gate2.Single(c => c.Id == "STROBE");
    static readonly PositionControl Apu = Gate2.Single(c => c.Id == "APU_START");
    static readonly PositionControl Spd = Gate2.Single(c => c.Id == "FCU_SPD");
    static readonly PositionControl Gain = Gate2.Single(c => c.Id == "WX_GAIN");

    sealed class Script
    {
        public readonly ObserveRecorder Rec = new(Gate2Vars);
        readonly double[] _v = new double[Gate2Vars.Count];
        long _frame;
        public double T;

        public Script() => Frame();
        public void Frame(params (string Var, double Value)[] set)
        {
            foreach (var (var, value) in set) _v[Gate2Vars.ToList().IndexOf(var)] = value;
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
        var tcas = Gate2.Single(c => c.Id == "TCAS_MODE");
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
        await using var source = new FakeObserveSource(Gate2Vars);
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
            Controls = Gate2,
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

    [Fact]
    public async Task The_fmgs_setup_is_asked_before_the_first_fcu_knob_and_waited_for()
    {
        var dir = Path.Combine(Path.GetTempPath(), "positions-test-" + Guid.NewGuid().ToString("N"));
        var run = PositionsVars.GateTest3;
        await using var source = new FakeObserveSource(PositionsVars.AllOf(run));
        var output = new StringWriter();
        var asked = new List<string>();
        Task<string?> Pilot(CancellationToken ct)
        {
            asked.Add(output.ToString());
            return Task.FromResult<string?>("");
        }
        var settings = new PositionsSettings
        {
            ResultsPath = Path.Combine(dir, "r.csv"), ChangesPath = Path.Combine(dir, "c.csv"), SummaryPath = Path.Combine(dir, "s.txt"),
            Only = "FCU_SPD", Controls = run, SettleMs = 30, ConnectSeconds = 5,
        };
        Assert.Equal(0, await new PositionsPhase(source, new FakeSimvars(), output, Pilot, settings).Run(default));
        // 1: the setup check, 2: the FMGS (step not started yet), 3: the step itself.
        Assert.Equal(3, asked.Count);
        Assert.EndsWith("> ", asked[1]);
        Assert.Contains("set up the FMGS", asked[1]);
        Assert.DoesNotContain("Pull the SPD knob", asked[1]);
        Assert.Contains("Pull the SPD knob, wait 2 s, push it, wait 2 s.", asked[2]);
        // Nothing moved: the summary says the hidden direction is still not visible, and what comes next.
        Assert.Contains("the hidden direction is still not visible", output.ToString());
        Assert.Contains("YourControls", output.ToString());
    }

    [Fact]
    public void Sim_vars_get_their_own_definitions_so_a_rejected_name_shifts_nothing()
    {
        // Gate 3 prep run (2026-11-03): one rejected sim var in a shared 64-slot definition shifted every
        // later value of that block by one. Now L: vars share chunks and each sim var is read on its own;
        // until the sim sends it, it is reported as missing (never a silent 0 read as "did not move").
        var vars = PositionsVars.AllOf(PositionsVars.GateTest3);
        var reader = new ObserveReader(vars);
        Assert.Equal(vars.Where(SimVars.IsSimVar), reader.MissingSimVars);
        Assert.Throws<ArgumentException>(() =>
            new ObserveReader(Enumerable.Range(0, ObserveReader.MaxSimVars + 1).Select(_ => "A:FLAPS HANDLE INDEX").ToList()));
        var text = File.ReadAllText(Path.Combine(RepoPaths.Find().Root, "tools", "profile-probe", "ObserveReader.cs"));
        Assert.Contains("RegisterDataDefineStruct<One>((Def)(SimFirstId + j))", text);
    }

    [Fact]
    public void The_confirmation_run_repeats_the_shifted_steps_with_the_fmgs_setup()
    {
        var ids = PositionsVars.Runs["gate3b"].Select(c => c.Id).ToList();
        Assert.Equal(["FLAPS", "SPOILERS", "ANTI_SKID", "RUD_TRIM", "RUD_TRIM_RESET", "DOOR_SWITCH", "DOOR",
            "FCU_SPD", "FCU_HDG", "FCU_ALT", "FCU_VS", "BARO_STD_CAPT"], ids);
        Assert.NotNull(PositionsVars.Runs["gate3b"].Single(c => c.Id == "FCU_SPD").Before);
        Assert.True(PositionsVars.TotalSeconds(PositionsVars.Runs["gate3b"]) <= 6 * 60);
    }

    [Fact]
    public void A_managed_lamp_that_goes_both_ways_shows_the_hidden_direction()
    {
        var spd = PositionsVars.GateTest3.Single(c => c.Id == "FCU_SPD");
        ChangeRow Row(string v, double old, double now) => new(0, 0, "FCU_SPD", "FCU_SPD", v, old, now);
        Assert.Contains("push and pull both visible through: I_FCU_SPEED_MANAGED",
            PositionsAnalysis.HiddenDirection(spd, [Row("S_FCU_SPEED", 0, -1), Row("I_FCU_SPEED_MANAGED", 1, 0), Row("I_FCU_SPEED_MANAGED", 0, 1)]));
        Assert.Contains("still not visible", PositionsAnalysis.HiddenDirection(spd, [Row("S_FCU_SPEED", 0, -1), Row("I_FCU_SPEED_MANAGED", 1, 0)]));
    }
}
