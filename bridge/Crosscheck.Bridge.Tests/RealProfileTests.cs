using Crosscheck.Bridge.Sim;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// CRO-27: the classes and limits in the shipped Fenix profile that gate test 1 showed were wrong.
/// These read profiles/fenix-a320/profile.yaml itself, not the test fixture.
/// </summary>
public class RealProfileTests
{
    private static readonly Profile Real =
        ProfileLoader.LoadFile(Path.Combine(Repo.Root, "profiles", "fenix-a320", "profile.yaml")).Profile!;

    private static VarEntry Var(string name) => Real.Vars.Single(v => v.Name == name);

    [Theory]
    [InlineData("S_OH_HYD_YELLOW_ELEC_PUMP")]
    [InlineData("S_OH_ELEC_BUSTIE")]
    [InlineData("S_OH_NAV_ADR1")]
    [InlineData("S_OH_NAV_IR3_SWITCH")]
    [InlineData("S_OH_LIGHTING_AVIONICS_COMPT")]
    [InlineData("S_WR_GCS")]
    [InlineData("S_PED_ATC_0")]
    [InlineData("S_PED_ATC_3")]
    public void Latching_push_buttons_are_edge_buttons(string name)
    {
        var v = Var(name);
        Assert.Equal(SyncClass.Pulse, v.Class);
        Assert.Equal(PressMode.Edge, v.Press);
        Assert.Null(v.Values);
        Assert.Null(v.PulseMs);
    }

    [Fact]
    public void Every_push_button_in_the_profile_mirrors_edges()
    {
        // CRO-29: every Fenix S_ push button counts +1 on press and +1 on release (observe-report.md).
        var pulses = Real.Vars.Where(v => v.Class == SyncClass.Pulse).ToList();
        Assert.True(pulses.Count > 400);
        Assert.All(pulses, v => Assert.Equal(PressMode.Edge, v.Press));
        Assert.All(pulses, v => Assert.Null(v.PulseMs));
    }

    [Theory]
    [InlineData("S_FCU_AP1", "I_FCU_AP1")]
    [InlineData("S_FCU_EFIS1_CSTR", "I_FCU_EFIS1_CSTR")]
    [InlineData("S_FCU_EFIS2_CSTR", "I_FCU_EFIS2_CSTR")]
    [InlineData("S_ASP4_VOICE", "I_ASP4_VOICE")]
    [InlineData("S_ASP_VHF_1_SEND", "I_ASP_VHF_1_SEND")]
    [InlineData("S_MIP_CLOCK_RST", null)]
    [InlineData("S_MIP_CHRONO_CAPT", null)]
    public void Observed_and_same_panel_push_buttons_left_the_review_list(string name, string? lamp)
    {
        var v = Var(name);
        Assert.Equal(PressMode.Edge, v.Press);
        Assert.Equal(lamp, v.Verify?.Lvar);
    }

    [Theory]
    [InlineData("S_FCU_SPEED")] // push/pull knob: a counter mirrored as a change (delta), never an edge button
    [InlineData("S_FC_THR_INST_DISCONNECT1")]
    [InlineData("S_OH_FUEL_XFEED")] // a 0/1 latch (positions run)
    [InlineData("S_FCU_ALTITUDE_SCALE")] // switch
    [InlineData("S_FCU_EFIS1_NAV1")] // ADF/VOR switch
    [InlineData("S_MIP_CLOCK_ET")] // switch
    [InlineData("S_ASP_INTRAD")] // switch
    [InlineData("S_ASP_SPKR_REC")] // unclear
    [InlineData("S_OH_INT_LT_EMER")] // EMER EXIT LT: a 3-position switch (positions 0, 1, 2)
    [InlineData("S_OH_HYD_BLUE_ELEC_PUMP")] // a 0/1 latch (gate test 2 cause B)
    public void Knobs_switches_and_unclear_controls_stay_off_the_edge_rule(string name) =>
        Assert.DoesNotContain(Real.Vars, v => v.Name == name && v.Class == SyncClass.Pulse);

