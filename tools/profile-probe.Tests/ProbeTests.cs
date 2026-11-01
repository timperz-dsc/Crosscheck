using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Sim;
using Crosscheck.Profiles.Model;
using ProfileProbe;

namespace ProfileProbe.Tests;

sealed class FakeSimvars : ISimvarReader
{
    public double OnGround = 1, ParkingBrake = 1, Speed, N1;
    public SimvarSnapshot? Latest => new(DateTime.UtcNow, OnGround, ParkingBrake, Speed, N1, N1, 0, 0, 0, 0);
    public string? Title { get; set; } = "FenixA320 test livery";
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

sealed class Rig : IAsyncDisposable
{
    public required FakeSimAdapter Sim { get; init; }
    public required FakeFenixGraphQl Gql { get; init; }
    public required FakeSimvars Simvars { get; init; }
    public required ProbeSession Session { get; init; }
    public required Checkpoint Checkpoint { get; init; }
    public required ResultLog Log { get; init; }
    public required string Dir { get; init; }
    public required List<Target> Targets { get; init; }
    public required StringWriter Out { get; init; }

    public static readonly ProbeTiming Fast = new()
    {
        ReadBackMs = 300, SettleMs = 80, StepMs = 60, PressMs = 20, HoldMs = 100, BreakerSettleMs = 80, EffectRestoreMs = 400, PollMs = 2,
    };

    public static Inputs MinimalInputs(IEnumerable<string> names) => new()
    {
        Profile = new Profile(1, new AircraftInfo("t", "t", new MatchInfo(), []), ["probe"], []),
        ProfileText = "",
        Review = [],
        Deny = new DenyFile { Rules = [], Breakers = [] },
        Scan = names.Distinct().ToDictionary(n => n, _ => "0"),
    };

    public static async Task<Rig> Make(IEnumerable<Target> targets, params string[] extra)
    {
        var list = targets.ToList();
        var watched = list.Select(t => t.Name).Concat(extra).Concat(SafetyVars.Lvars).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        var inputs = MinimalInputs(watched);
        var profile = ProbeProfile.Build(inputs, list, watched);
        var sim = new FakeSimAdapter(profile, new FakeSimOptions { FrameMs = 5, EffectLagMs = 10 });
        await sim.StartAsync(new SimPlan(watched), default);
        sim.Logic(SafetyVars.ParkingBrake, 1);
        sim.Logic("S_OH_ELEC_BAT1", 1);
        sim.Logic("S_OH_ELEC_BAT2", 1);
        sim.Logic("S_OH_ELEC_EXT_PWR", 2);
        foreach (var p in SafetyVars.Power) sim.Logic(p, 1);
        var gql = new FakeFenixGraphQl(sim);
        var simvars = new FakeSimvars();
        var safety = new SafetyMonitor(sim, simvars, gql);
        safety.TakeBaseline();
        var dir = Path.Combine(Path.GetTempPath(), "probe-test-" + Guid.NewGuid().ToString("N"));
        var cp = new Checkpoint(Path.Combine(dir, "checkpoint.json"));
        var log = new ResultLog(Path.Combine(dir, "details.jsonl"));
        var output = new StringWriter();
        return new Rig
        {
            Sim = sim, Gql = gql, Simvars = simvars, Checkpoint = cp, Log = log, Dir = dir, Targets = list, Out = output,
            Session = new ProbeSession(sim, gql, safety, cp, log, watched, new HashSet<string>(), Fast, output),
        };
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
        return Sim.DisposeAsync();
    }
}

public class SessionTests
{
    static Target Bin(string name) => new(name, Phase.Verified, Targets.BinaryPlusTwo);

    [Fact]
    public async Task Verified_switch_with_lamp_is_confirmed_and_restored()
    {
        await using var rig = await Rig.Make([Bin("S_OH_TEST_SW")], "I_OH_TEST_SW_L", "I_OH_TEST_SW_U");
        // the lower lamp follows the switch, but only for 0/1
        rig.Sim.ValueChanged += (n, v) => { if (n == "S_OH_TEST_SW" && v <= 1) rig.Sim.Logic("I_OH_TEST_SW_L", v); };

        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Verified, default);

