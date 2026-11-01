// Which vars each probe phase may write, and what it may write to them.
// The deny list, the extra skips below and the non-sampled breakers are never writable.

using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;

namespace ProfileProbe;

enum Phase { Readonly, Verified, Buttons, Positions, Encoders, Breakers, Takeover, Hold }

sealed record Target(string Name, Phase Phase, IReadOnlyList<double>? Values, bool Delta = false, bool Counter = false);

sealed record Skipped(string Name, Phase Phase, string Why);

static class Targets
{
    /// <summary>
    /// Not on the deny list, but unsafe or would change the test setup. The probe never writes
    /// them; they stay unresolved and can be checked supervised later.
    /// </summary>
    public static readonly (string Pattern, string Why)[] ExtraSkips =
    [
        ("S_OH_ELEC_APU_START", "would start the APU"),
        ("S_OH_ELEC_EXT_PWR", "would cut ground power (test setup)"),
        // Found in the first run: S_EXT_GPU_PLUG pulled the ground power cable. Ground equipment
        // and service panels are skipped as a whole: they change the test setup or owned state.
        ("S_EXT_GPU_*", "ground power unit (pulled the GPU plug in the first run)"),
        ("S_EXT_*PLUG*", "ground equipment plug"),
        ("S_EXT_*CHOCK*", "chocks"),
        ("S_EXT_DOOR_*", "passenger door lock/handle: slide risk"),
        ("S_EXT_REFUEL*", "refuel panel: could change fuel (owned state)"),
        ("S_OH_ELT", "ELT switch (the deny pattern S_OH_ELT_* misses the switch itself)"),
        ("S_FC_RUDDER_TRIM", "moves the rudder trim while held; writing the switch back does not move the trim back"),
        // Found in the first run: some keys are a group (selecting one deselects another), so
        // one var cannot be put back on its own. Supervised only.
        ("S_ASP*_SEND", "audio transmit keys are a group: restoring needs a second key"),
        ("S_PED_RMP*_VHF*", "RMP radio selection keys are a group"),
        ("S_PED_RMP*_HF*", "RMP radio selection keys are a group"),
        ("S_PED_RMP*_AM", "RMP radio selection keys are a group"),
        ("S_PED_RMP*_NAV", "RMP radio selection keys are a group"),
        ("S_PED_RMP*_VOR", "RMP radio selection keys are a group"),
        ("S_PED_RMP*_ILS", "RMP radio selection keys are a group"),
        ("S_PED_RMP*_MLS", "RMP radio selection keys are a group"),
        ("S_PED_RMP*_ADF", "RMP radio selection keys are a group"),
        ("S_PED_RMP*_BFO", "RMP radio selection keys are a group"),
        ("S_COCKPIT_DOOR", "cockpit door switch: its lights did not come back after the switch was restored"),
        ("S_PED_COCKPIT_DOOR", "cockpit door switch: its lights did not come back after the switch was restored"),
        ("S_MIP_CLOCK_*","clock: CHR starts/stops the chrono, RST resets it; one key cannot put it back"),
        ("S_FCU_EFIS*_FD","FD key: a press switched both FDs off and a second press did not bring them back"),
        ("S_FCU_EFIS*_CSTR","EFIS ND option keys are a group"),
        ("S_FCU_EFIS*_WPT", "EFIS ND option keys are a group"),
        ("S_FCU_EFIS*_VORD", "EFIS ND option keys are a group"),
        ("S_FCU_EFIS*_NDB", "EFIS ND option keys are a group"),
        ("S_FCU_EFIS*_ARPT", "EFIS ND option keys are a group"),
        ("S_FCU_ALTITUDE", "FCU push/pull knob: managed vs selected is not undone by the same press"),
        ("S_FCU_HEADING", "FCU push/pull knob: managed vs selected is not undone by the same press"),
        ("S_FCU_SPEED", "FCU push/pull knob: managed vs selected is not undone by the same press"),
        ("S_FCU_VERTICAL_SPEED", "FCU push/pull knob: managed vs selected is not undone by the same press"),
        // Found in the first run: BUS TIE OFF disconnects EXT PWR from the AC buses, and Fenix
        // does not reconnect it when BUS TIE goes back to AUTO (EXT PWR must be pressed again).
        ("S_OH_ELEC_BUSTIE", "OFF cuts ground power; EXT PWR does not come back by itself"),
        ("S_OH_NAV_IR*_MODE", "IRS mode: would stop or restart alignment"),
        ("S_EXT_DOOR_*_HANDLE*", "passenger door handle: slide risk"),
        ("S_EXT_DOOR_*_INSIDE_HANDLE", "passenger door handle: slide risk"),
        ("S_FC_THROTTLE_*", "thrust lever"),
        ("S_OH_APU_AUTOEXTING_TEST", "APU fire auto-extinguish test"),
        ("S_OH_CARGO_SMOKE_TEST", "cargo smoke test"),
        ("*AGENT*", "fire agent"),
        ("*DISCH*", "discharge"),
        ("S_MIP_PARKING_BRAKE", "parking brake (safety check source)"),
        ("S_ENG_MASTER_*", "ENG MASTER (safety check source)"),
    ];