    [Fact]
    public void Blue_elec_pump_is_a_latch_with_its_lamp_as_the_check()
    {
        var v = Var("S_OH_HYD_BLUE_ELEC_PUMP");
        Assert.Equal(SyncClass.Verified, v.Class);
        Assert.Equal([0.0, 1.0], v.Values);
        Assert.Equal("I_OH_HYD_BLUE_ELEC_PUMP_L", v.Verify?.Lvar);
    }

    /// <summary>The owner's deny list (gate test 2 section 7): exactly these, never written on any path.</summary>
    public static readonly string[] DenyList =
    [
        "S_OH_ELEC_IDG1", "S_OH_ELEC_IDG2",
        "S_OH_FIRE_ENG1_BUTTON", "S_OH_FIRE_ENG2_BUTTON", "S_OH_FIRE_APU_BUTTON",
        "S_OH_FIRE_ENG1_AGENT1", "S_OH_FIRE_ENG1_AGENT2", "S_OH_FIRE_ENG2_AGENT1", "S_OH_FIRE_ENG2_AGENT2", "S_OH_FIRE_APU_AGENT",
        "S_OH_CARGO_DISC_1_OLD_LAYOUT", "S_OH_CARGO_DISC_2_OLD_LAYOUT",
        "S_OH_HYD_RAT_MAN_ON", "S_OH_OXYGEN_MASK_MAN_ON",
    ];

    [Fact]
    public void The_deny_list_is_exactly_the_owners_list_and_the_guard_refuses_every_value()
    {
        Assert.Equal(DenyList.Order(), Real.Vars.Where(v => v.NeverWrite).Select(v => v.Name).Order());
        var guard = new SimSafetyGuard(Real);
        foreach (var name in DenyList)
        {
            Assert.True(guard.IsDenied(name));
            Assert.False(guard.IsWritable(name));
            foreach (var value in new[] { 0.0, 1, 2, 3 })
                Assert.Throws<SafetyViolationException>(() => guard.CheckWrite(name, value));
        }
        // Not on the list: reversible, they keep syncing (owner decision). Covers keep syncing too.
        foreach (var name in new[] { "S_ENG_MASTER_1", "S_ENG_MASTER_2", "S_OH_PNEUMATIC_DITCHING", "S_OH_EVAC_COMMAND",
                     "S_OH_ELEC_IDG1_Cover", "S_OH_FIRE_ENG1_BUTTON_Cover", "S_OH_HYD_RAT_MAN_ON_Cover", "S_OH_OXYGEN_MASK_MAN_ON_Cover" })
        {
            Assert.False(Var(name).NeverWrite, name);
            Assert.True(guard.IsWritable(name), name);
        }
    }

    [Theory]
    [InlineData("S_FCU_EFIS1_BARO_STD")]
    [InlineData("S_FCU_EFIS2_BARO_STD")]
    public void Counters_profiled_as_switches_are_not_synced_until_an_observe_run(string name) // door video: done, now an edge button
    {
        Assert.DoesNotContain(Real.Vars, v => v.Name == name);
        var list = UnsyncedList.LoadNextTo(Path.Combine(Repo.Root, "profiles", "fenix-a320", "profile.yaml"), Real);
        Assert.Contains(name, list.Labels.Keys);
    }

    [Theory]
    [InlineData("S_ECAM_CLR_LEFT")]
    [InlineData("S_ECAM_CLR_RIGHT")]
    public void Ecam_clr_lamps_are_no_toggle_check(string name) => Assert.Null(Var(name).Verify);

    [Theory]
    [InlineData("E_FCU_ALTITUDE", "B_FCU_POWER")]
    [InlineData("E_FCU_HEADING", "B_FCU_POWER")]
    [InlineData("E_FCU_SPEED", "B_FCU_POWER")]
    [InlineData("E_FCU_VS", "B_FCU_POWER")]
    [InlineData("E_FCU_EFIS1_BARO", "B_FCU_POWER")]
    [InlineData("E_PED_RMP1_INNER", "B_PED_RMP1_POWER")]
    [InlineData("E_PED_RMP2_OUTER", "B_PED_RMP2_POWER")]
    public void Fcu_and_rmp_displays_are_compared_only_while_powered(string name, string power) =>
        Assert.Equal(power, Var(name).CompareWhen?.Powered);

