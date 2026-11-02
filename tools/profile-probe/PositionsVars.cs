// The `positions` command (CRO-24 prep for gate test 2): the controls the pilot moves while the
// tool records every distinct value. Read only: these names are only ever read.
// Ordered by flight importance, so stopping early (q) loses only the least important steps.

namespace ProfileProbe;

enum PositionKind
{
    /// <summary>A selector, switch or lever: every position and back to where it started.</summary>
    Move,
    /// <summary>An FCU knob: push once, then pull once.</summary>
    PushPull,
    /// <summary>A control we think is a push button but are not sure of: one click.</summary>
    Click,
}

/// <param name="Id">Short name for --only and the CSV.</param>
/// <param name="Vars">The control's var first; the others are its neighbours, classified too when they move
/// (so a step still tells the truth if the label points at the wrong var).</param>
/// <param name="Effects">Lamps or displays that should follow; recorded, not classified.</param>
/// <param name="Say">Replaces the standard instruction.</param>
sealed record PositionControl(
    string Id, string Label, PositionKind Kind, string[] Vars, string[]? Effects = null, string? Hint = null, string? Say = null)
{
    public string Var => Vars[0];
    public IEnumerable<string> Watched => Vars.Concat(Effects ?? []);
    /// <summary>Rough time for one step, reading the prompt included.</summary>
    public int Seconds => Kind switch { PositionKind.Move => 20, PositionKind.PushPull => 15, _ => 10 };

    /// <summary>The observe recorder's view of this control (it only needs an id and the var names).</summary>
    public ObserveControl AsObserve() => new(Id, Label, Var, Watched.Skip(1).ToArray());
}

static class PositionsVars
{
    static readonly string[] Xpdr = ["S_XPDR_MODE", "S_XPDR_OPERATION", "S_XPDR_ATC", "S_XPDR_ALTREPORTING", "S_TCAS_RANGE", "S_TCAS_HORIZONTAL_RANGE"];
    static readonly string[] Wx = ["S_WR_SYS", "S_WR_MODE", "S_WR_GAIN", "S_WR_MULTISCAN", "S_WR_PRED_WS", "S_WR_GCS"];
    static readonly string[] Switching = ["S_DISPLAY_ATT_HDG", "S_DISPLAY_AIR_DATA", "S_DISPLAY_EIS_DMC", "S_DISPLAY_ECAM_ND_XFR"];
    static readonly string[] Landing = ["S_OH_EXT_LT_LANDING_L", "S_OH_EXT_LT_LANDING_R", "S_OH_EXT_LT_LANDING_BOTH"];

    /// <summary>The main var first, then the rest of its family.</summary>
    static string[] Family(string main, string[] family) => family.Where(v => v != main).Prepend(main).ToArray();

    const string ExtLt = "Overhead EXT LT panel.";
    const string Ir = "OFF stops this IRS: it has to align again afterwards, and the ADIRS ON BAT light or the ground horn may come on for a moment. That is fine at the gate.";

