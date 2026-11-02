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
    public async Task Verified_never_writes_2_when_2_is_not_known()
    {
        await using var rig = await Rig.Make([new Target("S_OH_TWO", Phase.Verified, [0, 1])], "I_OH_TWO_L");
        rig.Sim.ValueChanged += (n, v) => { if (n == "S_OH_TWO") rig.Sim.Logic("I_OH_TWO_L", v); };
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Verified, default);
        Assert.Equal("confirmed", r!.Result);
        Assert.DoesNotContain(rig.Sim.Writes, w => w.Name == "S_OH_TWO" && w.Value > 1);
        Assert.Contains("2 not written", r.Notes);
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
    public async Task Encoder_that_does_not_come_back_gets_no_extra_clicks()
    {
        // Like E_PED_RMP3_OUTER in the rerun: +1 moved the display, -1 was not taken.
        await using var rig = await Rig.Make([new Target("E_PED_RMP3_OUTER", Phase.Encoders, null, Delta: true)], "N_PED_RMP3_STDBY");
        var last = 0.0;
        rig.Sim.ValueChanged += (n, v) =>
        {
            if (n != "E_PED_RMP3_OUTER") return;
            if (v > last) rig.Sim.Logic("N_PED_RMP3_STDBY", rig.Sim.Get("N_PED_RMP3_STDBY") + 1000);
            last = v;
        };
        await Assert.ThrowsAsync<NotRestoredException>(() => rig.Session.RunVar(rig.Targets[0], Probes.Encoder, default));
        Assert.All(rig.Sim.Writes.Where(w => w.Name == "E_PED_RMP3_OUTER"), w => Assert.InRange(w.Value, -1, 1));
        Assert.Equal("E_PED_RMP3_OUTER", rig.Checkpoint.Data.Pending?.Var);
    }

    [Fact]
    public async Task Breaker_with_no_effect_is_never_written_above_1()
    {
        await using var rig = await Rig.Make([new Target("E_CB_49VU_G02", Phase.Breakers, Targets.Binary)]);
        rig.Sim.Logic("E_CB_49VU_G02", 1);
        var r = await rig.Session.RunVar(rig.Targets[0], Probes.Breaker, default);
        Assert.Equal("no-effect", r!.Result);
        Assert.All(rig.Sim.Writes.Where(w => w.Name == "E_CB_49VU_G02"), w => Assert.InRange(w.Value, 0, 1));
        Assert.Equal(1, rig.Sim.Get("E_CB_49VU_G02"));
    }

    [Fact]
    public async Task Breaker_pulled_with_effect_is_0_1()
    {
        await using var rig = await Rig.Make([new Target("E_CB_49VU_D02", Phase.Breakers, Targets.Binary)], "I_SOMETHING");
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
        foreach (var phase in Enum.GetValues<Phase>())
        {
            var (targets, _) = Targets.For(phase, inputs);
            // Encoders are all resolved in step 3 (none left on the review list).
            if (phase is Phase.Breakers or Phase.Takeover or Phase.Hold) Assert.NotEmpty(targets);
            foreach (var t in targets)
            {
                if (t.Name.StartsWith("E_CB_")) Assert.Contains(t.Name, inputs.Deny.Breakers);
                Assert.True(inputs.Known(t.Name));
                if (phase is not (Phase.Takeover or Phase.Hold))
                {
                    Assert.Null(inputs.Deny.Denies(t.Name));
                    Assert.Null(Targets.ExtraSkip(t.Name));
                }
            }
        }
        Assert.Equal(11, Targets.For(Phase.Breakers, inputs).Targets.Count);
        // 67: profile vars that stay on the probe deny list (ENG MODE, EMER EXIT LT ... joined in gate test 2 prep).
        Assert.Equal(67, inputs.Profile.Vars.Count(v => inputs.Deny.Denies(v.Name) is not null));
    }

    [Fact]
    public void Range_rule_only_plans_values_known_to_exist()
    {
        var inputs = Real.Value;
        var known = KnownValues.Build(inputs, []);
        foreach (var phase in Enum.GetValues<Phase>())
            foreach (var t in Targets.For(phase, inputs, null, known).Targets.Where(t => t.Values is not null))
                Assert.All(t.Values!, v => Assert.True(known.IsKnown(t.Name, v), $"{phase} {t.Name} = {v}"));
        // the position test is switched off
        var (positions, skipped) = Targets.For(Phase.Positions, inputs);
        Assert.Empty(positions);
        Assert.All(skipped, s => Assert.Equal(Targets.PositionsOff, s.Why));
        // breakers 0/1 only; ND MODE 0-4, ND ZOOM 0-5
        Assert.All(Targets.For(Phase.Breakers, inputs).Targets, t => Assert.Equal([0, 1], t.Values!));
        var hold = Targets.For(Phase.Hold, inputs).Targets.ToDictionary(t => t.Name, t => t.Values!);
        Assert.Equal([0, 1, 2, 3, 4], hold["S_FCU_EFIS2_ND_MODE"]);
        Assert.Equal([0, 1, 2, 3, 4, 5], hold["S_FCU_EFIS2_ND_ZOOM"]);
        Assert.False(known.IsKnown("S_FCU_EFIS2_ND_MODE", 5));
    }

    [Fact]
    public void Range_rule_skips_a_switch_with_only_one_known_value()
    {
        var inputs = Real.Value;
        var name = Targets.For(Phase.Verified, inputs, null, KnownValues.Of()).Skipped.Select(s => s.Name)
            .First(n => inputs.Entry(n) is not null && inputs.Deny.Denies(n) is null && Targets.ExtraSkip(n) is null);
        var (none, skipped) = Targets.For(Phase.Verified, inputs, null, KnownValues.Of((name, [0])));
        Assert.DoesNotContain(none, t => t.Name == name);
        Assert.Contains(skipped, s => s.Name == name && s.Why.StartsWith("range rule"));
        var (some, _) = Targets.For(Phase.Verified, inputs, null, KnownValues.Of((name, [0, 1])));
        Assert.Equal([0, 1], some.Single(t => t.Name == name).Values!); // 2 is not known: never planned
    }

    [Fact]
    public void Values_seen_count_only_from_valid_results()
    {
        var crash = new ProbeResult { Var = "crash-cutoff", Phase = Phase.Readonly, Test = "manual", Result = "info", Notes = "2026-11-01T14:16:40Z" };
        var ok = new ProbeResult { Var = "S_X", Phase = Phase.Verified, Test = "verified-binary", Result = "confirmed", Original = 0, ProposedValues = [0, 1], At = new DateTime(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc) };
        var late = new ProbeResult { Var = "S_Y", Phase = Phase.Verified, Test = "verified-binary", Result = "confirmed", Original = 0, ProposedValues = [0, 1], At = new DateTime(2026, 11, 1, 14, 30, 0, DateTimeKind.Utc) };
        var stored = new ProbeResult { Var = "S_Z", Phase = Phase.Verified, Test = "verified-binary", Result = "unresolved", Original = 0, StoredOk = true, At = new DateTime(2026, 11, 1, 14, 0, 0, DateTimeKind.Utc) };
        var k = KnownValues.Build(Rig.MinimalInputs([]), [crash, ok, late, stored]);
        Assert.Equal([0, 1], k.Seen("S_X"));
        Assert.Empty(k.Seen("S_Y"));          // inside the crash window
        Assert.Equal([0], k.Seen("S_Z"));     // a stored value with no effect proves nothing
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
        var t = Proposer.CrashWindowOf([cutoff, late]);
        Assert.Equal("invalid-crash", Proposer.AfterCrash(late, t).Result);
        Assert.Null(Proposer.AfterCrash(late, t).ProposedDisplay);
        Assert.Equal("display", Proposer.AfterCrash(early, t).Result);
        Assert.Equal("invalid-crash", Proposer.AfterCrash(Proposer.AfterCrash(late, t), t).Result);

        // after the reload (crash-end) results are valid again
        var end = new ProbeResult { Var = "crash-end", Phase = Phase.Readonly, Test = "manual", Result = "info", Notes = "2026-11-01T16:00:00Z" };
        var rerun = late with { At = new DateTime(2026, 11, 1, 16, 30, 0, DateTimeKind.Utc) };
        var w = Proposer.CrashWindowOf([cutoff, end, late, rerun]);
        Assert.Equal("invalid-crash", Proposer.AfterCrash(late, w).Result);
        Assert.Equal("display", Proposer.AfterCrash(rerun, w).Result);
    }

    [Fact]
    public void Proposer_applies_results_and_the_result_validates()
    {
        var inputs = Real.Value;
        var guess = inputs.Profile.Vars.First(v => v.Note?.Contains("lower-half rule") ?? false);
        var stem = guess.Name[2..];
        var button = inputs.Review.First(r => r.Reason == "momentary or latching unknown" && ProfileGen.Rules.GroupOf(r.Name) is not null);
        var pos = Targets.PositionCandidates(inputs).First(n => inputs.Entry(n) is null && ProfileGen.Rules.GroupOf(n) is not null && inputs.Deny.Denies(n) is null);
        var neverProbed = Targets.PositionCandidates(inputs).First(n => n != pos && inputs.Entry(n) is null && ProfileGen.Rules.GroupOf(n) is not null);
        List<ProbeResult> results =
        [
            new() { Var = guess.Name, Phase = Phase.Verified, Test = "verified-binary", Result = "confirmed", ProposedValues = [0, 1], Notes = "lamp" },
            new() { Var = guess.Name, Phase = Phase.Verified, Test = "lamp-half", Result = "upper", ProposedVerifyLvar = $"I_{stem}_U", Notes = "U changed" },
            new() { Var = button.Name, Phase = Phase.Buttons, Test = "momentary-or-latching", Result = "pulse", ProposedClass = "pulse", Notes = "edge" },
            new() { Var = "E_CB_105VU_A01", Phase = Phase.Breakers, Test = "breaker", Result = "breaker-01", Notes = "x" },
            new() { Var = "TITLE", Phase = Phase.Readonly, Test = "simvar", Result = "info", Notes = "FenixA320 Test" },
            new() { Var = pos, Phase = Phase.Positions, Test = "positions", Result = "positions", ProposedValues = [0, 1, 2, 3], Original = 1, Notes = "stepped" },
            new() { Var = neverProbed, Phase = Phase.Positions, Test = "positions", Result = "skipped", Notes = "extra skip" },
        ];
        var (text, changes, _) = Proposer.Propose(inputs, results);
        var check = Crosscheck.Profiles.ProfileLoader.LoadText(text, "proposed");
        Assert.True(check.IsValid, string.Join("\n", check.FormatIssues()));
        var p = check.Profile!;
        Assert.Equal($"I_{stem}_U", p.Vars.Single(v => v.Name == guess.Name).Verify!.Lvar);
        Assert.Equal(SyncClass.Pulse, p.Vars.Single(v => v.Name == button.Name).Class);
        Assert.Contains("0/1 breaker", p.Vars.Single(v => v.Name == "E_CB_105VU_A01").Note);
        Assert.NotEmpty(p.Aircraft.Match.Titles!); // set by hand in step 3; the proposer leaves it alone
        Assert.Contains(changes, c => c.Kind == "effect source");

        // a stepped switch with an unknown position count gets only the values seen, never the
        // stepped range; a switch that was never probed is not added
        var entry = p.Vars.Single(v => v.Name == pos);
        var scan = double.Parse(inputs.Scan[pos], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(new[] { scan, 1.0 }.Distinct().Order(), entry.Values!);
        Assert.Contains("CRO-24", entry.Note);
        Assert.DoesNotContain(p.Vars, v => v.Name == neverProbed);
    }
}