        Assert.Equal("confirmed", r!.Result);
        Assert.True(r.Restored);
        Assert.Equal(0, rig.Sim.Get("S_OH_TEST_SW"));
        Assert.Equal(0, rig.Sim.Get("I_OH_TEST_SW_L"));
        Assert.Equal("I_OH_TEST_SW_L", r.ProposedVerifyLvar);
        Assert.Null(rig.Checkpoint.Data.Pending);
        Assert.True(rig.Checkpoint.IsDone(Phase.Verified, "S_OH_TEST_SW"));
    }

    [Fact]
    public async Task Press_counter_switch_is_restored_by_one_more_press()
    {
        // Like the yellow ELEC PUMP: each press (rising to an odd value) toggles the pump.
        await using var rig = await Rig.Make([Bin("S_OH_HYD_PUMP")], "I_OH_HYD_PUMP_L");
        var last = 0.0;
        rig.Sim.ValueChanged += (n, v) =>
        {
            if (n != "S_OH_HYD_PUMP") return;
            if (v > last && (long)v % 2 == 1) rig.Sim.Logic("I_OH_HYD_PUMP_L", 1 - rig.Sim.Get("I_OH_HYD_PUMP_L"));
            last = v;
        };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Verified, default);
        Assert.Equal("counter-suspected", r!.Result);
        Assert.True(r.Restored);
        Assert.Equal(0, rig.Sim.Get("I_OH_HYD_PUMP_L"));
        Assert.Equal(0, (long)rig.Sim.Get("S_OH_HYD_PUMP") % 2);
    }

    [Fact]
    public async Task Third_state_rejects_binary()
    {
        await using var rig = await Rig.Make([Bin("S_OH_TRI")], "S_OH_TRI_Anim");
        rig.Sim.ValueChanged += (n, v) => { if (n == "S_OH_TRI") rig.Sim.Logic("S_OH_TRI_Anim", v * 10); };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Verified, default);
        Assert.Equal("rejected", r!.Result);
        Assert.Equal(0, rig.Sim.Get("S_OH_TRI"));
    }

    [Fact]
    public async Task Exception_in_test_restores_then_rethrows()
    {
        await using var rig = await Rig.Make([Bin("S_OH_X")]);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.RunVar(rig.Targets[0], async (run, ct) =>
        {
            await run.Write(1, ct);
            throw new InvalidOperationException("boom");
        }, default));
        Assert.Equal("boom", ex.Message);
        Assert.Equal(0, rig.Sim.Get("S_OH_X"));
        Assert.Null(rig.Checkpoint.Data.Pending);
        Assert.False(rig.Checkpoint.IsDone(Phase.Verified, "S_OH_X")); // probed again on resume
    }

    [Fact]
    public async Task Ctrl_C_restores_then_stops()
    {
        await using var rig = await Rig.Make([Bin("S_OH_X")]);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Session.RunVar(rig.Targets[0], async (run, ct) =>
        {
            await run.Write(1, ct);
            cts.Cancel();
            await run.Delay(5000, ct);
            return null!;
        }, cts.Token));
        Assert.Equal(0, rig.Sim.Get("S_OH_X"));
    }

    [Fact]
    public async Task Parking_brake_released_is_a_safety_stop_after_restore()
    {
        await using var rig = await Rig.Make([Bin("S_OH_X")]);
        await Assert.ThrowsAsync<SafetyStopException>(() => rig.Session.RunVar(rig.Targets[0], async (run, ct) =>
        {
            rig.Sim.Logic(SafetyVars.ParkingBrake, 0);
            await run.Write(1, ct);
            return null!;
        }, default));
        Assert.Equal(0, rig.Sim.Get("S_OH_X"));
    }

    [Fact]
    public async Task Engine_spooling_or_leaving_the_ground_stops()
    {
        await using var rig = await Rig.Make([Bin("S_OH_X")]);
        rig.Simvars.N1 = 15;
        await Assert.ThrowsAsync<SafetyStopException>(() => rig.Session.RunVar(rig.Targets[0], Probes.Verified, default));
        rig.Simvars.N1 = 0;
        rig.Simvars.OnGround = 0;
        await Assert.ThrowsAsync<SafetyStopException>(() => rig.Session.RunVar(rig.Targets[0], Probes.Verified, default));
        Assert.Empty(rig.Sim.Writes);
    }

    [Fact]
    public async Task Setup_switch_changing_on_its_own_stops()
    {
        await using var rig = await Rig.Make([Bin("S_OH_X")]);
        await Assert.ThrowsAsync<SafetyStopException>(() => rig.Session.RunVar(rig.Targets[0], async (run, ct) =>
        {
            rig.Sim.Logic("S_OH_ELEC_EXT_PWR", 0);
            await run.Write(1, ct);
            return null!;
        }, default));
        Assert.Equal(0, rig.Sim.Get("S_OH_X"));
    }

    [Fact]
    public async Task Losing_bus_power_is_a_safety_stop()
    {
        await using var rig = await Rig.Make([Bin("S_OH_ELEC_BUSTIE_TEST")]);
        rig.Sim.ValueChanged += (n, v) => { if (n == "S_OH_ELEC_BUSTIE_TEST" && v == 1) rig.Sim.Logic("B_ELEC_BUS_POWER_AC_1", 0); };
        await Assert.ThrowsAsync<SafetyStopException>(() => rig.Session.RunVar(rig.Targets[0], async (run, ct) =>
        {
            await run.Write(1, ct);
            await Task.Delay(50, ct);
            await run.Write(1, ct);
            return null!;
        }, default));
        Assert.Equal(0, rig.Sim.Get("S_OH_ELEC_BUSTIE_TEST"));
    }

    [Fact]
    public async Task Value_that_cannot_be_restored_stops_and_stays_pending()
    {
        await using var rig = await Rig.Make([Bin("S_OH_STUCK")]);
        rig.Sim.Stick("S_OH_STUCK", 1, revertMs: 1);
        await Assert.ThrowsAsync<NotRestoredException>(() => rig.Session.RunVar(rig.Targets[0], async (run, ct) =>
        {
            rig.Sim.Logic("S_OH_STUCK", 1);
            await Task.Delay(20, ct);
            return new ProbeResult { Var = "S_OH_STUCK", Phase = Phase.Verified, Test = "t", Result = "x" };
        }, default));
        Assert.NotNull(rig.Checkpoint.Data.Pending);
        Assert.Contains("S_OH_STUCK", rig.Checkpoint.Data.NotRestored);
    }

    [Fact]
    public async Task Pending_var_from_an_interrupted_run_is_restored()
    {
        await using var rig = await Rig.Make([Bin("S_OH_X")]);
        rig.Sim.Logic("S_OH_X", 1);
        rig.Checkpoint.SetPending(new Pending("S_OH_X", Phase.Verified, 0, RestoreKind.Absolute, []));
        var reopened = new Checkpoint(Path.Combine(rig.Dir, "checkpoint.json"));
        Assert.NotNull(reopened.Data.Pending);
        Assert.True(await Program.RestorePending(rig.Session, reopened.Data.Pending!));
        Assert.Equal(0, rig.Sim.Get("S_OH_X"));
        Assert.Null(rig.Checkpoint.Data.Pending);
    }

    [Fact]
    public async Task Original_outside_test_values_writes_nothing()
    {
        await using var rig = await Rig.Make([Bin("S_OH_X")]);
        rig.Sim.Logic("S_OH_X", 5);
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Verified, default);
        Assert.Equal("skipped", r!.Result);
        Assert.Empty(rig.Sim.Writes);
    }

    [Fact]
    public async Task Probe_profile_refuses_writes_to_anything_but_targets()
    {
        await using var rig = await Rig.Make([Bin("S_OH_X")], "S_OH_OTHER");
        await Assert.ThrowsAsync<SafetyViolationException>(() => rig.Sim.WriteAsync("S_OH_OTHER", 1));
        await Assert.ThrowsAsync<SafetyViolationException>(() => rig.Sim.WriteAsync("S_OH_X", 5));
        await Assert.ThrowsAsync<SafetyViolationException>(() => rig.Sim.WriteAsync(SafetyVars.ParkingBrake, 0));
    }
}

