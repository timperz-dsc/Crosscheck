// The in-memory profile the probe hands to the unchanged SimConnectAdapter. Only the current
// phase's targets are writable, each with exactly the values that phase writes; every other var
// is observe, so it is watched but the adapter's SimSafetyGuard refuses any write to it.

using System.Text.RegularExpressions;
using Crosscheck.Profiles.Model;

namespace ProfileProbe;

static partial class ProbeProfile
{
    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex PlainName();

    /// <summary>Every var the probe watches: scan (Fenix names), profile and review list.</summary>
    public static List<string> Watched(Inputs inputs) =>
        inputs.Scan.Keys.Concat(inputs.AllNames).Concat(SafetyVars.Lvars)
            .Where(n => PlainName().IsMatch(n)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();

    public static Profile Build(Inputs inputs, IEnumerable<Target> targets, IReadOnlyList<string> watched)
    {
        var writable = targets.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var vars = watched.Select(name => writable.TryGetValue(name, out var t) ? Writable(t) : new VarEntry(name, SyncClass.Observe, "probe")).ToList();
        return inputs.Profile with { Vars = vars, Datarefs = null, Roles = null };
    }

    static VarEntry Writable(Target t)
    {
        if (t.Delta) return new VarEntry(t.Name, SyncClass.Delta, "probe");
        if (t.Counter) return new VarEntry(t.Name, SyncClass.Pulse, "probe", PulseMs: 150, Press: PressMode.Counter);
        return new VarEntry(t.Name, SyncClass.Verified, "probe", Values: t.Values);
    }
}