    [Theory]
    [InlineData("S_OH_ELEC_EXT_PWR", "I_OH_ELEC_EXT_PWR_L")] // scan value 2: one press and release counted
    [InlineData("S_OH_ELEC_APU_START", null)] // lamps follow the start sequence, not the press
    [InlineData("S_OH_RCRD_GND_CTL", "I_OH_RCRD_GND_CTL_L")] // positions run: the lamp came on with the press
    [InlineData("S_PED_RMP1_VHF1", null)] // radio group: no toggle check
    [InlineData("S_PED_RMP3_VHF3", null)]
    [InlineData("S_MIP_AUTOBRAKE_LO", null)]
    [InlineData("S_MISC_WIPER_REPELLENT_CAPT", null)] // gate test 1 counted 0 -> 1 -> 2 for one click
    public void Overhead_and_pedestal_push_buttons_left_the_review_list(string name, string? lamp)
    {
        var v = Var(name);
        Assert.Equal(PressMode.Edge, v.Press);
        Assert.Equal(lamp, v.Verify?.Lvar);
    }

    [Fact]
    public void Spring_switches_have_a_rest_value_and_the_FCU_indicators_are_inputs()
    {
        Assert.Equal(1, Var("S_FC_RUDDER_TRIM").Rest);
        Assert.Equal(1, Var("S_OH_PNEUMATIC_PRESS_MAN").Rest);
        Assert.Equal([0.0, 1, 2, 3, 4], Var("S_FC_FLAPS").Values);
        foreach (var n in new[] { "I_FCU_SPEED_MANAGED", "B_FCU_SPEED_DASHED", "I_FCU_HEADING_MANAGED", "B_FCU_HEADING_DASHED", "I_FCU_ALTITUDE_MANAGED", "B_FCU_VERTICALSPEED_DASHED" })
            Assert.Contains("managed differs between the cockpits", Var(n).Alarm!.Text);
        // CRO-32 (supervised write test 2026-11-03): the push/pull is a counter, pull +1 and push -1. Mirrored as
        // the change only (delta), never corrected, checked by read-back of the own var; one line with its knob.
        foreach (var (knob, encoder) in new[] { ("S_FCU_SPEED", "E_FCU_SPEED"), ("S_FCU_HEADING", "E_FCU_HEADING"),
                     ("S_FCU_ALTITUDE", "E_FCU_ALTITUDE"), ("S_FCU_VERTICAL_SPEED", "E_FCU_VS") })
        {
            var v = Var(knob);
            Assert.Equal(SyncClass.Delta, v.Class);
            Assert.Null(v.Display); // no per-click lamp or display check (ALT: read-back only, owner)
            Assert.Null(v.Verify);
            Assert.Equal(encoder, v.Control);
        }
    }

    [Fact]
    public void Write_test_2026_11_03_door_and_switch_labels()
    {
        // The door: 0 closed, 1 open (the owner saw it); the OPEN light follows and is its check.
        var door = Var("S_COCKPIT_DOOR");
        Assert.Equal("CLOSED", door.ValueLabels!["0"]);
        Assert.Equal("OPEN", door.ValueLabels!["1"]);
        Assert.Equal("I_PED_COCKPIT_DOOR_U", door.Verify!.Lvar);
        Assert.Equal("OFF", Var("S_FC_MIP_ANTI_SKID").ValueLabels!["0"]);
        Assert.Equal([0, 3], Var("A_FC_SPEEDBRAKE").Range!);
    }

    [Fact]
    public void The_guard_lets_every_counter_value_from_gate_test_1_through()
    {
        var guard = new SimSafetyGuard(Real);
        for (var c = 1; c <= 28; c++) guard.CheckWrite("S_OH_HYD_YELLOW_ELEC_PUMP", c);
        for (var c = 1; c <= 8; c++) guard.CheckWrite("S_OH_ELEC_BUSTIE", c);
        Assert.Equal(0, guard.Refused);
        Assert.Throws<SafetyViolationException>(() => guard.CheckWrite("S_OH_HYD_YELLOW_ELEC_PUMP", 1.5));
    }