public class ProbeKindTests
{
    static Target Btn(string name) => new(name, Phase.Buttons, null, Counter: true);

    [Fact]
    public async Task Edge_triggered_button_is_pulse_and_is_pressed_again_on_restore()
    {
        await using var rig = await Rig.Make([Btn("S_FCU_LOC")], "I_FCU_LOC");
        var last = 0.0;
        rig.Sim.ValueChanged += (n, v) =>
        {
            if (n != "S_FCU_LOC") return;
            if (last == 0 && v == 1) rig.Sim.Logic("I_FCU_LOC", 1 - rig.Sim.Get("I_FCU_LOC"));
            last = v;
        };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Button, default);
        Assert.Equal("pulse", r!.Result);
        Assert.True(r.Restored);
        Assert.Equal(0, rig.Sim.Get("I_FCU_LOC"));
        Assert.Equal(0, rig.Sim.Get("S_FCU_LOC"));
        Assert.Equal("I_FCU_LOC", r.ProposedVerifyLvar);
    }

    [Fact]
    public async Task Button_whose_effect_follows_the_value_is_latching()
    {
        await using var rig = await Rig.Make([Btn("S_MIP_BTN")], "I_MIP_BTN");
        rig.Sim.ValueChanged += (n, v) => { if (n == "S_MIP_BTN") rig.Sim.Logic("I_MIP_BTN", v); };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Button, default);
        Assert.Equal("follows-value", r!.Result);
        Assert.Equal(new double[] { 0, 1 }, r.ProposedValues);
    }

    [Fact]
    public async Task Self_resetting_button_is_pulse()
    {
        await using var rig = await Rig.Make([Btn("S_MIP_SELF")]);
        rig.Sim.ValueChanged += (n, v) => { if (n == "S_MIP_SELF" && v == 1) _ = Task.Delay(30).ContinueWith(_ => rig.Sim.Logic("S_MIP_SELF", 0)); };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Button, default);
        Assert.Equal("pulse", r!.Result);
        Assert.Contains("self-resetting", r.Notes);
    }

    [Fact]
    public async Task Button_with_no_effect_stays_unresolved()
    {
        await using var rig = await Rig.Make([Btn("S_MIP_NOTHING")]);
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Button, default);
        Assert.Equal("unresolved", r!.Result);
        Assert.Equal(0, rig.Sim.Get("S_MIP_NOTHING"));
    }

    [Fact]
    public async Task Press_counter_keeps_parity_on_restore()
    {
        await using var rig = await Rig.Make([Btn("S_MIP_MASTER_CAUTION_CAPT")]);
        rig.Sim.Logic("S_MIP_MASTER_CAUTION_CAPT", 24);
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Button, default);
        Assert.Equal("counter", r!.Result);
        Assert.True(r.ProposedCounter);
        Assert.Equal(0, (long)rig.Sim.Get("S_MIP_MASTER_CAUTION_CAPT") % 2);
    }

    [Fact]
    public async Task Button_resting_above_1_is_not_written()
    {
        await using var rig = await Rig.Make([Btn("S_FCU_EFIS2_ND_MODE")]);
        rig.Sim.Logic("S_FCU_EFIS2_ND_MODE", 3);
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Button, default);
        Assert.Equal("unresolved", r!.Result);
        Assert.Empty(rig.Sim.Writes);
        Assert.Equal(3, rig.Sim.Get("S_FCU_EFIS2_ND_MODE"));
    }

    [Fact]
    public async Task Positions_from_an_animation_var()
    {
        await using var rig = await Rig.Make([new Target("S_OH_SEL", Phase.Positions, Targets.Steps)], "S_OH_SEL_Anim");
        rig.Sim.ValueChanged += (n, v) => { if (n == "S_OH_SEL") rig.Sim.Logic("S_OH_SEL_Anim", Math.Min(v, 2)); };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Positions, default);
        Assert.Equal("positions", r!.Result);
        Assert.Equal(new double[] { 0, 1, 2 }, r.ProposedValues);
        Assert.Equal(0, rig.Sim.Get("S_OH_SEL"));
    }

    [Fact]
    public async Task Positions_where_GraphQL_only_echoes_stay_unresolved()
    {
        await using var rig = await Rig.Make([new Target("S_OH_ECHO", Phase.Positions, Targets.Steps)]);
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Positions, default);
        Assert.Equal("unresolved", r!.Result);
        Assert.Contains("echoes", r.Notes);
    }

    [Fact]
    public async Task Positions_where_Fenix_clamps_the_value()
    {
        await using var rig = await Rig.Make([new Target("S_OH_CLAMP", Phase.Positions, Targets.Steps)]);
        rig.Sim.ValueChanged += (n, v) => { if (n == "S_OH_CLAMP" && v > 2) rig.Sim.Logic("S_OH_CLAMP", 2); };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Positions, default);
        Assert.Equal(new double[] { 0, 1, 2 }, r!.ProposedValues);
    }

    [Fact]
    public async Task Encoder_display_is_the_N_var_that_follows()
    {
        await using var rig = await Rig.Make([new Target("E_PED_RMP1_INNER", Phase.Encoders, null, Delta: true)], "N_PED_RMP1_STBY", "N_OTHER");
        var last = 0.0;
        rig.Sim.ValueChanged += (n, v) =>
        {
            if (n != "E_PED_RMP1_INNER") return;
            rig.Sim.Logic("N_PED_RMP1_STBY", rig.Sim.Get("N_PED_RMP1_STBY") + (v - last) * 0.025);
            last = v;
        };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Encoder, default);
        Assert.Equal("display", r!.Result);
        Assert.Equal("N_PED_RMP1_STBY", r.ProposedDisplay);
        Assert.Equal(0, rig.Sim.Get("N_PED_RMP1_STBY"), 6);
    }

    [Fact]
    public async Task Breaker_pulled_with_effect_is_0_1()
    {
        await using var rig = await Rig.Make([new Target("E_CB_49VU_D02", Phase.Breakers, [0, 1, 2, 3])], "I_SOMETHING");
        rig.Sim.Logic("E_CB_49VU_D02", 1);
        rig.Sim.Logic("I_SOMETHING", 1);
        rig.Sim.ValueChanged += (n, v) => { if (n == "E_CB_49VU_D02") rig.Sim.Logic("I_SOMETHING", v >= 1 ? 1 : 0); };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Breaker, default);
        Assert.Equal("breaker-01", r!.Result);
        Assert.Equal(1, rig.Sim.Get("E_CB_49VU_D02"));
        Assert.Equal(1, rig.Sim.Get("I_SOMETHING"));
    }
}

