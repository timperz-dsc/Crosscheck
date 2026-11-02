// CRO-29 part 1: the push buttons the `observe` command watches while the pilot clicks them.
// Read only: these names are only ever read.

namespace ProfileProbe;

enum StepKind { Once, Double, Hold }

/// <param name="Id">Short name for --only and the CSV.</param>
/// <param name="Button">The S_ var the pilot clicks.</param>
/// <param name="Effects">Lamps or values that should follow the button.</param>
/// <param name="Hint">Printed once before the first step of this control.</param>
sealed record ObserveControl(string Id, string Label, string Button, string[] Effects, string? Hint = null, bool McduScreen = false);

static class ObserveVars
{
    const string McduHint = "Put MCDU1 on the DATA INDEX page first. Letters go into the scratchpad; the CLR hold clears it again.";

    public static readonly ObserveControl[] Controls =
    [
        new("MCDU1_S", "MCDU1 key S", "S_CDU1_KEY_S", [], McduHint, McduScreen: true),
        new("MCDU1_E", "MCDU1 key E", "S_CDU1_KEY_E", [], McduScreen: true),
        new("MCDU1_1", "MCDU1 key 1", "S_CDU1_KEY_1", [], McduScreen: true),
        new("MCDU1_LSK1L", "MCDU1 line select key 1L (left, top)", "S_CDU1_KEY_LSK1L", [],
            "On DATA INDEX, 1L opens POSITION MONITOR. Go back to DATA INDEX (DATA key) before each step if you like; that is fine.", McduScreen: true),
        new("MCDU1_CLR", "MCDU1 CLR", "S_CDU1_KEY_CLEAR", [], McduScreen: true),

        new("ECAM_ENG", "ECAM control panel ENG page key", "S_ECAM_ENGINE", ["I_ECAM_ENGINE"]),
        new("ECAM_ALL", "ECAM control panel ALL", "S_ECAM_ALL", [], "ALL cycles through the pages while held. That is expected."),
        new("ECAM_EMER_CANC", "ECAM EMER CANC", "S_ECAM_EMER_CANCEL", ["I_ECAM_EMER_CANCEL"],
            "Only do this with no caution showing on the upper ECAM (normal at the gate)."),
        new("ECAM_CLR", "ECAM CLR (left)", "S_ECAM_CLR_LEFT", ["I_ECAM_CLR_LEFT"]),

        new("CLOCK_CHR", "Captain clock CHR", "S_MIP_CLOCK_CHR", ["N_MIP_CLOCK_CHRONO"]),
        new("CLOCK_RST", "Captain clock RST", "S_MIP_CLOCK_RST", ["N_MIP_CLOCK_CHRONO"]),
        new("XPDR_CLR", "Transponder CLR", "S_PED_ATC_CLR", []),

        new("FD1", "FD on the captain EFIS (FCU left)", "S_FCU_EFIS1_FD", ["I_FCU_EFIS1_FD", "I_FCU_EFIS2_FD"]),
        new("AP1", "AP1 on the FCU", "S_FCU_AP1", ["I_FCU_AP1"], "AP1 should not engage at the gate; just click it as asked."),
        new("CSTR1", "CSTR on the captain EFIS", "S_FCU_EFIS1_CSTR", ["I_FCU_EFIS1_CSTR"]),
        new("VOICE1", "VOICE on ASP 1 (captain audio panel)", "S_ASP_VOICE", ["I_ASP_VOICE"]),
        new("YELLOW_PUMP", "YELLOW ELEC PUMP (overhead HYD panel)", "S_OH_HYD_YELLOW_ELEC_PUMP",
            ["I_OH_HYD_YELLOW_ELEC_PUMP_L", "I_OH_HYD_YELLOW_ELEC_PUMP_U"],
            "Known counter, for comparison. The four presses in total leave it as it was."),

        new("ENG1_FIRE_TEST", "ENG 1 FIRE TEST (the small TEST button, NOT the red ENG 1 FIRE pushbutton)", "S_OH_FIRE_ENG1_TEST",
            ["I_OH_FIRE_ENG1_BUTTON", "I_MIP_MASTER_WARNING_CAPT"],
            "Only the TEST button. Never the red fire pushbutton or the AGENT buttons. Silence MASTER WARN after each step if it stays on."),
        new("SVCE_INT_OVRD", "SVCE INT OVRD (overhead CALLS area)", "S_OH_SVCE_INT_OVRD", ["I_OH_SVCE_INT_OVRD"]),
        new("CAPT_TAKEOVER", "Captain sidestick takeover button", "S_FC_CAPT_INST_DISCONNECT",
            ["I_FC_SIDESTICK_PRIORITY_CAPT", "I_FC_SIDESTICK_PRIORITY_CAPT_ARROW"],
            "Use the button on the captain sidestick (or its key binding). Keep holds short, about 2 s."),
        new("FO_TAKEOVER", "First officer sidestick takeover button", "S_FC_FO_INST_DISCONNECT",
            ["I_FC_SIDESTICK_PRIORITY_FO", "I_FC_SIDESTICK_PRIORITY_FO_ARROW"]),
    ];

    /// <summary>Also watched in every step, to see side effects (the fire test sets off MASTER WARN).</summary>
    public static readonly string[] Extra =
        ["S_MIP_MASTER_WARNING_CAPT", "I_MIP_MASTER_WARNING_CAPT", "S_MIP_MASTER_CAUTION_CAPT", "I_MIP_MASTER_CAUTION_CAPT"];

    /// <summary>Every L: var the observe reader reads, in a fixed order: controls, extras, then the setup check.</summary>
    public static IReadOnlyList<string> All { get; } =
        Controls.SelectMany(c => c.Effects.Prepend(c.Button)).Concat(Extra).Concat(SafetyVars.Lvars).Distinct(StringComparer.Ordinal).ToList();

    public static string Instruction(ObserveControl c, StepKind k) => k switch
    {
        StepKind.Once => $"Click {c.Label} ONCE.",
        StepKind.Double => $"Click {c.Label} TWICE, as fast as you can.",
        StepKind.Hold => $"HOLD {c.Label} for about 2 s, then release.",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };
}
