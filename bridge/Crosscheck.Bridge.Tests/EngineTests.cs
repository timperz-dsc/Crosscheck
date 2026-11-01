using System.Text.Json.Nodes;

namespace Crosscheck.Bridge.Tests;

/// <summary>Change detection per class: what a pilot action in this cockpit sends.</summary>
public class ChangeDetectionTests : IAsyncLifetime
{
    private readonly EngineHarness _h = new();

    public Task InitializeAsync() => _h.StartAsync(pf: true);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Verified_switch_sends_the_absolute_value()
    {
        _h.Sim.PilotSet("S_OH_ELEC_APU_MASTER", 1);
        await Wait.Until(() => _h.Commands.Count == 1, "one command");
        var c = _h.Commands[0];
        Assert.Equal("S_OH_ELEC_APU_MASTER", c["var"]!.GetValue<string>());
        Assert.Equal("verified", c["class"]!.GetValue<string>());
        Assert.Equal(1, c["value"]!.GetValue<double>());
        Assert.Equal(150, c["settle_ms"]!.GetValue<int>()); // from verify in the profile
    }

    [Fact]
    public async Task Analog_knob_sends_the_value()
    {
        _h.Sim.PilotSet("A_OH_PNEUMATIC_COCKPIT_TEMP", 0.42);
        await Wait.Until(() => _h.Commands.Count == 1, "one command");
        Assert.Equal(0.42, _h.Commands[0]["value"]!.GetValue<double>());
    }

    [Fact]
    public async Task Encoder_sends_only_the_change()
    {
        _h.Sim.PilotTurn("E_FCU_ALTITUDE", 3);
        await Wait.Until(() => _h.Commands.Count == 1, "one command");
        var c = _h.Commands[0];
        Assert.Equal("delta", c["class"]!.GetValue<string>());
        Assert.Equal(3, c["delta"]!.GetValue<double>());
        Assert.Null(c["value"]);
        await Task.Delay(100);
        Assert.Single(_h.Commands); // the display var changing is not a command
    }

    [Fact]
    public async Task Pulse_sends_one_command_per_press()
    {
        await _h.Sim.PilotPress("S_CDU1_KEY_FPLN", holdMs: 30);
        await Task.Delay(60);
        var c = Assert.Single(_h.Commands);
        Assert.Equal("pulse", c["class"]!.GetValue<string>());
        Assert.Equal(1, c["value"]!.GetValue<double>());
        Assert.Equal(600 + 150, c["settle_ms"]!.GetValue<int>()); // verify settle + pulse_ms
    }

    [Fact]
    public async Task Observe_vars_are_never_sent()
    {
        _h.Sim.PilotSet("I_MIP_GEAR_1_L", 1);
        _h.Sim.Logic("N_FCU_ALTITUDE", 35000);
        await Task.Delay(80);
        Assert.Empty(_h.Commands);
    }
}

public class ChangeDetectionStartTests
{
    [Fact]
    public async Task Nothing_is_sent_before_a_session_exists_or_for_initial_values()
    {
        await using var h = new EngineHarness();
        h.Sim.Logic("S_OH_ELEC_APU_MASTER", 1); // state before the bridge started
        h.Engine.Start();
        await h.Sim.StartAsync(h.Engine.Plan, CancellationToken.None);
        await Task.Delay(40);
        h.Sim.PilotSet("S_FCU_EFIS1_ND_MODE", 2); // no session yet
        await Task.Delay(40);
        Assert.Empty(h.Commands);
    }
}

/// <summary>Applying remote commands without ever sending their read-back back (echo).</summary>
public class ApplyAndEchoTests : IAsyncLifetime
{
    private readonly EngineHarness _h = new();

    public Task InitializeAsync() => _h.StartAsync(pf: false);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Remote_switch_is_written_verified_acked_and_not_echoed()
    {
        var id = _h.Remote("S_OH_ELEC_APU_MASTER", "verified", 1);
        var ack = await _h.WaitAck(id);
        Assert.True(ack["ok"]!.GetValue<bool>());
        Assert.Equal(1, ack["observed"]!.GetValue<double>());
        Assert.Equal(1, _h.Sim.Get("I_OH_ELEC_APU_MASTER_L")); // effect var followed
        await Task.Delay(100);
        Assert.Empty(_h.Commands);
        Assert.True(_h.Engine.EchoSuppressed >= 1);
    }