public class RepoInputTests
{
    static readonly Lazy<Inputs> Real = new(() => Inputs.Load(RepoPaths.Find()));

    [Fact]
    public void No_phase_targets_a_denied_var_or_an_unsampled_breaker()
    {
        var inputs = Real.Value;
        foreach (var phase in new[] { Phase.Verified, Phase.Buttons, Phase.Positions, Phase.Encoders, Phase.Breakers })
        {
            var (targets, _) = Targets.For(phase, inputs);
            Assert.NotEmpty(targets);
            foreach (var t in targets)
            {
                Assert.Null(inputs.Deny.Denies(t.Name));
                Assert.Null(Targets.ExtraSkip(t.Name));
                if (t.Name.StartsWith("E_CB_")) Assert.Contains(t.Name, inputs.Deny.Breakers);
                Assert.True(inputs.Known(t.Name));
            }
        }
        Assert.Equal(11, Targets.For(Phase.Breakers, inputs).Targets.Count);
        Assert.Equal(65, inputs.Profile.Vars.Count(v => inputs.Deny.Denies(v.Name) is not null));
    }

    [Fact]
    public void Fire_and_engine_controls_are_never_targets()
    {
        var inputs = Real.Value;
        var all = new[] { Phase.Verified, Phase.Buttons, Phase.Positions, Phase.Encoders, Phase.Breakers }
            .SelectMany(p => Targets.For(p, inputs).Targets).Select(t => t.Name).ToList();
        Assert.DoesNotContain(all, n => n.StartsWith("S_OH_FIRE_") || n.StartsWith("S_ENG_MASTER") || n.Contains("EVAC") || n.Contains("DITCHING")
                                        || n.Contains("MAN_ON") || n.Contains("IDG") && !n.EndsWith("_Cover") || n == "S_OH_ELEC_APU_START");
    }

