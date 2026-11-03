using Crosscheck.Bridge.Sim;

namespace ProfileProbe.Tests;

/// <summary>The supervised write test (gate test 3 prep): what it may write, and how it behaves, against the fake sim.</summary>
public class WriteTestTests
{
    static WriteControl C(string id) => WriteTest.Controls.Single(c => c.Id == id);

    [Fact]
    public void Only_the_approved_controls_with_values_seen_in_the_cockpit()
    {
        // TRAY: the CRO-40 check of the bridge write path (owner approved, gate test 3 section 8).
        Assert.Equal(["FLAPS", "SPEEDBRAKE", "ANTI_SKID", "DOOR", "FCU_SPD", "TRAY"], WriteTest.Controls.Select(c => c.Id));
        Assert.Equal([0, 1], C("FLAPS").Allowed);
        Assert.Equal([0, 1, 2.0234375], C("SPEEDBRAKE").Allowed);
        Assert.Equal([0, 1], C("ANTI_SKID").Allowed);
        Assert.Equal([0, 1], C("DOOR").Allowed);
        Assert.Equal([-1, 0, 1], C("FCU_SPD").Allowed);
        Assert.Equal([0, 1], C("TRAY").Allowed);
        Assert.Equal("S_TRAY_TABLE_CAPT", C("TRAY").Var);
        // Every step writes an allowed value and every plan ends where it started.
        foreach (var (c, start) in new[] { (C("FLAPS"), 0.0), (C("SPEEDBRAKE"), 1.0), (C("ANTI_SKID"), 1.0), (C("DOOR"), 1.0), (C("FCU_SPD"), 0.0), (C("TRAY"), 0.0), (C("TRAY"), 1.0) })
        {
            var steps = c.Plan(start)!;
            Assert.All(steps, s => Assert.Contains(s.Value, c.Allowed));
            Assert.Equal(start, steps[^1].Value);
        }
        // Another resting value is not a start this test accepts (nothing written).
        Assert.Null(C("FLAPS").Plan(2));
        // The door: 0 = closed, 1 = open (owner, write test 2026-11-03); from either position to the other and back.
        Assert.Equal([1.0, 0.0], C("DOOR").Plan(0)!.Select(st => st.Value));
        Assert.Equal("open", C("DOOR").Plan(0)![0].Means);
        Assert.Equal(1, C("DOOR").Plan(0)![0].Expect!["I_PED_COCKPIT_DOOR_U"]);
        Assert.Equal([0.0, 1.0], C("DOOR").Plan(1)!.Select(st => st.Value));
        Assert.Null(C("FCU_SPD").Plan(1));
        Assert.True(C("FLAPS").DryHydraulics);
        Assert.NotNull(C("FCU_SPD").Before);
        Assert.True(C("FCU_SPD").StopWhenModelFails);
        // Never the deny list or the controls the tests must never press.
        string[] never = ["FIRE", "AGENT", "IDG", "ENG_MASTER", "EVAC", "MASK_MAN_ON", "DITCHING", "RAT_MAN_ON", "EMER_GEN", "INST_DISCONNECT"];
        Assert.All(WriteTest.Controls, c => Assert.DoesNotContain(never, n => c.Var.Contains(n, StringComparison.Ordinal)));
    }

    static Func<CancellationToken, Task<string?>> Pilot(params string[] answers)
    {
        var q = new Queue<string>(answers);
        return _ => Task.FromResult<string?>(q.Count > 0 ? q.Dequeue() : "");
    }

    static async Task<(Rig Rig, string Results)> Setup(string id)
    {
        var c = C(id);
        var rig = await ProfileProbe.Tests.Rig.Make(WriteTest.Targets([c]), [.. c.Effects]);
        return (rig, Path.Combine(rig.Dir, "writetest-results.md"));
    }

    [Fact]
    public async Task Speed_brake_every_step_sticks_and_it_ends_at_ret()
    {
        var (rig, results) = await Setup("SPEEDBRAKE");
        await using var _ = rig;
        rig.Sim.Logic("A_FC_SPEEDBRAKE", 1);
        await WriteTest.Run(rig.Session, [C("SPEEDBRAKE")], Pilot(), results, default);
        Assert.Equal([2.0234375, 1, 0, 1], rig.Sim.WritesOf("A_FC_SPEEDBRAKE"));
        Assert.Equal(1, rig.Sim.Get("A_FC_SPEEDBRAKE"));
        var text = File.ReadAllText(results);
        Assert.True(text.Contains("1 -> 2.0234375 (half): STUCK for 2.5 s"), text);
        Assert.Contains("1 -> 0 (ARM): STUCK", text);
        Assert.Contains("back at 1 (where it started)", text);
    }

    [Fact]
    public async Task A_write_that_does_not_stick_is_reported_and_the_test_goes_on()
    {
        var (rig, results) = await Setup("ANTI_SKID");
        await using var _ = rig;
        rig.Sim.Logic("S_FC_MIP_ANTI_SKID", 1);
        rig.Sim.Stick("S_FC_MIP_ANTI_SKID", 1, revertMs: 20); // Fenix puts it back, as in gate test 2
        await WriteTest.Run(rig.Session, [C("ANTI_SKID")], Pilot(), results, default);
        var text = File.ReadAllText(results);
        Assert.Contains("1 -> 0 (OFF): did NOT stick", text);
        Assert.Contains("back at 1 (where it started)", text);
    }