    [Theory]
    [InlineData("A_OH_PNEUMATIC_LDG_ELEV", -3, 9.75)] // gate test 2 (interim, owner confirmed)
    [InlineData("A_FC_SPEEDBRAKE", 0, 3)]             // 0 = ARM (positions runs 2026-11-03)
    [InlineData("A_WR_TILT", -15, 15)]                // gate test 2
    [InlineData("A_WR_GAIN", -5, 4)]                  // gate test 2
    public void Analog_ranges_come_from_the_gate_tests(string name, double min, double max)
    {
        Assert.Equal([min, max], Var(name).Range);
        var guard = new SimSafetyGuard(Real);
        guard.CheckWrite(name, min);
        guard.CheckWrite(name, max);
        Assert.Throws<SafetyViolationException>(() => guard.CheckWrite(name, max + 0.25));
        Assert.Throws<SafetyViolationException>(() => guard.CheckWrite(name, min - 0.25));
    }

    [Fact]
    public void Positions_run_2026_11_03_clear_results()
    {
        // positions-report-gate3.md: blocks 2 and 3 of that run (read correctly).
        foreach (var n in new[] { 1, 2 })
        {
            var mode = Var($"S_OH_ENG_N1_MODE_{n}");
            Assert.Equal(SyncClass.Verified, mode.Class);
            Assert.Equal([0, 1], mode.Values!);
            Assert.Equal($"I_OH_ENG_N1_MODE_{n}_L", mode.Verify!.Lvar);
        }
        string[] edges =
        [
            "S_MIP_AUTOBRAKE_MED", "S_MIP_AUTOBRAKE_MAX", "S_MIP_CLOCK_SET", "S_PED_COCKPIT_DOOR_VIDEO",
            .. new[] { 1, 2, 3 }.SelectMany(r => new[] { "HF1", "HF2", "AM", "NAV", "VOR", "ILS", "MLS", "ADF", "BFO" }.Select(k => $"S_PED_RMP{r}_{k}")),
        ];
        foreach (var name in edges)
        {
            var v = Var(name);
            Assert.True(v is { Class: SyncClass.Pulse, Press: PressMode.Edge, Verify: null }, name);
        }
        Assert.Equal([0, 1, 2], Var("S_DCDU1_DIM_BRT").Values!);
        Assert.Equal([0, 1, 2], Var("S_PED_COCKPIT_DOOR").Values!); // gate3b: LOCK 0, NORM 1, UNLOCK 2, springs back
        Assert.Equal(1, Var("S_PED_COCKPIT_DOOR").Rest);
        Assert.Equal(1, Var("S_DCDU1_DIM_BRT").Rest);
        foreach (var b in new[] { "ECAM_U", "ECAM_L", "CO", "CI", "CI_OUTER", "FO", "FI", "FI_OUTER" })
        {
            Assert.Equal(SyncClass.Analog, Var($"A_DISPLAY_BRIGHTNESS_{b}").Class);
            Assert.Equal([0, 1], Var($"A_DISPLAY_BRIGHTNESS_{b}").Range!);
        }
    }

    [Theory]
    [InlineData("S_OH_IN_LT_ICE", new double[] { 0, 1 })]
    [InlineData("S_MIP_LIGHT_CONSOLEFLOOR_CAPT", new double[] { 0, 1, 2 })]
    [InlineData("S_MIP_LIGHT_CONSOLEFLOOR_FO", new double[] { 0, 1, 2 })]
    [InlineData("S_CHART_LIGHT_TEMP_CAPT", new double[] { 0, 1 })]
    [InlineData("S_CHART_LIGHT_TEMP_FO", new double[] { 0, 1 })]
    [InlineData("S_EXT_CARGO_BAY_AFT_LT", new double[] { 0, 1 })]
    [InlineData("S_EXT_CARGO_BAY_FWD_LT", new double[] { 0, 1 })]
    [InlineData("S_EXT_CARGO_DOOR_SELECTOR_FWD", new double[] { 0, 1 })]
    [InlineData("S_EXT_CARGO_DOOR_SELECTOR_LOADING_AREA_LT_AFT", new double[] { 0, 1 })]
    [InlineData("S_EXT_CARGO_DOOR_SELECTOR_LOADING_AREA_LT_FWD", new double[] { 0, 1 })]
    public void Interim_values_from_gate_test_2(string name, double[] values)
    {
        // Pilot input in gate test 2, owner confirmed (gate test 3 prep). None of them kept counting up.
        Assert.Equal(values, Var(name).Values);
        var guard = new SimSafetyGuard(Real);
        foreach (var v in values) guard.CheckWrite(name, v);
        Assert.Throws<SafetyViolationException>(() => guard.CheckWrite(name, values[^1] + 1));
    }

