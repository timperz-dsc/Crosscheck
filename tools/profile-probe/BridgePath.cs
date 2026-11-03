// writetest --bridge-profile (CRO-40): the supervised write through the bridge's own write path. The adapter
// gets the bridge's watch list (SyncEngine.PlanFor on the real profile, plus the probe's safety vars and the
// control's effect vars), so SimConnect sees the same definitions as in a session: the read chunks and the
// pool of write slots (SimDefinitions). Only the tested controls stay writable, with exactly the values the
// write test may write; every other var is watched read-only, so the guard refuses any other write.
// Gate test 3 cause A: with one definition per writable var, writes to vars late in the profile (the tray
// table among them) never landed. This proves the pool fixed it, with nothing but the tray table written.

using Crosscheck.Bridge;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles.Model;

namespace ProfileProbe;

static class BridgePath
{
    static readonly HashSet<SyncClass> Writable = [SyncClass.Verified, SyncClass.Analog, SyncClass.Owned, SyncClass.Delta, SyncClass.Pulse];

    /// <summary>The adapter profile and watch list for the bridge write path. Throws if a control is not a plain switch of the real profile.</summary>
    public static (Profile Profile, List<string> Watched) For(Profile real, UnsyncedList unsynced, IReadOnlyList<WriteControl> controls)
    {
        var allowed = controls.ToDictionary(c => c.Var, c => c.Allowed, StringComparer.Ordinal);
        foreach (var c in controls)
        {
            var spec = real.Vars.FirstOrDefault(v => v.Name == c.Var)
                ?? throw new InvalidOperationException($"{c.Var} is not in the profile");
            if (spec.Class != SyncClass.Verified || spec.NeverWrite || spec.Values is not { } values || c.Allowed.Any(a => !values.Contains(a)))
                throw new InvalidOperationException($"{c.Var}: --bridge-profile writes only verified switches whose profile values include every value the test writes");
        }
        var watched = SyncEngine.PlanFor(real, unsynced.Names).Watched
            .Concat(SafetyVars.Lvars)
            .Concat(controls.SelectMany(c => c.Effects))
            .Concat(controls.Select(c => c.Var))
            .Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var vars = real.Vars.Select(v => allowed.TryGetValue(v.Name, out var a) ? v with { Values = a, Rest = null }
            : Writable.Contains(v.Class) ? ReadOnly(v) : v).ToList();
        return (real with { Vars = vars, Datarefs = null, Roles = null }, watched);
    }

    /// <summary>Watched like every other var, never writable.</summary>
    static VarEntry ReadOnly(VarEntry v) => v with
    {
        Class = SyncClass.Observe, Values = null, Range = null, Verify = null, Press = null, PulseMs = null, Display = null,
        Rest = null, NeverWrite = false, CompareWhen = null,
    };

    /// <summary>One line for the console and the results file.</summary>
    public static string Describe(int watched) =>
        $"bridge write path (--bridge-profile): {SimDefinitions.Total(watched)} SimConnect definitions " +
        $"({SimDefinitions.ReadChunks(watched)} read chunks for {watched} watched vars + {SimDefinitions.PoolSize} write slots), as in a bridge session";
}