    [Fact]
    public async Task Remote_encoder_deltas_add_to_the_counter_and_are_not_echoed()
    {
        var ids = new[] { _h.Remote("E_FCU_ALTITUDE", "delta", delta: 2, settleMs: 100), _h.Remote("E_FCU_ALTITUDE", "delta", delta: 3, settleMs: 100), _h.Remote("E_FCU_ALTITUDE", "delta", delta: -1, settleMs: 100) };
        foreach (var id in ids) await _h.WaitAck(id);
        Assert.Equal(4, _h.Sim.Get("E_FCU_ALTITUDE"));
        await Wait.Until(() => _h.Sim.Get("N_FCU_ALTITUDE") == 4, "display follows");
        Assert.Equal(4, _h.AckFor(ids[2])!["observed"]!.GetValue<double>());
        Assert.Equal([2.0, 5.0, 4.0], _h.Sim.Writes.Where(w => w.Name == "E_FCU_ALTITUDE").Select(w => w.Value));
        await Task.Delay(100);
        Assert.Empty(_h.Commands);
    }

    [Fact]
    public async Task Remote_pulse_writes_1_then_0_after_pulse_ms_and_is_not_echoed()
    {
        var id = _h.Remote("S_CDU1_KEY_FPLN", "pulse", 1);
        var ack = await _h.WaitAck(id, 3000);
        Assert.True(ack["ok"]!.GetValue<bool>());
        Assert.Equal([1.0, 0.0], _h.Sim.Writes.Where(w => w.Name == "S_CDU1_KEY_FPLN").Select(w => w.Value));
        Assert.Equal("FPLN", _h.Sim.McduText(1));
        Assert.Equal(0, _h.Sim.Get("S_CDU1_KEY_FPLN"));
        await Task.Delay(100);
        Assert.Empty(_h.Commands);
    }

    [Fact]
    public async Task Remote_press_counter_only_ever_counts_up()
    {
        _h.Sim.Logic("S_MIP_MASTER_CAUTION_CAPT", 6); // three earlier presses
        await Wait.Until(() => _h.Sim.Get("S_MIP_MASTER_CAUTION_CAPT") == 6, "start value");
        await Task.Delay(50);
        var before = _h.Commands.Count;
        var ack = await _h.WaitAck(_h.Remote("S_MIP_MASTER_CAUTION_CAPT", "pulse", 1), 3000);
        Assert.True(ack["ok"]!.GetValue<bool>());
        var writes = _h.Sim.Writes.Where(w => w.Name == "S_MIP_MASTER_CAUTION_CAPT").Select(w => w.Value).ToList();
        Assert.Equal([7.0, 8.0], writes); // press, release: never a decrement
        Assert.Equal(8, _h.Sim.Get("S_MIP_MASTER_CAUTION_CAPT"));
        await Task.Delay(100);
        Assert.Equal(before, _h.Commands.Count);
    }

    [Fact]
    public async Task Remote_delta_without_display_is_confirmed_by_read_back_only()
    {
        var ack = await _h.WaitAck(_h.Remote("E_MIP_CLOCK_SET", "delta", delta: 2, settleMs: 50));
        Assert.True(ack["ok"]!.GetValue<bool>());
        Assert.Equal(2, ack["observed"]!.GetValue<double>()); // its own counter, read back
        Assert.Equal([2.0], _h.Sim.Writes.Where(w => w.Name == "E_MIP_CLOCK_SET").Select(w => w.Value));
        await Task.Delay(100);
        Assert.Empty(_h.Commands);
    }