    public static readonly PositionControl[] Controls =
    [
        // --- flight controls and FCU
        new("FLAPS", "FLAPS lever", PositionKind.Move, ["S_FC_FLAPS", "S_FC_FLAPS_LATCH"],
            Hint: "Through 1, 2, 3, FULL and back to 0. The flaps may move if there is hydraulic pressure; that is fine."),
        new("FCU_SPD", "FCU SPD knob", PositionKind.PushPull, ["S_FCU_SPEED"], ["I_FCU_SPEED_MANAGED", "B_FCU_SPEED_DASHED", "N_FCU_SPEED"],
            "Managed/selected speed may change; that is fine at the gate. One direction is invisible to frame sampling: its managed lamp, dashes and display are recorded to find it (detector, after gate test 2)."),
        new("FCU_HDG", "FCU HDG knob", PositionKind.PushPull, ["S_FCU_HEADING"], ["I_FCU_HEADING_MANAGED", "B_FCU_HEADING_DASHED", "N_FCU_HEADING"]),
        new("FCU_ALT", "FCU ALT knob", PositionKind.PushPull, ["S_FCU_ALTITUDE"], ["I_FCU_ALTITUDE_MANAGED", "N_FCU_ALTITUDE"]),
        new("FCU_VS", "FCU V/S knob", PositionKind.PushPull, ["S_FCU_VERTICAL_SPEED"], ["B_FCU_VERTICALSPEED_DASHED", "N_FCU_VS"]),
        new("FCU_ALT_SCALE", "FCU ALT 100/1000 switch", PositionKind.Move, ["S_FCU_ALTITUDE_SCALE"]),
        new("ENG_MODE", "ENG MODE selector (pedestal)", PositionKind.Move, ["S_ENG_MODE"],
            Hint: "CRANK, NORM, IGN/START and back to NORM. The ENG MASTER switches stay OFF."),
        new("AUTOBRK_LO", "AUTO BRK LO button", PositionKind.Click, ["S_MIP_AUTOBRAKE_LO", "S_MIP_AUTOBRAKE_MED", "S_MIP_AUTOBRAKE_MAX"],
            ["I_MIP_AUTOBRAKE_LO_L", "I_MIP_AUTOBRAKE_LO_U"]),
        new("ANTI_SKID", "A/SKID & N/W STRG switch", PositionKind.Move, ["S_FC_MIP_ANTI_SKID"]),
        new("RUD_TRIM", "RUD TRIM knob (pedestal)", PositionKind.Move, ["S_FC_RUDDER_TRIM"], ["N_FC_RUDDER_TRIM_DECIMAL"],
            Say: "Turn the RUD TRIM knob left for a moment, then right for a moment, so the trim ends near 0.",
            Hint: "It springs back to the middle. If the trim does not end at 0, press RESET afterwards."),
        new("XPDR_MODE", "transponder mode selector (STBY / AUTO / ON)", PositionKind.Move, Family("S_XPDR_MODE", Xpdr)),
        new("TCAS_MODE", "TCAS mode selector (STBY / TA / TA/RA)", PositionKind.Move, Family("S_XPDR_OPERATION", Xpdr)),

        // --- exterior lights and signs
        new("STROBE", "STROBE switch", PositionKind.Move, ["S_OH_EXT_LT_STROBE"], Hint: ExtLt),
        new("BEACON", "BEACON switch", PositionKind.Move, ["S_OH_EXT_LT_BEACON"]),
        new("WING", "WING lights switch", PositionKind.Move, ["S_OH_EXT_LT_WING"]),
        new("NAV_LOGO", "NAV & LOGO switch", PositionKind.Move, ["S_OH_EXT_LT_NAV_LOGO"]),
        new("RWY_TURNOFF", "RWY TURN OFF switch", PositionKind.Move, ["S_OH_EXT_LT_RWY_TURNOFF"]),
        new("LAND_L", "left LAND switch", PositionKind.Move, Family("S_OH_EXT_LT_LANDING_L", Landing)),
        new("LAND_R", "right LAND switch", PositionKind.Move, Family("S_OH_EXT_LT_LANDING_R", Landing)),
        new("NOSE", "NOSE light switch (T.O / TAXI / OFF)", PositionKind.Move, ["S_OH_EXT_LT_NOSE"]),
        new("SEAT_BELTS", "SEAT BELTS switch", PositionKind.Move, ["S_OH_SIGNS"]),
        new("NO_SMOKING", "NO SMOKING switch", PositionKind.Move, ["S_OH_SIGNS_SMOKING"]),
        new("EMER_EXIT_LT", "EMER EXIT LT switch", PositionKind.Move, ["S_OH_INT_LT_EMER"], ["I_OH_INT_LT_EMER_OFF"],
            "ON, ARM, OFF and back to where it was."),

        // --- navigation
        new("IR1", "IR 1 mode selector (ADIRS)", PositionKind.Move, ["S_OH_NAV_IR1_MODE"], ["I_OH_NAV_IR1_ALIGN"], Ir),
        new("IR2", "IR 2 mode selector (ADIRS)", PositionKind.Move, ["S_OH_NAV_IR2_MODE"], ["I_OH_NAV_IR2_ALIGN"]),
        new("IR3", "IR 3 mode selector (ADIRS)", PositionKind.Move, ["S_OH_NAV_IR3_MODE"], ["I_OH_NAV_IR3_ALIGN"]),
        new("EFIS1_NAV1", "captain EFIS ADF/VOR 1 switch", PositionKind.Move, ["S_FCU_EFIS1_NAV1", "S_FCU_EFIS1_NAV2"]),
        new("EFIS1_NAV2", "captain EFIS ADF/VOR 2 switch", PositionKind.Move, ["S_FCU_EFIS1_NAV2", "S_FCU_EFIS1_NAV1"]),
        new("EFIS2_NAV1", "first officer EFIS ADF/VOR 1 switch", PositionKind.Move, ["S_FCU_EFIS2_NAV1", "S_FCU_EFIS2_NAV2"]),
        new("EFIS2_NAV2", "first officer EFIS ADF/VOR 2 switch", PositionKind.Move, ["S_FCU_EFIS2_NAV2", "S_FCU_EFIS2_NAV1"]),

        // --- weather radar, ATC, TCAS
        new("WX_SYS", "weather radar SYS switch", PositionKind.Move, Family("S_WR_SYS", Wx)),
        new("WX_MODE", "weather radar mode selector", PositionKind.Move, Family("S_WR_MODE", Wx)),
        new("WX_GAIN", "weather radar GAIN knob", PositionKind.Move, Family("S_WR_GAIN", Wx)),
        new("WX_MULTISCAN", "weather radar MULTISCAN switch", PositionKind.Move, Family("S_WR_MULTISCAN", Wx)),
        new("WX_PWS", "weather radar PWS switch", PositionKind.Move, Family("S_WR_PRED_WS", Wx)),
        new("ATC_SEL", "ATC 1/2 selector", PositionKind.Move, Family("S_XPDR_ATC", Xpdr), ["I_XPDR_ATC_1", "I_XPDR_ATC_2"]),
        new("TCAS_TRAFFIC", "TCAS traffic selector (THRT / ALL / ABV / BLW)", PositionKind.Move, Family("S_TCAS_RANGE", Xpdr)),
        new("TCAS_OTHER", "other TCAS selector, if the panel has a second one (s = skip if not)", PositionKind.Move, Family("S_TCAS_HORIZONTAL_RANGE", Xpdr)),

        // --- ECAM and audio switching
        new("ATT_HDG", "ATT HDG selector (pedestal SWITCHING panel)", PositionKind.Move, Family("S_DISPLAY_ATT_HDG", Switching)),
        new("AIR_DATA", "AIR DATA selector (SWITCHING panel)", PositionKind.Move, Family("S_DISPLAY_AIR_DATA", Switching)),
        new("EIS_DMC", "EIS DMC selector (SWITCHING panel)", PositionKind.Move, Family("S_DISPLAY_EIS_DMC", Switching)),
        new("ECAM_ND_XFR", "ECAM/ND XFR selector (SWITCHING panel)", PositionKind.Move, Family("S_DISPLAY_ECAM_ND_XFR", Switching)),
        new("AUDIO_SW", "AUDIO SWITCHING selector (overhead)", PositionKind.Move, ["S_AUDIO_SWITCHING"]),

        // --- push buttons we are not sure of (one click each; the rest of each family follows the result)
        new("APU_START", "APU START button", PositionKind.Click, ["S_OH_ELEC_APU_START"], ["I_OH_ELEC_APU_START_L", "I_OH_ELEC_APU_START_U"],
            "APU MASTER stays OFF, so nothing starts."),
        new("GND_CTL", "RCRD GND CTL button", PositionKind.Click, ["S_OH_RCRD_GND_CTL"], ["I_OH_RCRD_GND_CTL_L"]),
        new("RMP1_VHF", "RMP 1 VHF2 key, then VHF1", PositionKind.Click, ["S_PED_RMP1_VHF2", "S_PED_RMP1_VHF1"],
            ["I_PED_RMP1_VHF2", "I_PED_RMP1_VHF1", "I_PED_RMP1_SEL"], Say: "Click VHF2 on RMP 1 once, then VHF1 once (back to VHF1)."),
        new("TERR_ND", "captain TERR ON ND button", PositionKind.Click, ["S_MIP_GPWS_TERRAIN_ON_ND_CAPT"], ["I_MIP_GPWS_TERRAIN_ON_ND_CAPT_L"]),
        new("GS_ALERT", "captain GPWS G/S button (next to the PFD)", PositionKind.Click, ["S_MIP_GPWS_VISUAL_ALERT_CAPT"],
            ["I_MIP_GPWS_VISUAL_ALERT_CAPT_L", "I_MIP_GPWS_VISUAL_ALERT_CAPT_U"]),
        new("AUTOLAND", "captain AUTOLAND button", PositionKind.Click,
            ["S_MIP_AUTOLAND_CAPT", "S_MIP_AUTOLAND_FO", "S_MIP_BKUP_SPD_ALT_CAPT", "S_MIP_BKUP_SPD_ALT_FO"],
            ["I_MIP_AUTOLAND_CAPT", "I_MIP_AUTOLAND_CAPT_L", "I_MIP_AUTOLAND_CAPT_U"]),
        new("PFDND_XFER", "captain PFD/ND XFER button", PositionKind.Click, ["S_DISPLAY_PFDND_XFER_CAPT", "S_DISPLAY_PFDND_XFER_FO"],
            Hint: "The PFD and ND swap. If you want them back, click it once more before Enter; two clicks are fine."),
        new("ATC_MSG", "captain ATC MSG button", PositionKind.Click, ["S_MIP_ATC_MSG_CAPT"], ["I_MIP_ATC_MSG_CAPT_L", "I_MIP_ATC_MSG_CAPT_U"]),
        new("ISFD_LS", "LS button on the standby instrument (ISFD)", PositionKind.Click, ["S_MIP_ISFD_LS"]),

        // --- overhead, less used in flight
        new("XBLEED", "X BLEED selector (SHUT / AUTO / OPEN)", PositionKind.Move, ["S_OH_PNEUMATIC_XBLEED_SELECTOR"]),
        new("MAN_VS", "MAN V/S CTL switch (UP, then DN; it springs back)", PositionKind.Move, ["S_OH_PNEUMATIC_PRESS_MAN"]),
        new("PRESS_MODE", "cabin pressure MODE SEL button", PositionKind.Click, ["S_OH_PNEUMATIC_PRESS_MODE"],
            ["I_OH_PNEUMATIC_PRESS_MODE_L", "I_OH_PNEUMATIC_PRESS_MODE_U"]),
        new("HOT_AIR", "HOT AIR button (AIR COND panel)", PositionKind.Click, ["S_OH_PNEUMATIC_HOT_AIR"], ["I_OH_PNEUMATIC_HOT_AIR_L", "I_OH_PNEUMATIC_HOT_AIR_U"]),
        new("FUEL_XFEED", "fuel X FEED button", PositionKind.Click, ["S_OH_FUEL_XFEED"], ["I_OH_FUEL_XFEED_L", "I_OH_FUEL_XFEED_U"]),
        new("FUEL_MODE", "fuel MODE SEL button", PositionKind.Click, ["S_OH_FUEL_MODE_SEL"], ["I_OH_FUEL_MODE_SEL_L", "I_OH_FUEL_MODE_SEL_U"]),
        new("GPWS_FLAP", "GPWS FLAP MODE button", PositionKind.Click, ["S_OH_GPWS_FLAP_MODE"], ["I_OH_GPWS_FLAP_MODE_L"]),
        new("GPWS_SYS", "GPWS SYS button", PositionKind.Click, ["S_OH_GPWS_SYS"], ["I_OH_GPWS_SYS_L", "I_OH_GPWS_SYS_U"]),
        new("GPWS_GS", "GPWS G/S MODE button (overhead)", PositionKind.Click, ["S_OH_GPWS_GS_MODE"], ["I_OH_GPWS_GS_MODE_L"]),
        new("ANN_LT", "ANN LT switch (TEST / BRT / DIM)", PositionKind.Move, ["S_OH_IN_LT_ANN_LT"], Hint: "TEST lights every lamp; that is fine."),
        new("WIPER_CAPT", "captain WIPER selector", PositionKind.Move, ["S_MISC_WIPER_CAPT"]),
        new("WIPER_FO", "first officer WIPER selector", PositionKind.Move, ["S_MISC_WIPER_FO"]),
        new("CLOCK_ET", "captain clock ET selector (RUN / STP / RST)", PositionKind.Move, ["S_MIP_CLOCK_ET"], ["N_MIP_CLOCK_ELAPSED"]),
        new("CLOCK_UTC", "captain clock UTC selector (GPS / INT / SET)", PositionKind.Move, ["S_MIP_CLOCK_UTC", "S_MIP_CLOCK_SET"]),
    ];

    /// <summary>Every L: var the reader reads: controls, effects, then the setup check.</summary>
    public static IReadOnlyList<string> All { get; } =
        Controls.SelectMany(c => c.Watched).Concat(SafetyVars.Lvars).Distinct(StringComparer.Ordinal).ToList();

    public static string Instruction(PositionControl c) => c.Say ?? c.Kind switch
    {
        PositionKind.Move => $"Move the {c.Label} through every position and back to where it started.",
        PositionKind.PushPull => $"Push the {c.Label}, then pull it.",
        PositionKind.Click => $"Click the {c.Label} once.",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static int TotalSeconds(IEnumerable<PositionControl> controls) => controls.Sum(c => c.Seconds);
}
