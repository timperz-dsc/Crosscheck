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
    public void Latching_push_buttons_are_press_counters(string name)
    {
        var v = Var(name);
        Assert.Equal(SyncClass.Pulse, v.Class);
        Assert.Equal(PressMode.Counter, v.Press);
        Assert.Null(v.Values);
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
    [InlineData("S_WR_MODE", new double[] { 1, 2, 3 })]
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
        Assert.NotEqual(PressMode.Counter, v.Press);
        Assert.Equal(lamp, v.Verify?.Lvar);
        Assert.Equal(SyncClass.Verified, Var(name + "_LATCH").Class);
    }

    [Fact]
    public void Hold_buttons_are_left_for_the_hold_step()
    {
        // Counter mode would turn a held button into a fixed 150 ms press on the other side.
        Assert.Equal(SyncClass.Verified, Var("S_OH_SVCE_INT_OVRD").Class);
        Assert.NotEqual(PressMode.Counter, Var("S_OH_FIRE_ENG1_TEST").Press);
    }
}
