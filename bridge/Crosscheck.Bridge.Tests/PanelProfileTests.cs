using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// CRO-34: what the panel takes from the shipped Fenix profile: one control per button and lamp, knob and
/// display; tolerances and power gates outside the exact hash; the owner's decisions on comfort items and
/// windows; and the bridge's side of the loose comparison.
/// </summary>
public class PanelProfileTests
{
    private static readonly Profile Real = PanelReplay.Real;
    private static readonly ControlMap Controls = new(Real, new Labels(Real));
    private static VarEntry Var(string name) => Real.Vars.Single(v => v.Name == name);

    [Theory]
    [InlineData("I_FCU_EFIS1_FD", "S_FCU_EFIS1_FD")]           // a button's lamp
    [InlineData("N_FCU_ALTITUDE", "E_FCU_ALTITUDE")]           // a knob's display
    [InlineData("I_FCU_HEADING_MANAGED", "E_FCU_HEADING")]     // a mode lamp under its knob
    [InlineData("B_FCU_VERTICALSPEED_DASHED", "E_FCU_VS")]
    [InlineData("E_PED_RMP1_INNER", "rmp1_freq")]               // inner and outer knob: one control
    [InlineData("E_PED_RMP1_OUTER", "rmp1_freq")]
    [InlineData("N_PED_RMP1_STDBY", "rmp1_freq")]
    [InlineData("I_FCU_EFIS1_WPT", "efis1_nd_filter")]          // the ND filter buttons and lamps
    [InlineData("S_FCU_EFIS1_CSTR", "efis1_nd_filter")]
    [InlineData("I_ECAM_ENGINE", "ecam_page")]
    [InlineData("I_OH_ELEC_APU_MASTER_L", "S_OH_ELEC_APU_MASTER")]
    public void One_control_per_button_and_lamp_knob_and_display(string name, string control) =>
        Assert.Equal(control, Controls.ControlOf(name));

    [Fact]
    public void Control_names_are_plain_and_walkaround_items_are_outside()
    {
        Assert.Equal("RMP 1 frequency", Controls.Label("rmp1_freq"));
        Assert.Equal("LS (Capt)", Controls.Label("S_FCU_EFIS1_LS"));
        Assert.Equal("FCU ALT", Controls.Label("E_FCU_ALTITUDE"));
        Assert.Equal("CDU 1 screen", Controls.Label("aircraft.mcdu1.display"));
        Assert.True(Controls.Outside("S_EXT_CARGO_BAY_AFT_LT"));
        Assert.False(Controls.Outside("S_OH_ELEC_APU_MASTER"));
        Assert.Equal(2, Controls.Priority("E_FCU_ALTITUDE"));
        Assert.Equal(2, Controls.Priority("S_ENG_MASTER_1"));    // engines on the pedestal: flight-critical
        Assert.Equal(3, Controls.Priority("S_OH_ELEC_APU_MASTER"));
        Assert.Equal(4, Controls.Priority("rmp1_freq"));
        Assert.Equal(5, Controls.Priority("A_FCU_LIGHTING"));    // lights: 5 even on the FCU
        // Every control the panel can show has a name a pilot can read.
        foreach (var v in Real.Vars.Where(v => v.Class is not (SyncClass.Ignore or SyncClass.Stream)))
            Assert.DoesNotMatch(@"^[A-Z]{1,3}_[A-Z0-9]+_", Controls.Label(Controls.ControlOf(v.Name)));
    }