    /// <summary>Flaps and speedbrake levers: only with no hydraulic pressure (nothing can move).</summary>
    public static readonly string[] NeedsNoHydraulics = ["S_FC_FLAPS*", "S_FC_SPEEDBRAKE*", "S_FC_SPOILER*", "S_PED_SPEEDBRAKE*"];

    /// <summary>On the review list as "momentary or latching", but they are levers with several positions.</summary>
    public static readonly string[] LeversNotButtons = ["S_FC_FLAPS"];

    public static readonly string[] TakeoverVars = ["S_FC_CAPT_INST_DISCONNECT", "S_FC_FO_INST_DISCONNECT"];
    public static readonly string[] HoldVars = ["S_FCU_EFIS1_ND_MODE", "S_FCU_EFIS1_ND_ZOOM", "S_FCU_EFIS2_ND_MODE", "S_FCU_EFIS2_ND_ZOOM"];

    public const string ApuMaster = "S_OH_ELEC_APU_MASTER";
    public const string ConfirmNote = "confirm in step 2";

    public static readonly double[] Binary = [0, 1];
    /// <summary>The verified test writes 0, 1 and 2; 3 and 4 only for the press-counter restore.</summary>
    public static readonly double[] BinaryPlusTwo = [0, 1, 2, 3, 4];
    public static readonly double[] Steps = [0, 1, 2, 3, 4, 5, 6, 7];
    public static readonly double[] NdValues = [0, 1, 2, 3, 4, 5];

    public static string? ExtraSkip(string name) =>
        ExtraSkips.FirstOrDefault(s => new ExcludeFilter([s.Pattern]).IsExcluded(name)).Why;

    public static bool NeedsDryHydraulics(string name) => new ExcludeFilter(NeedsNoHydraulics).IsExcluded(name);

    /// <param name="positionExtras">Vars rejected as [0,1] by the verified phase (they move on to positions).</param>
    public static (List<Target> Targets, List<Skipped> Skipped) For(Phase phase, Inputs inputs, IEnumerable<string>? positionExtras = null)
    {
        var candidates = phase switch
        {
            Phase.Verified => inputs.Profile.Vars
                .Where(v => v.Class == SyncClass.Verified && (v.Name == ApuMaster || (v.Note?.Contains(ConfirmNote) ?? false)))
                .Select(v => new Target(v.Name, phase, BinaryPlusTwo)),
            Phase.Buttons => inputs.Review
                .Where(r => r.Reason.Contains("momentary or latching unknown") && !LeversNotButtons.Contains(r.Name))
                .Select(r => new Target(r.Name, phase, null, Counter: true)),
            Phase.Positions => inputs.Review
                .Where(r => r.Reason.Contains("multi-position") || r.Reason.StartsWith("scan value"))
                .Select(r => r.Name)
                .Concat(LeversNotButtons.Where(n => inputs.Review.Any(r => r.Name == n)))
                .Concat(positionExtras ?? [])
                .Distinct()
                .Select(n => new Target(n, phase, Steps)),
            Phase.Encoders => inputs.Review
                .Where(r => r.Name.StartsWith("E_", StringComparison.Ordinal) && !r.Name.StartsWith("E_CB_", StringComparison.Ordinal))
                .Select(r => new Target(r.Name, phase, null, Delta: true)),
            Phase.Breakers => inputs.Deny.Breakers.Select(b => new Target(b, phase, [0, 1, 2, 3])),
            Phase.Takeover => TakeoverVars.Select(n => new Target(n, phase, Binary)),
            Phase.Hold => HoldVars.Select(n => new Target(n, phase, NdValues)),
            _ => [],
        };

        var targets = new List<Target>();
        var skipped = new List<Skipped>();
        var supervised = phase is Phase.Takeover or Phase.Hold; // the brief's explicit supervised checks
        foreach (var t in candidates)
        {
            if (!inputs.Known(t.Name)) { skipped.Add(new(t.Name, phase, "not in the scan or profile")); continue; }
            if (t.Name.StartsWith("E_CB_", StringComparison.Ordinal) && !inputs.Deny.Breakers.Contains(t.Name))
            { skipped.Add(new(t.Name, phase, "breaker not in the probe sample")); continue; }
            if (!supervised && inputs.Deny.Denies(t.Name) is { } rule) { skipped.Add(new(t.Name, phase, "deny list: " + rule.Reason)); continue; }
            if (!supervised && ExtraSkip(t.Name) is { } why) { skipped.Add(new(t.Name, phase, "extra skip: " + why)); continue; }
            targets.Add(t);
        }
        return (targets, skipped);
    }
}