    [Fact]
    public void Probe_profile_only_makes_the_phase_targets_writable()
    {
        var inputs = Real.Value;
        var (targets, _) = Targets.For(Phase.Verified, inputs);
        var watched = ProbeProfile.Watched(inputs);
        var guard = new SimSafetyGuard(ProbeProfile.Build(inputs, targets, watched));
        Assert.Equal(targets.Count, watched.Count(guard.IsWritable));
        Assert.False(guard.IsWritable("S_OH_FIRE_ENG1_BUTTON"));
        Assert.False(guard.IsWritable("E_CB_105VU_A02"));
    }

    [Fact]
    public void Results_after_the_crash_cutoff_propose_nothing()
    {
        var cutoff = new ProbeResult { Var = "crash-cutoff", Phase = Phase.Readonly, Test = "manual", Result = "info", Notes = "2026-11-01T14:16:40Z" };
        var late = new ProbeResult { Var = "E_PED_RMP1_INNER", Phase = Phase.Encoders, Test = "encoder-display", Result = "display", ProposedDisplay = "N_X", At = new DateTime(2026, 11, 1, 14, 30, 0, DateTimeKind.Utc) };
        var early = late with { At = new DateTime(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc) };
        var t = Proposer.CrashCutoff([cutoff, late]);
        Assert.Equal("invalid-crash", Proposer.AfterCrash(late, t).Result);
        Assert.Null(Proposer.AfterCrash(late, t).ProposedDisplay);
        Assert.Equal("display", Proposer.AfterCrash(early, t).Result);
        Assert.Equal("invalid-crash", Proposer.AfterCrash(Proposer.AfterCrash(late, t), t).Result);
    }