    [Fact]
    public async Task A_pilot_click_in_the_same_instant_as_a_remote_delta_is_not_lost()
    {
        _h.Sim.PilotTurn("E_FCU_ALTITUDE", 1); // not reported yet (next frame)
        var id = _h.Remote("E_FCU_ALTITUDE", "delta", delta: 2);
        await _h.WaitAck(id);
        await Wait.Until(() => _h.Commands.Count == 1, "the pilot's click is sent");
        Assert.Equal(1, _h.Commands[0]["delta"]!.GetValue<double>());
        Assert.Equal(3, _h.Sim.Get("E_FCU_ALTITUDE"));
        await Task.Delay(100);
        Assert.Single(_h.Commands); // and only once
    }

    [Fact]
    public async Task A_pilot_action_right_after_a_remote_write_is_still_sent()
    {
        var id = _h.Remote("S_FCU_EFIS1_ND_MODE", "verified", 2);
        await _h.WaitAck(id);
        _h.Sim.PilotSet("S_FCU_EFIS1_ND_MODE", 3);
        await Wait.Until(() => _h.Commands.Count == 1, "pilot action sent");
        Assert.Equal(3, _h.Commands[0]["value"]!.GetValue<double>());
    }

    [Fact]
    public async Task Hundred_rapid_remote_changes_produce_no_echo()
    {
        var ids = new List<string>();
        for (var i = 0; i < 100; i++)
        {
            ids.Add(_h.Remote("S_FCU_EFIS1_ND_MODE", "verified", i % 5, settleMs: 5));
            ids.Add(_h.Remote("E_FCU_ALTITUDE", "delta", delta: 1, settleMs: 5));
            if (i % 10 == 0) await Task.Delay(7); // let some frames through mid-burst
        }
        await Wait.Until(() => ids.All(id => _h.AckFor(id) is not null), "all acks", 20000);
        await Task.Delay(200);
        Assert.Empty(_h.Commands);
        Assert.Equal(4, _h.Sim.Get("S_FCU_EFIS1_ND_MODE"));
        Assert.Equal(100, _h.Sim.Get("E_FCU_ALTITUDE"));
    }

    [Fact]
    public async Task A_resent_command_is_applied_once_and_acked_again()
    {
        var id = _h.Remote("E_FCU_ALTITUDE", "delta", delta: 5);
        await _h.WaitAck(id);
        _h.Remote("E_FCU_ALTITUDE", "delta", delta: 5, id: id); // server re-send
        await Wait.Until(() => _h.Channel.SentOf("ack").Count(a => a["id"]!.GetValue<string>() == id) == 2, "second ack");
        Assert.Equal(5, _h.Sim.Get("E_FCU_ALTITUDE"));
    }

    [Fact]
    public async Task A_value_that_refuses_to_change_is_acked_not_ok()
    {
        _h.Sim.Refuse("S_OH_ELEC_APU_MASTER");
        var id = _h.Remote("S_OH_ELEC_APU_MASTER", "verified", 1);
        var ack = await _h.WaitAck(id);
        Assert.False(ack["ok"]!.GetValue<bool>());
        Assert.Equal(0, ack["observed"]!.GetValue<double>());
    }

    [Fact]
    public async Task An_older_remote_command_while_an_own_command_is_unaccepted_is_superseded()
    {
        _h.Sim.PilotSet("S_FCU_EFIS1_ND_MODE", 1); // own command, not accepted yet
        await Wait.Until(() => _h.Commands.Count == 1, "own command");
        var id = _h.Remote("S_FCU_EFIS1_ND_MODE", "verified", 3);
        var ack = await _h.WaitAck(id);
        Assert.True(ack["superseded"]!.GetValue<bool>());
        Assert.Equal(1, _h.Sim.Get("S_FCU_EFIS1_ND_MODE")); // own later command wins
    }

    [Fact]
    public async Task Sim_not_connected_acks_not_ok()
    {
        _h.Sim.SetConnected(false);
        var id = _h.Remote("S_OH_ELEC_APU_MASTER", "verified", 1);
        var ack = await _h.WaitAck(id);
        Assert.False(ack["ok"]!.GetValue<bool>());
        Assert.Null(ack["observed"]);
    }

