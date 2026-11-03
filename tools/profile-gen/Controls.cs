// Panel controls (CRO-34): which vars the pilots see as one control, and the panel priority of
// lights. Simple links (a button and its verify lamp, a knob and its display) are found by the
// bridge itself from verify/display; this pass only writes what those links cannot express:
// controls made of several knobs or buttons (an RMP's inner and outer knob, the EFIS ND filter
// buttons). Overrides (control:, priority:) always win.

using System.Text.RegularExpressions;

namespace ProfileGen;

static class Controls
{
    /// <summary>Synthetic controls by name: id pattern, label pattern. $1 is the unit number.</summary>
    static readonly (Regex Rx, string Id, string Label)[] Rules =
    [
        (new(@"^(?:E_PED_RMP(\d)_(?:INNER|OUTER)|N_PED_RMP(\d)_(?:ACTIVE|STDBY))$"), "rmp{n}_freq", "RMP {n} frequency"),
        // The ND filter buttons are mutually exclusive: pressing one clears the others.
        (new(@"^[SI]_FCU_EFIS([12])_(?:CSTR|WPT|VORD|NDB|ARPT)$"), "efis{n}_nd_filter", "ND filter ({side})"),
        // The ECAM page keys show one page at a time (ALL steps through them): one control.
        (new(@"^[SI]_ECAM_(?:ALL|APU|BLEED|CAB_PRESS|COND|DOOR|ELEC|ENGINE|FCTL|FUEL|HYD|STATUS|WHEEL)()$"), "ecam_page", "ECAM page"),
    ];

    // Lights and lighting knobs: priority 5 even on a flight-critical panel.
    static readonly Regex Light = new(@"^[SA]_.*(_LT_|_LT$|LIGHTING|_LIGHT)");

    // Indicators of a unit with a proven power flag (the CRO-33 encoder gates): FCU and EFIS lamps
    // and flags, and the RMP lamps and flags. Not compared while the unit is off in either sim.
    static readonly Regex FcuIndicator = new(@"^[IB]_FCU_");
    static readonly Regex RmpIndicator = new(@"^[IB]_PED_RMP([123])_");

    static string? PowerFlag(string name)
    {
        if (FcuIndicator.IsMatch(name)) return "B_FCU_POWER";
        var m = RmpIndicator.Match(name);
        return m.Success ? $"B_PED_RMP{m.Groups[1].Value}_POWER" : null;
    }

    /// <summary>Sets Control, Priority and Powered on the entries. Returns the synthetic controls used (id -> label).</summary>
    public static SortedDictionary<string, string> Assign(List<Entry> entries, IReadOnlyDictionary<string, int> groupPriority)
    {
        var used = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (e.Class == "ignore") continue;
            if (e.Control is null)
                foreach (var (rx, id, label) in Rules)
                {
                    var m = rx.Match(e.Name);
                    if (!m.Success) continue;
                    var n = m.Groups.Values.Skip(1).First(g => g.Success).Value;
                    e.Control = id.Replace("{n}", n);
                    used[e.Control] = label.Replace("{n}", n).Replace("{side}", n == "1" ? "Capt" : "F/O");
                    break;
                }
            if (e.Priority is null && Light.IsMatch(e.Name) && groupPriority.GetValueOrDefault(e.Group, 5) < 5)
                e.Priority = 5;
            if (e.Class == "observe" && e.Powered is null && PowerFlag(e.Name) is { } flag && flag != e.Name)
                e.Powered = flag;
        }
        return used;
    }
}
