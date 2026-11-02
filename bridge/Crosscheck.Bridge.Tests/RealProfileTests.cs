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
    [InlineData("S_OH_HYD_BLUE_ELEC_PUMP")]
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
    [InlineData("S_FCU_SPEED")] // push/pull knob: one direction is invisible, stays unsynced
    [InlineData("S_PED_RMP1_NAV")] // no evidence yet: a button family is not always a counter
    [InlineData("S_FC_THR_INST_DISCONNECT1")]
    [InlineData("S_OH_FUEL_XFEED")] // a 0/1 latch (positions run)
    [InlineData("S_FCU_ALTITUDE_SCALE")] // switch
    [InlineData("S_FCU_EFIS1_NAV1")] // ADF/VOR switch
    [InlineData("S_MIP_CLOCK_ET")] // switch
    [InlineData("S_ASP_INTRAD")] // switch
    [InlineData("S_ASP_SPKR_REC")] // unclear
    [InlineData("S_OH_INT_LT_EMER")] // EMER EXIT LT: a 3-position switch (positions 0, 1, 2)
    [InlineData("S_OH_FIRE_ENG1_BUTTON")] // never pressed in tests, stays unsynced
    public void Knobs_switches_and_unclear_controls_stay_off_the_edge_rule(string name) =>
        Assert.DoesNotContain(Real.Vars, v => v.Name == name && v.Class == SyncClass.Pulse);

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
        Assert.DoesNotContain(Real.Vars, v => v.Name is "S_FCU_SPEED" or "S_FCU_HEADING" or "S_FCU_ALTITUDE" or "S_FCU_VERTICAL_SPEED");
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
    [InlineData("A_OH_PNEUMATIC_LDG_ELEV", -3, 7)]
    [InlineData("A_FC_SPEEDBRAKE", 1, 3)]
    [InlineData("A_WR_TILT", 3, 10)]
    [InlineData("A_WR_GAIN", -5, 0)]
    public void Analog_ranges_come_from_gate_test_1(string name, double min, double max)
    {
        Assert.Equal([min, max], Var(name).Range);
        var guard = new SimSafetyGuard(Real);
        guard.CheckWrite(name, min);
        guard.CheckWrite(name, max);
        Assert.Throws<SafetyViolationException>(() => guard.CheckWrite(name, max + 0.25));
        Assert.Throws<SafetyViolationException>(() => guard.CheckWrite(name, min - 0.25));
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