    [Fact]
    public async Task Analog_effect_is_checked_in_GraphQL_with_the_scale()
    {
        var id = _h.Remote("A_OH_PNEUMATIC_COCKPIT_TEMP", "analog", 0.5);
        var ack = await _h.WaitAck(id);
        Assert.True(ack["ok"]!.GetValue<bool>());
        Assert.Equal(512.0, await _h.Gql.GetAsync("system.analog.A_OH_PNEUMATIC_COCKPIT_TEMP"));
    }
}

/// <summary>Corrections, including the PF defence in depth.</summary>
public class CorrectionTests
{
    [Fact]
    public async Task As_PF_a_correction_that_is_not_from_the_command_log_is_refused_and_not_written()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        var id = h.Correction("S_OH_ELEC_APU_MASTER", "verified", 1, "pf_reference");
        var ack = await h.WaitAck(id);
        Assert.False(ack["ok"]!.GetValue<bool>());
        Assert.Empty(h.Sim.Writes);
        Assert.Equal(1, h.Engine.CorrectionsRefused);
        Assert.Contains(h.Hub.Recent, e => e.Type == "correction_refused");
    }

    [Fact]
    public async Task As_PF_a_command_log_correction_is_applied()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        var ack = await h.WaitAck(h.Correction("S_OH_ELEC_APU_MASTER", "verified", 1, "command_log"));
        Assert.True(ack["ok"]!.GetValue<bool>());
        Assert.Equal(1, h.Sim.Get("S_OH_ELEC_APU_MASTER"));
        await Task.Delay(80);
        Assert.Empty(h.Commands); // not echoed
    }

    [Fact]
    public async Task As_PM_corrections_are_applied_and_an_encoder_is_turned_to_the_target_display()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        h.Sim.Logic("E_FCU_ALTITUDE", 40);
        h.Sim.Logic("N_FCU_ALTITUDE", 7);
        await Task.Delay(30);
        var ack = await h.WaitAck(h.Correction("E_FCU_ALTITUDE", "delta", 10, "pf_reference"));
        Assert.True(ack["ok"]!.GetValue<bool>());
        Assert.Equal(43, h.Sim.Get("E_FCU_ALTITUDE")); // +3 clicks
        await Wait.Until(() => h.Sim.Get("N_FCU_ALTITUDE") == 10, "display at target");
    }

    [Fact]
    public async Task A_correction_outside_the_range_is_refused_by_the_guard()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        var ack = await h.WaitAck(h.Correction("A_OH_PNEUMATIC_COCKPIT_TEMP", "analog", 7, "pf_reference"));
        Assert.False(ack["ok"]!.GetValue<bool>());
        Assert.Empty(h.Sim.Writes);
        Assert.Contains(h.Hub.Recent, e => e.Type == "safety_refused");
    }
}

public class SnapshotTests
{
    [Fact]
    public async Task Group_values_use_display_for_encoders_mcdu_screens_and_datarefs()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        h.Sim.Logic("N_FCU_ALTITUDE", 120);
        await Task.Delay(30);
        var fcu = h.Engine.GroupValues("fcu");
        Assert.Contains(fcu, v => v.Var == "E_FCU_ALTITUDE" && v.Class == "delta" && (double)v.Value! == 120);
        Assert.Contains(h.Engine.GroupValues("cdu1"), v => v.Var == "aircraft.mcdu1.display" && v.Value is string);
        Assert.Contains(h.Engine.GroupValues("systems"), v => v.Var == "aircraft.fuel.left.amount.kg" && (double)v.Value! == 1500);
        Assert.Empty(h.Engine.GroupValues("cabin")); // nothing in that group
    }

    [Fact]
    public async Task Answers_snapshot_requests_per_group()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        h.Channel.Inject("snapshot_request", new JsonObject { ["request_id"] = "r9", ["groups"] = null, ["reason"] = "join" }, h.Channel.NextSeq());
        var answers = h.Channel.SentOf("snapshot_values");
        Assert.Equal(["cdu1", "efis", "fcu", "flightcontrols", "mip", "overhead", "systems"], answers.Select(a => a["group"]!.GetValue<string>()).Order());
        Assert.All(answers, a => Assert.Equal("r9", a["request_id"]!.GetValue<string>()));
    }
}
