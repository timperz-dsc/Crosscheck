// Pairs a switch with an indicator that clearly belongs to it. Only on an unambiguous name match.

using System.Text.RegularExpressions;

namespace ProfileGen;

sealed record Pairing(string Switch, string Target, string Rule);

static class EffectPairer
{
    public const int LampSettleMs = 150;  // effect vars react after ~105-120 ms (TYPES.md)
    public const int McduSettleMs = 600;  // key to screen ~450 ms inside Fenix (PROTOCOL.md)

    /// <param name="lamps">Names of observe vars that will exist in the profile.</param>
    public static List<Pairing> Pair(IEnumerable<Entry> entries, HashSet<string> lamps)
    {
        var result = new List<Pairing>();
        foreach (var e in entries)
        {
            if (e.Verify is not null || !e.Name.StartsWith("S_")) continue;

            var key = Regex.Match(e.Name, @"^S_CDU([12])_KEY_");
            if (key.Success && e.Class == "pulse")
            {
                e.Verify = new Verify("mcdu", key.Groups[1].Value, McduSettleMs);
                result.Add(new Pairing(e.Name, $"MCDU {key.Groups[1].Value} screen", "mcdu key"));
                continue;
            }

            var stem = e.Name[2..];
            if (e.Class == "pulse")
            {
                if (e.Name.StartsWith("S_ECAM_") && lamps.Contains("I_" + stem))
                    Add(e, "I_" + stem, "ECAM page button, lamp = page selected", result);
                continue;
            }
            if (e.Class != "verified" || e.Name.EndsWith("_Cover") || e.Name.StartsWith("E_")) continue;

            if (lamps.Contains("I_" + stem)) Add(e, "I_" + stem, "exact name", result);
            else if (lamps.Contains($"I_{stem}_L"))
            {
                // Airbus pushbuttons: lower half shows the switch state, upper half FAULT/AVAIL.
                if (lamps.Contains($"I_{stem}_U"))
                {
                    e.Notes.Add("lower-half rule");
                    Add(e, $"I_{stem}_L", "lower-half rule", result);
                }
                else Add(e, $"I_{stem}_L", "only _L exists", result);
            }
        }
        return result;
    }

    static void Add(Entry e, string lamp, string rule, List<Pairing> result)
    {
        e.Verify = new Verify("lvar", lamp, LampSettleMs);
        result.Add(new Pairing(e.Name, lamp, rule));
    }
}