    [Fact]
    public async Task Fcu_spd_model_that_holds_pull_plus_one_and_push_minus_one_with_the_lamp()
    {
        var (rig, results) = await Setup("FCU_SPD");
        await using var _ = rig;
        rig.Sim.Logic("S_FCU_SPEED", 0);
        rig.Sim.Logic("I_FCU_SPEED_MANAGED", 1);
        rig.Sim.Logic("B_FCU_SPEED_DASHED", 1);
        using var stop = new CancellationTokenSource();
        // Like Fenix in the positions runs: selected while the knob var is 1, managed at 0.
        var fenix = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var managed = rig.Sim.Get("S_FCU_SPEED") == 0 ? 1 : 0;
                rig.Sim.Logic("I_FCU_SPEED_MANAGED", managed);
                rig.Sim.Logic("B_FCU_SPEED_DASHED", managed);
                await Task.Delay(5);
            }
        });
        await WriteTest.Run(rig.Session, [C("FCU_SPD")], Pilot("", "", ""), results, default);
        stop.Cancel();
        await fenix;
        Assert.Equal([1, 0], rig.Sim.WritesOf("S_FCU_SPEED"));
        var text = File.ReadAllText(results);
        Assert.Contains("expected I_FCU_SPEED_MANAGED 0, B_FCU_SPEED_DASHED 0: yes", text);
        Assert.Contains("expected I_FCU_SPEED_MANAGED 1, B_FCU_SPEED_DASHED 1: yes", text);
    }

    [Fact]
    public async Task Fcu_spd_model_that_fails_tries_no_other_value_and_puts_it_back()
    {
        var (rig, results) = await Setup("FCU_SPD");
        await using var _ = rig;
        rig.Sim.Logic("S_FCU_SPEED", 0);
        rig.Sim.Logic("I_FCU_SPEED_MANAGED", 1);
        rig.Sim.Logic("B_FCU_SPEED_DASHED", 1);
        await WriteTest.Run(rig.Session, [C("FCU_SPD")], Pilot("", ""), results, default);
        // +1 was written, the lamp did not follow: no -1 test step, only the restore to 0.
        Assert.Equal([1, 0], rig.Sim.WritesOf("S_FCU_SPEED"));
        Assert.Equal(0, rig.Sim.Get("S_FCU_SPEED"));
        var text = File.ReadAllText(results);
        Assert.Contains("expected I_FCU_SPEED_MANAGED 0, B_FCU_SPEED_DASHED 0: NO", text);
        Assert.Contains("the model does not hold: no other values are tried", text);
    }

    [Fact]
    public async Task Q_stops_and_the_control_is_put_back()
    {
        var (rig, results) = await Setup("SPEEDBRAKE");
        await using var _ = rig;
        rig.Sim.Logic("A_FC_SPEEDBRAKE", 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteTest.Run(rig.Session, [C("SPEEDBRAKE")], Pilot("", "q"), results, default));
        Assert.Equal([2.0234375, 1], rig.Sim.WritesOf("A_FC_SPEEDBRAKE")); // half, then the restore to RET
        Assert.Equal(1, rig.Sim.Get("A_FC_SPEEDBRAKE"));
        Assert.Contains("stopped (by the pilot); back at 1 (where it started)", File.ReadAllText(results));
    }

    [Fact]
    public async Task An_unexpected_value_stops_at_once_and_the_control_is_put_back()
    {
        var (rig, results) = await Setup("DOOR");
        await using var _ = rig;
        rig.Sim.Logic("S_COCKPIT_DOOR", 1);
        rig.Sim.Logic("I_PED_COCKPIT_DOOR_U", 1);
        // Once the door is written open, something else sets a value nobody wrote.
        var odd = Task.Run(async () =>
        {
            for (var i = 0; i < 400 && rig.Sim.Get("S_COCKPIT_DOOR") != 0; i++) await Task.Delay(5);
            rig.Sim.Logic("S_COCKPIT_DOOR", 0.5);
        });
        await Assert.ThrowsAsync<SafetyStopException>(() => WriteTest.Run(rig.Session, [C("DOOR")], Pilot(), results, default));
        await odd;
        Assert.Equal(1, rig.Sim.Get("S_COCKPIT_DOOR")); // put back
        Assert.Contains("unexpected: S_COCKPIT_DOOR read 0.5", File.ReadAllText(results) + rig.Out);
    }

    [Fact]
    public async Task Fenix_that_stops_answering_stops_the_test()
    {
        var (rig, results) = await Setup("ANTI_SKID");
        await using var _ = rig;
        rig.Sim.Logic("S_FC_MIP_ANTI_SKID", 1);
        rig.Gql.Available = false;
        await Assert.ThrowsAsync<SafetyStopException>(() => WriteTest.Run(rig.Session, [C("ANTI_SKID")], Pilot(), results, default));
        Assert.Equal(1, rig.Sim.Get("S_FC_MIP_ANTI_SKID"));
    }

    [Fact]
    public async Task Flaps_are_only_moved_with_dry_hydraulics()
    {
        var (rig, results) = await Setup("FLAPS");
        await using var _ = rig;
        rig.Sim.Logic("S_FC_FLAPS", 0);
        foreach (var p in SafetyVars.HydraulicPressures) rig.Gql.Datarefs[p] = 3000.0;
        await WriteTest.Run(rig.Session, [C("FLAPS")], Pilot(), results, default);
        Assert.Empty(rig.Sim.WritesOf("S_FC_FLAPS"));
        Assert.Contains("hydraulics are pressurised", File.ReadAllText(results));
    }
}
