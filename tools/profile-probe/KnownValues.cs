// The range rule (CLAUDE.md): a test tool may only write values that are already known to exist
// for that var. Learned the hard way: stepping S_COCKPIT_DOOR to positions that do not exist
// most likely crashed the Fenix WASM in the first run.
//
// A value is known when:
// - it is the var's value in the scan, or
// - a valid probe result saw it: the value the var rested at, or a value that was written and
//   confirmed by an effect (lamp, GraphQL, other var), or a value the pilot read on a display, or
// - it is in the short list of ranges the owner confirmed (Confirmed below).
// Results from the time the Fenix WASM was crashed do not count.
//
// Counters have no fixed range and are the one exception: encoders (delta) and the MASTER
// CAUTION/WARNING press counters only get +1/-1 steps from the value just read, never an
// absolute value.

using Crosscheck.Profiles;

namespace ProfileProbe;

sealed class KnownValues
{
    /// <summary>Ranges the owner confirmed for this probe (2026-11-01, CRO-8 step 2 rerun).</summary>
    public static readonly (string Pattern, double[] Values, string Source)[] Confirmed =
    [
        ("S_FCU_EFIS*_ND_MODE", [0, 1, 2, 3, 4], "ND MODE selector, 0-4 (owner)"),
        ("S_FCU_EFIS*_ND_ZOOM", [0, 1, 2, 3, 4, 5], "ND RANGE selector, 0-5 (owner)"),
        ("E_CB_*", [0, 1], "circuit breaker, 0 and 1 only (owner)"),
        ("S_FC_*_INST_DISCONNECT", [0, 1], "takeover pushbutton, released 0 / held 1 (owner)"),
    ];

    readonly Dictionary<string, SortedSet<double>> _seen = new(StringComparer.Ordinal);

    public static KnownValues Build(Inputs inputs, IEnumerable<ProbeResult> results)
    {
        var k = new KnownValues();
        foreach (var (name, value) in inputs.Scan)
            if (double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                k.Add(name, d);
        var list = results.ToList();
        var window = Proposer.CrashWindowOf(list);
        foreach (var r in list.Select(r => Proposer.AfterCrash(r, window)))
            foreach (var v in SeenIn(r)) k.Add(r.Var, v);
        return k;
    }

    /// <summary>Values a single valid result proves exist.</summary>
    internal static IEnumerable<double> SeenIn(ProbeResult r)
    {
        if (r.Result is "invalid-crash" or "error" or "cancelled" || !r.Restored) yield break;
        if (r.Original is { } o) yield return o;
        switch (r.Result)
        {
            // written and confirmed by an effect
            // (not the old position test: it is switched off and its results are not trusted)
            case "confirmed" or "follows-value" when r.ProposedValues is { } pv:
                foreach (var v in pv) yield return v;
                break;
            case "pulse" when r.EffectOk == true:
                yield return 0;
                yield return 1;
                break;
            // [0,1] rejected because 2 showed a distinct state: 0, 1 and 2 all did something
            case "rejected" when r.EffectOk == true:
                yield return 0;
                yield return 1;
                yield return 2;
                break;
        }
        if (r.ProposedValueLabels is { } labels)
            foreach (var key in labels.Keys)
                if (double.TryParse(key, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var l))
                    yield return l;
    }

    void Add(string name, double v)
    {
        if (!double.IsFinite(v)) return;
        if (!_seen.TryGetValue(name, out var set)) _seen[name] = set = [];
        set.Add(v);
    }

    /// <summary>Only for unit tests: a var with these known values.</summary>
    internal static KnownValues Of(params (string Name, double[] Values)[] vars)
    {
        var k = new KnownValues();
        foreach (var (n, vs) in vars) foreach (var v in vs) k.Add(n, v);
        return k;
    }

    public static (double[] Values, string Source)? ConfirmedFor(string name) =>
        Confirmed.FirstOrDefault(c => new ExcludeFilter([c.Pattern]).IsExcluded(name)) is { Pattern: not null } c ? (c.Values, c.Source) : null;

    /// <summary>Values seen in the scan and in valid probe results (without the owner-confirmed ranges).</summary>
    public IReadOnlyList<double> Seen(string name) => _seen.TryGetValue(name, out var s) ? s.ToList() : [];

    /// <summary>Every value known to exist for this var.</summary>
    public IReadOnlyList<double> For(string name) =>
        Seen(name).Concat(ConfirmedFor(name)?.Values ?? []).Distinct().Order().ToList();

    public bool IsKnown(string name, double value) => For(name).Any(v => Math.Abs(v - value) < 1e-9);

    /// <summary>The planned values that are known to exist; the rest may not be written.</summary>
    public List<double> Filter(string name, IEnumerable<double> planned) => planned.Where(v => IsKnown(name, v)).ToList();
}
