// The `throttle` command (flight-1 diagnosis item 7): the thrust lever vars it reads and the guided
// steps. Read only: these names are only ever read. Every L: var is in the Fenix 3.0 scan
// (profiles/fenix-a320/scan/lvars-2026-11-01.csv); the two sim vars are documented MSFS SDK sim vars,
// each in its own definition, so a rejected name is reported as missing and shifts nothing.

namespace ProfileProbe;

enum ThrottleStepKind
{
    /// <summary>Hands off: hardware noise and jitter.</summary>
    Noise,
    /// <summary>Both levers held in one detent.</summary>
    Hold,
    /// <summary>One lever moved slowly from MAX REV to TOGA.</summary>
    Sweep,
}

/// <param name="Detent">Hold steps: the detent id.</param>
/// <param name="Lever">Sweep steps: the lever that moves.</param>
sealed record ThrottleStep(string Id, ThrottleStepKind Kind, string Say, string? Detent = null, ThrottleLever? Lever = null);

/// <summary>One lever's vars. <see cref="Input"/> is the one the profile streams.</summary>
sealed record ThrottleLever(string Name, string Input, string Gate, string[] Others)
{
    public IEnumerable<string> Vars => Others.Prepend(Gate).Prepend(Input);
}

static class ThrottleVars
{
    public static readonly ThrottleLever Left = new("left", "A_FC_THROTTLE_LEFT_INPUT", "A_FC_THROTTLE_LEFT_GATE",
        ["WT_VIRTUAL_THROTTLE_LEVER_POS_1", "XMLVAR_Throttle1Position", "S_FC_THROTTLE_LEFT_MAX_REVERSE", SimVars.A("GENERAL ENG THROTTLE LEVER POSITION:1")]);

    public static readonly ThrottleLever Right = new("right", "A_FC_THROTTLE_RIGHT_INPUT", "A_FC_THROTTLE_RIGHT_GATE",
        ["WT_VIRTUAL_THROTTLE_LEVER_POS_2", "XMLVAR_Throttle2Position", "S_FC_THROTTLE_RIGHT_MAX_REVERSE", SimVars.A("GENERAL ENG THROTTLE LEVER POSITION:2")]);

    public static readonly ThrottleLever[] Levers = [Left, Right];

    /// <summary>The lever vars, left and right in pairs.</summary>
    public static readonly string[] LeverVars = Left.Vars.Zip(Right.Vars).SelectMany(p => new[] { p.First, p.Second }).ToArray();

    /// <summary>Everything the reader reads: the lever vars, then the parked setup check.</summary>
    public static IReadOnlyList<string> All => LeverVars.Concat(SafetyVars.Lvars).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The detents, from the back stop to the front stop.</summary>
    public static readonly string[] Detents = ["MAX_REV", "REV_IDLE", "IDLE", "CL", "FLX_MCT", "TOGA"];

    public static string Label(string detent) => detent switch
    {
        "MAX_REV" => "MAX REV",
        "REV_IDLE" => "REV IDLE",
        "FLX_MCT" => "FLX/MCT",
        _ => detent,
    };

    static string HoldSay(string detent) => detent switch
    {
        "MAX_REV" => "Move both thrust levers to MAX REV (full reverse) and hold them there.",
        "REV_IDLE" => "Move both thrust levers to REV IDLE (reverse idle, reversers unlocked, no reverse thrust) and hold them there.",
        _ => $"Move both thrust levers to {Label(detent)} and hold them there.",
    };

    static string SweepSay(ThrottleLever lever) =>
        $"Put the {lever.Name.ToUpperInvariant()} lever at MAX REV and the other lever at IDLE. Press Enter, then move the {lever.Name} lever " +
        "slowly and steadily all the way to TOGA (about 15 s, no stops), and press Enter again at TOGA. The other lever stays at IDLE.";

    public static readonly ThrottleStep[] Steps =
    [
        new("NOISE", ThrottleStepKind.Noise, "Put both thrust levers at IDLE and take your hands off the levers (and the hardware throttle) for 10 s."),
        .. Detents.Select(d => new ThrottleStep(d, ThrottleStepKind.Hold, HoldSay(d), Detent: d)),
        new("SWEEP_LEFT", ThrottleStepKind.Sweep, SweepSay(Left), Lever: Left),
        new("SWEEP_RIGHT", ThrottleStepKind.Sweep, SweepSay(Right), Lever: Right),
    ];
}
