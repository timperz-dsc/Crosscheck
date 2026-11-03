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
/// <param name="Before">Asked before the step, and waited for (Enter): a setup the pilot does first.</param>
/// <param name="Time">Rough seconds for the step, instead of the default for its kind.</param>
sealed record PositionControl(
    string Id, string Label, PositionKind Kind, string[] Vars, string[]? Effects = null, string? Hint = null, string? Say = null,
    string? Before = null, int? Time = null)
{
    public string Var => Vars[0];
    public IEnumerable<string> Watched => Vars.Concat(Effects ?? []);
    /// <summary>Rough time for one step, reading the prompt included.</summary>
    public int Seconds => Time ?? Kind switch { PositionKind.Move => 20, PositionKind.PushPull => 15, _ => 10 };

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

    /// <summary>The gate test 2 prep run (CRO-24, 2026-11-02): positions-results.csv.</summary>
    public static readonly PositionControl[] GateTest2 =
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

    static string A(string name) => SimVars.A(name);

    const string Fmgs =
        "Before the FCU steps, set up the FMGS so managed modes are available: INIT A (FROM/TO, FLT NBR, CRZ FL), " +
        "a departure in F-PLN, PERF TAKE OFF (V1, VR, V2, FLEX). The ALT window should show managed (dot) after a push. " +
        "Press Enter when done, or s + Enter at the step to skip the FCU knobs.";

    const string Cover = "Lift the guard first and close it again at the end.";

    /// <summary>
    /// Gate test 3 prep (the last sim session before gate test 3): the writes that did not stick in gate
    /// test 2 with candidate sim vars, the FCU push/pull with the FMGS set up, the buttons and selectors
    /// still on the review list, the brightness and volume knobs, and the cockpit comfort items (CRO-36).
    /// Most important first: stopping early (q) loses only the last steps.
    /// </summary>
    public static readonly PositionControl[] GateTest3 =
    [
        // --- writes that did not stick in gate test 2 (cause H): which var does Fenix really read?
        new("FLAPS", "FLAPS lever", PositionKind.Move,
            ["S_FC_FLAPS", "S_FC_FLAPS_LATCH", "FLAPS_INTERNAL_CONFIG", "A320_FC_L_FLAPS",
             A("FLAPS HANDLE INDEX"), A("FLAPS HANDLE PERCENT"), A("TRAILING EDGE FLAPS LEFT PERCENT")],
            Hint: "Through 1, 2, 3, FULL and back to 0, about a second in each position. The flaps may move; that is fine.", Time: 25),
        new("SPOILERS", "speed brake lever", PositionKind.Move,
            ["A_FC_SPEEDBRAKE", "B_FC_SPEEDBRAKE", "WT_SPEEDBRAKE_LEVER_POS",
             A("SPOILERS HANDLE POSITION"), A("SPOILERS ARMED"), A("SPOILERS LEFT POSITION")],
            Say: "Pull the speed brake lever up to ARM and push it back down; then pull it back to half, to full, and back to RET.", Time: 25),
        new("ANTI_SKID", "A/SKID & N/W STRG switch", PositionKind.Move,
            ["S_FC_MIP_ANTI_SKID", A("ANTISKID BRAKES ACTIVE"), A("NOSEWHEEL LOCK ON GROUND")]),
        new("RUD_TRIM", "RUD TRIM knob (pedestal)", PositionKind.Move,
            ["S_FC_RUDDER_TRIM", "B_FC_RUDDER_TRIM", "B_FC_RUDDER_TRIM_DASHED", "N_FC_RUDDER_TRIM_DECIMAL", A("RUDDER TRIM PCT")],
            Say: "Turn the RUD TRIM knob left for 2 s, then right for 2 s, so the trim moves and ends near 0. Do not press RESET yet."),
        new("RUD_TRIM_RESET", "RUD TRIM RESET button", PositionKind.Click,
            ["S_FC_RUDDER_TRIM_RESET", "S_FC_RUDDER_TRIM", "N_FC_RUDDER_TRIM_DECIMAL", A("RUDDER TRIM PCT")],
            Say: "Turn RUD TRIM left for 2 s, so the trim is off 0, then press RESET once.", Time: 20),
        new("DOOR_SWITCH", "cockpit door switch (pedestal: UNLOCK / NORM / LOCK)", PositionKind.Move,
            ["S_PED_COCKPIT_DOOR", "B_DOORS_COCKPIT_LOCKED", "S_COCKPIT_DOOR"], ["I_PED_COCKPIT_DOOR_L", "I_PED_COCKPIT_DOOR_U"],
            Say: "Hold the cockpit door switch at UNLOCK for 2 s, let go, then hold it at LOCK for 2 s and let go (it springs back to NORM)."),
        new("DOOR", "cockpit door", PositionKind.Move,
            ["S_COCKPIT_DOOR", "B_DOORS_COCKPIT_LOCKED",
             ..Enumerable.Range(0, 10).Select(i => A($"INTERACTIVE POINT OPEN:{i}"))],
            Say: "Open the cockpit door (its handle, or the door itself), wait 2 s, and close it again.", Time: 25),

        // --- FCU push and pull (CRO-32): can the hidden direction be seen with managed modes available?
        new("FCU_SPD", "FCU SPD knob", PositionKind.PushPull,
            ["S_FCU_SPEED", "E_FCU_SPEED", "I_FCU_SPEED_MANAGED", "B_FCU_SPEED_DASHED", "I_FCU_SPEED_MODE", "B_FCU_SPEED_MACH", "N_FCU_SPEED",
             A("AUTOPILOT SPEED SLOT INDEX")],
            Before: Fmgs, Say: "Pull the SPD knob, wait 2 s, push it, wait 2 s.", Time: 25),
        new("FCU_HDG", "FCU HDG knob", PositionKind.PushPull,
            ["S_FCU_HEADING", "E_FCU_HEADING", "I_FCU_HEADING_MANAGED", "B_FCU_HEADING_DASHED", "I_FCU_HEADING_VS_MODE", "N_FCU_HEADING",
             A("AUTOPILOT HEADING SLOT INDEX")],
            Say: "Pull the HDG knob, wait 2 s, push it, wait 2 s.", Time: 25),
        new("FCU_ALT", "FCU ALT knob", PositionKind.PushPull,
            ["S_FCU_ALTITUDE", "E_FCU_ALTITUDE", "I_FCU_ALTITUDE_MANAGED", "N_FCU_ALTITUDE", A("AUTOPILOT ALTITUDE SLOT INDEX")],
            Say: "Pull the ALT knob, wait 2 s, push it, wait 2 s.", Time: 25),
        new("FCU_VS", "FCU V/S knob", PositionKind.PushPull,
            ["S_FCU_VERTICAL_SPEED", "E_FCU_VS", "B_FCU_VERTICALSPEED_DASHED", "N_FCU_VS", A("AUTOPILOT VS SLOT INDEX")],
            Say: "Pull the V/S knob, wait 2 s, push it, wait 2 s.", Time: 25),
        new("BARO_STD_CAPT", "captain BARO knob", PositionKind.PushPull,
            ["S_FCU_EFIS1_BARO_STD", "B_FCU_EFIS1_BARO_STD", "S_FCU_EFIS1_BARO_MODE", "E_FCU_EFIS1_BARO", "N_FCU_EFIS1_BARO_HPA",
             A("KOHLSMAN SETTING STD:1")],
            Say: "Pull the captain BARO knob (STD), wait 2 s, push it (back to QNH)."),
        new("BARO_STD_FO", "first officer BARO knob", PositionKind.PushPull,
            ["S_FCU_EFIS2_BARO_STD", "B_FCU_EFIS2_BARO_STD", "S_FCU_EFIS2_BARO_MODE", "E_FCU_EFIS2_BARO", "N_FCU_EFIS2_BARO_HPA",
             A("KOHLSMAN SETTING STD:2")],
            Say: "Pull the first officer BARO knob (STD), wait 2 s, push it (back to QNH)."),

        // --- buttons and selectors still on the review list
        new("N1_MODE_1", "ENG 1 N1 MODE button (overhead)", PositionKind.Click,
            ["S_OH_ENG_N1_MODE_1", "S_OH_ENG_N1_MODE_1_Cover", "S_OH_ENG_N1_MODE_2"], ["I_OH_ENG_N1_MODE_1_L"],
            Hint: Cover, Say: "Click ENG 1 N1 MODE once, wait 2 s, click it once more (back to off).", Time: 20),
        new("N1_MODE_2", "ENG 2 N1 MODE button (overhead)", PositionKind.Click,
            ["S_OH_ENG_N1_MODE_2", "S_OH_ENG_N1_MODE_2_Cover", "S_OH_ENG_N1_MODE_1"], ["I_OH_ENG_N1_MODE_2_L"],
            Hint: Cover, Say: "Click ENG 2 N1 MODE once, wait 2 s, click it once more (back to off).", Time: 20),
        new("AUTOBRK_MED", "AUTO BRK MED button", PositionKind.Click,
            ["S_MIP_AUTOBRAKE_MED", "S_MIP_AUTOBRAKE_LO", "S_MIP_AUTOBRAKE_MAX", A("AUTO BRAKE SWITCH CB")],
            ["I_MIP_AUTOBRAKE_MED_L", "I_MIP_AUTOBRAKE_MED_U"], Say: "Click AUTO BRK MED once, wait 2 s, click it once more (off)."),
        new("AUTOBRK_MAX", "AUTO BRK MAX button", PositionKind.Click,
            ["S_MIP_AUTOBRAKE_MAX", "S_MIP_AUTOBRAKE_LO", "S_MIP_AUTOBRAKE_MED", A("AUTO BRAKE SWITCH CB")],
            ["I_MIP_AUTOBRAKE_MAX_L", "I_MIP_AUTOBRAKE_MAX_U"], Say: "Click AUTO BRK MAX once, wait 2 s, click it once more (off)."),
        new("RMP1_KEYS", "RMP 1 keys", PositionKind.Click,
            ["S_PED_RMP1_HF1", "S_PED_RMP1_HF2", "S_PED_RMP1_AM", "S_PED_RMP1_NAV", "S_PED_RMP1_NAV_Cover", "S_PED_RMP1_VOR",
             "S_PED_RMP1_ILS", "S_PED_RMP1_MLS", "S_PED_RMP1_ADF", "S_PED_RMP1_BFO", "S_PED_RMP1_VHF1", "B_PED_RMP1_COURSE_DISPLAYED"],
            ["I_PED_RMP1_HF1", "I_PED_RMP1_HF2", "I_PED_RMP1_AM", "I_PED_RMP1_NAV", "I_PED_RMP1_VOR", "I_PED_RMP1_ILS", "I_PED_RMP1_MLS",
             "I_PED_RMP1_ADF", "I_PED_RMP1_BFO", "I_PED_RMP1_VHF1", "I_PED_RMP1_SEL", "N_PED_RMP1_ACTIVE", "N_PED_RMP1_STDBY"],
            Say: "On RMP 1, click each once, about a second apart: HF1, HF2, AM; lift the NAV guard, NAV, VOR, LS, GLS, ADF, BFO; " +
                 "close the NAV guard; VHF1 last.",
            Hint: "RMP 2 and 3 follow as the same part.", Time: 50),
        new("CLOCK_SET", "clock SET selector (captain clock)", PositionKind.Move,
            ["S_MIP_CLOCK_SET", "S_MIP_CLOCK_UTC", "E_MIP_CLOCK_SET"],
            Hint: "Not the UTC selector: the SET selector or knob next to it. Back to where it started."),
        new("DOOR_VIDEO", "cockpit door video button", PositionKind.Click,
            ["S_PED_COCKPIT_DOOR_VIDEO", "S_OH_COCKPIT_DOOR_VIDEO"], ["I_OH_COCKPIT_DOOR_VIDEO"],
            Say: "Click the cockpit door video button once, wait 2 s, click it once more.", Time: 15),

        // --- brightness and volume knobs
        new("ECAM_BRT", "ECAM brightness knobs", PositionKind.Move,
            ["A_DISPLAY_BRIGHTNESS_ECAM_U", "A_DISPLAY_BRIGHTNESS_ECAM_L", "N_DISPLAY_BRIGHTNESS_ECAM_U", "N_DISPLAY_BRIGHTNESS_ECAM_L"],
            Say: "Turn the upper ECAM brightness knob down a little and back; then the lower one."),
        new("CAPT_BRT", "captain PFD and ND brightness knobs", PositionKind.Move,
            ["A_DISPLAY_BRIGHTNESS_CO", "A_DISPLAY_BRIGHTNESS_CI", "A_DISPLAY_BRIGHTNESS_CI_OUTER", "N_DISPLAY_BRIGHTNESS_CO", "N_DISPLAY_BRIGHTNESS_CI"],
            Say: "Captain: turn the PFD brightness knob down a little and back; then the ND knob, inner and outer ring."),
        new("FO_BRT", "first officer PFD and ND brightness knobs", PositionKind.Move,
            ["A_DISPLAY_BRIGHTNESS_FO", "A_DISPLAY_BRIGHTNESS_FI", "A_DISPLAY_BRIGHTNESS_FI_OUTER", "N_DISPLAY_BRIGHTNESS_FO", "N_DISPLAY_BRIGHTNESS_FI"],
            Say: "First officer: turn the PFD brightness knob down a little and back; then the ND knob, inner and outer ring."),
        new("DCDU_BRT", "captain DCDU BRT/DIM", PositionKind.Click,
            ["S_DCDU1_BRT", "S_DCDU1_DIM", "S_DCDU1_DIM_BRT", "N_DCDU1_BRIGHTNESS"],
            Say: "On the captain DCDU, press BRT twice, then DIM twice.", Time: 15),
        new("ASP2_VOLUME", "ASP 2 VHF 1 volume knob (first officer audio panel)", PositionKind.Move,
            ["A_ASP2_VHF_1_VOLUME", "A_ASP2_VHF_2_VOLUME", "A_ASP2_VHF_3_VOLUME", "A_ASP2_INT_VOLUME", "A_ASP2_CAB_VOLUME"],
            Say: "On the first officer's audio panel (ASP 2), turn the VHF 1 volume knob down a little and back.",
            Hint: "Open item J: this shows whether ASP 2 moves at all."),

        // --- cockpit comfort items (CRO-36: sync later)
        new("TRAY_TABLES", "tray tables", PositionKind.Move, ["S_TRAY_TABLE_CAPT", "S_TRAY_TABLE_FO"],
            Say: "Open the captain tray table and stow it again; then the first officer's."),
        new("SEATS", "captain seat", PositionKind.Move,
            ["S_SEAT_DISTANCE_CAPT", "S_SEAT_HEIGHT_CAPT", "N_SEAT_DISTANCE_CAPT", "N_SEAT_HEIGHT_CAPT"],
            Say: "Move the captain seat forward and back, then up and down, and back to where it was."),
        new("ARMRESTS", "armrests", PositionKind.Move,
            ["S_ARMREST_LEFT_CAPT", "S_ARMREST_RIGHT_CAPT", "S_ARMREST_LEFT_FO", "S_ARMREST_RIGHT_FO"],
            Say: "Raise and lower one captain armrest, then one first officer armrest."),
        new("SUNSHADES", "sunshades", PositionKind.Move, ["S_SUNSHADE_CAPT", "S_SUNSHADE_FO"],
            Say: "Pull the captain sunshade out and stow it; then the first officer's."),
        new("JUMPSEAT", "jumpseat", PositionKind.Move, ["S_JUMPSEAT", "S_JUMPSEAT_HEADREST"],
            Say: "Unfold the jumpseat and fold it again (the headrest too, if it moves)."),
        new("MASK_HATCH", "oxygen mask stowage doors", PositionKind.Move, ["S_OXYGEN_MASK_COVER_CAPT", "S_OXYGEN_MASK_COVER_FO"],
            Say: "Open the captain oxygen mask stowage door and close it; then the first officer's.",
            Hint: "Only the door: never the mask TEST or the overhead MASK MAN ON."),
        new("GRAVITY_GEAR", "gravity gear extension handle (pedestal, rear)", PositionKind.Move,
            ["S_HYD_GRAVITY_GEAR_HANDLE", "S_HYD_GRAVITY_GEAR_EXTEND", A("GEAR HANDLE POSITION"), A("GEAR EMERGENCY HANDLE POSITION")],
            Say: "Unstow the gravity gear crank, turn it 3 turns clockwise, 3 turns back, and stow it.",
            Hint: "The gear is down and locked at the gate, so nothing moves outside.", Time: 30),
    ];

    /// <summary>The runs, by name (--run). The latest is the default.</summary>
    public static readonly IReadOnlyDictionary<string, PositionControl[]> Runs = new Dictionary<string, PositionControl[]>
    {
        ["gate2"] = GateTest2,
        ["gate3"] = GateTest3,
        // The first 12 steps again (writes that did not stick, door, FCU, BARO): their values were shifted in
        // the gate3 run (a rejected sim var name in a shared definition); confirms the realigned reading.
        ["gate3b"] = GateTest3.TakeWhile(c => c.Id != "BARO_STD_FO").ToArray(),
    };

    public const string DefaultRun = "gate3";

    /// <summary>The default run's steps.</summary>
    public static PositionControl[] Controls => Runs[DefaultRun];

    /// <summary>
    /// Every var the reader reads for these steps: controls, effects, the run's background vars (reported
    /// whenever they move), then the setup check.
    /// </summary>
    public static IReadOnlyList<string> AllOf(IEnumerable<PositionControl> controls) =>
        controls.SelectMany(c => c.Watched).Concat(Background(controls)).Concat(SafetyVars.Lvars).Distinct(StringComparer.Ordinal).ToList();

    public static IReadOnlyList<string> All => AllOf(Controls);

    /// <summary>A run with FCU push/pull steps watches the whole FCU family: any var that moves with a push or pull shows up.</summary>
    static IEnumerable<string> Background(IEnumerable<PositionControl> controls) =>
        controls.Any(c => c.Id.StartsWith("FCU_", StringComparison.Ordinal) && c.Kind == PositionKind.PushPull) ? FcuFamily : [];

    static readonly string[] FcuFamily =
    [
        "B_FCU_HEADING_DASHED", "B_FCU_POWER", "B_FCU_SPEED_DASHED", "B_FCU_SPEED_MACH", "B_FCU_TRACK_FPA_MODE", "B_FCU_VERTICALSPEED_DASHED",
        "I_FCU_ALTITUDE_MANAGED", "I_FCU_AP1", "I_FCU_AP2", "I_FCU_APPR", "I_FCU_ATHR", "I_FCU_EXPED", "I_FCU_HEADING_MANAGED",
        "I_FCU_HEADING_VS_MODE", "I_FCU_LOC", "I_FCU_MACH_MODE", "I_FCU_SPEED_MANAGED", "I_FCU_SPEED_MODE", "I_FCU_TRACK_FPA_MODE",
        "N_FCU_ALTITUDE", "N_FCU_HEADING", "N_FCU_SPEED", "N_FCU_VS", "E_FCU_ALTITUDE", "E_FCU_HEADING", "E_FCU_SPEED", "E_FCU_VS",
        "S_FCU_SPEED", "S_FCU_HEADING", "S_FCU_ALTITUDE", "S_FCU_VERTICAL_SPEED",
    ];

    public static string Instruction(PositionControl c) => c.Say ?? c.Kind switch
    {
        PositionKind.Move => $"Move the {c.Label} through every position and back to where it started.",
        PositionKind.PushPull => $"Push the {c.Label}, then pull it.",
        PositionKind.Click => $"Click the {c.Label} once.",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static int TotalSeconds(IEnumerable<PositionControl> controls) => controls.Sum(c => c.Seconds);
}