    [Fact]
    public void Proposer_applies_results_and_the_result_validates()
    {
        var inputs = Real.Value;
        var guess = inputs.Profile.Vars.First(v => v.Note?.Contains("lower-half rule") ?? false);
        var stem = guess.Name[2..];
        var button = inputs.Review.First(r => r.Reason == "momentary or latching unknown" && ProfileGen.Rules.GroupOf(r.Name) is not null);
        List<ProbeResult> results =
        [
            new() { Var = guess.Name, Phase = Phase.Verified, Test = "verified-binary", Result = "confirmed", ProposedValues = [0, 1], Notes = "lamp" },
            new() { Var = guess.Name, Phase = Phase.Verified, Test = "lamp-half", Result = "upper", ProposedVerifyLvar = $"I_{stem}_U", Notes = "U changed" },
            new() { Var = button.Name, Phase = Phase.Buttons, Test = "momentary-or-latching", Result = "pulse", ProposedClass = "pulse", Notes = "edge" },
            new() { Var = "E_CB_105VU_A01", Phase = Phase.Breakers, Test = "breaker", Result = "breaker-01", Notes = "x" },
            new() { Var = "TITLE", Phase = Phase.Readonly, Test = "simvar", Result = "info", Notes = "FenixA320 Test" },
        ];
        var (text, changes, _) = Proposer.Propose(inputs, results);
        var check = Crosscheck.Profiles.ProfileLoader.LoadText(text, "proposed");
        Assert.True(check.IsValid, string.Join("\n", check.FormatIssues()));
        var p = check.Profile!;
        Assert.Equal($"I_{stem}_U", p.Vars.Single(v => v.Name == guess.Name).Verify!.Lvar);
        Assert.Equal(SyncClass.Pulse, p.Vars.Single(v => v.Name == button.Name).Class);
        Assert.Contains("0/1 breaker", p.Vars.Single(v => v.Name == "E_CB_105VU_A01").Note);
        Assert.Equal("FenixA320 Test", Assert.Single(p.Aircraft.Match.Titles!));
        Assert.Contains(changes, c => c.Kind == "effect source");
    }
}