    [Fact]
    public void No_analog_entry_has_a_graphql_check_until_the_scale_is_calibrated()
    {
        // The x 1024 scale did not hold in gate test 1 (CRO-28): analog writes are confirmed by read-back.
        Assert.DoesNotContain(Real.Vars, v => v.Class == SyncClass.Analog && v.Verify?.Graphql is not null);
        Assert.Contains(Real.Vars, v => v.Class == SyncClass.Analog);
    }

    [Theory]
    [InlineData("S_OH_EXT_LT_STROBE", new double[] { 0, 1, 2 })]
    [InlineData("S_OH_SIGNS_SMOKING", new double[] { 0, 1, 2 })]
    [InlineData("S_TCAS_RANGE", new double[] { 0, 1, 2, 3 })]
    [InlineData("S_WR_MODE", new double[] { 0, 1, 2, 3 })] // 0 added by the positions run
    [InlineData("S_COCKPIT_DOOR", new double[] { 0, 1 })]
    [InlineData("S_OH_CARGO_DISC_1_OLD_LAYOUT", new double[] { 0, 1, 2 })]
    [InlineData("S_OH_CARGO_DISC_2_OLD_LAYOUT", new double[] { 0, 1, 2 })]
    public void Positions_the_pilots_used_are_allowed(string name, double[] values) => Assert.Equal(values, Var(name).Values);

    [Theory]
    [InlineData("S_WINDOW_CAPT_LATCH")]
    [InlineData("A_FC_THROTTLE_LEFT_INPUT")]
    [InlineData("A_FC_THROTTLE_RIGHT_GATE")]
    [InlineData("N_FUEL_TOTAL_QTY")]
    [InlineData("N_CDU1_BRIGHTNESS")]
    [InlineData("N_MIP_LIGHTING_FLOOD_MAIN_L")]
    [InlineData("N_FC_RUDDER")]
    [InlineData("N_FC_SIDESTICK_CAPT_PITCH")]
    [InlineData("N_MIP_CLOCK_CHRONO")]
    public void Noise_and_hardware_axes_are_ignored(string name) => Assert.Equal(SyncClass.Ignore, Var(name).Class);

    [Theory]
    [InlineData("S_ASP_VOICE", "I_ASP_VOICE")]
    [InlineData("S_ASP2_VOICE", "I_ASP2_VOICE")]
    [InlineData("S_ASP3_VOICE", "I_ASP3_VOICE")]
    public void The_voice_key_is_a_pulse_checked_against_its_lamp(string name, string lamp)
    {
        var v = Var(name);
        Assert.Equal(SyncClass.Pulse, v.Class);
        Assert.Equal(PressMode.Edge, v.Press);
        Assert.Equal(lamp, v.Verify?.Lvar);
        Assert.Equal(SyncClass.Verified, Var(name + "_LATCH").Class);
    }

    [Theory]
    [InlineData("S_OH_SVCE_INT_OVRD")] // momentary: the lamp is on only while held (observe-report.md)
    [InlineData("S_OH_FIRE_ENG1_TEST")]
    [InlineData("S_OH_FIRE_APU_TEST")]
    [InlineData("S_OH_FIRE_ENG2_TEST")]
    [InlineData("S_FC_CAPT_INST_DISCONNECT")]
    [InlineData("S_FC_FO_INST_DISCONNECT")]
    [InlineData("S_MIP_MASTER_WARNING_CAPT")]
    public void Hold_buttons_are_mirrored_for_as_long_as_they_are_held(string name)
    {
        var v = Var(name);
        Assert.Equal(SyncClass.Pulse, v.Class);
        Assert.Equal(PressMode.Edge, v.Press);
    }

    [Fact]
    public void The_takeover_buttons_are_edge_push_buttons()
    {
        Assert.Equal(new TakeoverRole("S_FC_CAPT_INST_DISCONNECT", "S_FC_FO_INST_DISCONNECT"), Real.Roles!.Takeover);
    }
}