    [Fact]
    public void Tolerances_power_gates_and_the_gate_test_1_ignore_list()
    {
        Assert.Equal(1.2, Var("N_ELEC_VOLT_BAT_1").Tolerance); // 0.6.1: flight 1 showed 1.1 V of noise
        Assert.Equal(1.2, Var("N_ELEC_VOLT_BAT_2").Tolerance); // 0.6.1: flight 1 showed 1.1 V of noise
        Assert.Equal(0.3, Var("N_HYD_PRESSURE_BRAKE_LEFT").Tolerance);
        Assert.Equal(0.3, Var("N_HYD_PRESSURE_BRAKE_RIGHT").Tolerance);
        Assert.Equal("B_FCU_POWER", Var("I_FCU_EFIS1_LS").Powered);
        Assert.Equal("B_FCU_POWER", Var("B_FCU_HEADING_DASHED").Powered);
        Assert.Equal("B_PED_RMP1_POWER", Var("B_PED_RMP1_DATA_DISPLAYED_ACT").Powered);
        Assert.Equal(["S_FCU_EFIS1_FD", "S_FCU_EFIS2_FD"], Var("I_FCU_HEADING_MANAGED").Upstream!);
        // Owner decision (gate test 3 section 8, CRO-40): the rudder trim position is an amber indicator with a
        // tolerance of one display step; pitch trim stays ignore until the stream (CRO-13).
        var rudderTrim = Var("N_FC_RUDDER_TRIM_DECIMAL");
        Assert.Equal(SyncClass.Observe, rudderTrim.Class);
        Assert.Equal(1, rudderTrim.Tolerance);
        Assert.Equal("Rudder trim differs – press RESET or adjust it.", rudderTrim.Notice);
        Assert.Equal(SyncClass.Ignore, Var("A_FC_ELEVATOR_TRIM").Class);
        Assert.Equal(SyncClass.Ignore, Var("N_FC_ELEVATOR_TRIM").Class);
        Assert.Equal([0, 1], Var("S_EXT_CARGO_DOOR_SELECTOR_AFT").Values!);
        // The stream is CRO-13 (CRO-17 is the follower spike).
        Assert.DoesNotContain(Real.Vars, v => v.Note?.Contains("CRO-17", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Owner_decisions_comfort_items_sync_and_windows_are_each_pilots_own()
    {
        // CRO-36 (owner decision 2026-11-03, after the positions run): seats, armrests, jumpseat, tray tables,
        // mask stowage doors and the gravity gear sync with the positions seen. The sunshades did not move in
        // that run: they stay on the review list (amber "not synced yet" when moved).
        var unsynced = UnsyncedList.LoadNextTo(Path.Combine(Repo.Root, "profiles", "fenix-a320", "profile.yaml"), Real).Labels;
        Assert.Equal([0, 1, 2], Var("S_SEAT_HEIGHT_CAPT").Values!);
        Assert.Equal(1, Var("S_SEAT_DISTANCE_FO").Rest);
        foreach (var name in new[] { "S_ARMREST_LEFT_FO", "S_JUMPSEAT", "S_TRAY_TABLE_CAPT", "S_OXYGEN_MASK_COVER_CAPT", "S_HYD_GRAVITY_GEAR_HANDLE" })
            Assert.Equal([0, 1], Var(name).Values!);
        Assert.Equal([0, 3], Var("S_HYD_GRAVITY_GEAR_EXTEND").Range!);
        Assert.Equal(2, Controls.Priority("S_HYD_GRAVITY_GEAR_HANDLE"));
        foreach (var name in new[] { "S_SUNSHADE_CAPT", "S_SUNSHADE_FO" })
        {
            Assert.DoesNotContain(Real.Vars, v => v.Name == name);
            Assert.True(unsynced.ContainsKey(name), name);
        }
        Assert.Equal(SyncClass.Verified, Var("S_COCKPIT_DOOR").Class);
        // Windows: each pilot's own. Blinds keep syncing.
        foreach (var name in new[] { "S_WINDOW_CAPT_LEVER", "S_WINDOW_FO_LEVER", "S_WINDOW_FO_LATCH", "N_WINDOW_CAPT", "N_WINDOW_FO", "S_WINDOW_CAPT_LEVER_BUTTON" })
            Assert.Equal(SyncClass.Ignore, Var(name).Class);
        Assert.Equal(SyncClass.Verified, Var("S_WINDOW_BLINDS_CAPT").Class);
    }

    [Fact]
    public async Task Loose_indicators_leave_the_hash_and_are_sent_with_it()
    {
        await using var h = new EngineHarness(profile: Real);
        await h.StartAsync(pf: false);
        h.Sim.Logic("B_FCU_POWER", 1);
        h.Sim.Logic("N_ELEC_VOLT_BAT_1", 27.8);
        var before = h.Engine.GroupHash("systems");
        h.Sim.Logic("N_ELEC_VOLT_BAT_1", 27.3);
        Assert.Equal(before, h.Engine.GroupHash("systems")); // the hash does not flap with the volts
        Assert.DoesNotContain(h.Engine.GroupValues("systems"), v => v.Var == "N_ELEC_VOLT_BAT_1");
        var bat = h.Engine.LooseValues("systems").Single(l => l.Var == "N_ELEC_VOLT_BAT_1");
        Assert.Equal((27.3, 1.2, false), (bat.Value!.Value, bat.Tolerance!.Value, bat.Unpowered)); // 1.2 V since 0.6.1

        // A plain FCU flag of a powered unit: loose, with its value; unpowered: null and "unpowered".
        h.Sim.Logic("B_FCU_TRACK_FPA_MODE", 1);
        Assert.Equal(1, h.Engine.LooseValues("fcu").Single(l => l.Var == "B_FCU_TRACK_FPA_MODE").Value);
        h.Sim.Logic("B_FCU_POWER", 0);
        var off = h.Engine.LooseValues("fcu").Single(l => l.Var == "B_FCU_TRACK_FPA_MODE");
        Assert.True(off.Unpowered);
        Assert.Null(off.Value);
        // A toggle lamp of an edge button stays in the hash, but carries nothing while unpowered.
        Assert.Contains(h.Engine.GroupValues("efis"), v => v.Var == "I_FCU_EFIS1_LS" && v.Class == "toggle" && v.Value is null);
    }

    [Fact]
    public void A_desync_for_a_var_the_profile_does_not_sync_is_never_red_and_never_raw()
    {
        var map = new ControlMap(Real, new Labels(Real), new Dictionary<string, string> { ["S_FCU_EFIS1_BARO_STD"] = "BARO STD (Capt)" });
        var r = new PanelReplay();
        var at = PanelReplay.Start;
        var body = new JsonObject { ["var"] = "S_FCU_EFIS1_BARO_STD", ["reason"] = "refused", ["tier"] = "red", ["expected"] = -9, ["observed"] = 0 };
        var panel = PanelLines.Build(Real, map, r.Labels, r.Context,
            new PanelInput([(body, at)], new Dictionary<string, DateTimeOffset>(), [], [], at.AddSeconds(10)));
        Assert.Empty(panel["red"]!.AsArray());
        Assert.Equal("BARO STD (Capt)", panel["indicators"]!["items"]![0]!["label"]!.GetValue<string>());
    }
}
