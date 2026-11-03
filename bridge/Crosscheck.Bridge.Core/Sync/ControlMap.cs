using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Sync;

/// <summary>
/// Which vars the pilots see as one control on the panel (CRO-34): a button with its lamp, a knob with
/// its display and mode flags, an RMP's inner and outer knob. From the profile: an explicit `control:`,
/// else the button whose verify lamp it is, else the encoder whose display or mode flag it is, else the
/// var itself. Also the control's name, panel group, priority and whether it is outside the cockpit.
/// </summary>
public sealed class ControlMap
{
    private readonly Profile _profile;
    private readonly Labels _labels;
    private readonly Dictionary<string, VarEntry> _vars;
    private readonly Dictionary<string, string> _controlOf = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal);
    private readonly HashSet<string> _outside;

    private readonly IReadOnlyDictionary<string, string> _extraLabels;

    /// <param name="unsyncedLabels">Names of controls that are not synced yet (unsynced.csv), so they never show raw.</param>
    public ControlMap(Profile profile, Labels labels, IReadOnlyDictionary<string, string>? unsyncedLabels = null)
    {
        _profile = profile;
        _labels = labels;
        _extraLabels = unsyncedLabels ?? new Dictionary<string, string>();
        _vars = profile.Vars.ToDictionary(v => v.Name, StringComparer.Ordinal);
        _outside = (profile.OutsideGroups ?? []).ToHashSet(StringComparer.Ordinal);
        var onPanel = profile.Vars.Where(v => v.Class is not (SyncClass.Ignore or SyncClass.Stream)).ToList();

        // Links nobody wrote down: a lamp that is the verify target of exactly one control, an encoder's
        // display, and a B_ mode flag that only one encoder depends on.
        var linked = new Dictionary<string, string>(StringComparer.Ordinal);
        void Link(IEnumerable<(string Member, string Owner)> pairs)
        {
            foreach (var g in pairs.GroupBy(p => p.Member))
                if (g.Select(p => p.Owner).Distinct().Count() == 1 && g.Key != g.First().Owner) linked.TryAdd(g.Key, g.First().Owner);
        }
        Link(onPanel.Where(v => v.Verify?.Lvar is { } l && _vars.TryGetValue(l, out var lamp) && lamp.Class == SyncClass.Observe)
            .Select(v => (v.Verify!.Lvar!, v.Name)));
        Link(onPanel.Where(v => v.Display is not null).Select(v => (v.Display!, v.Name)));
        Link(onPanel.Where(v => v.CompareWhen?.Same is not null)
            .SelectMany(v => v.CompareWhen!.Same!.Where(s => s.StartsWith("B_", StringComparison.Ordinal)).Select(s => (s, v.Name))));

        string Resolve(string name, int depth = 0)
        {
            if (depth > 4 || !_vars.TryGetValue(name, out var v)) return name;
            if (v.Control is { } c) return _vars.ContainsKey(c) ? Resolve(c, depth + 1) : c;
            return linked.TryGetValue(name, out var owner) ? Resolve(owner, depth + 1) : name;
        }
        foreach (var v in onPanel)
        {
            var id = Resolve(v.Name);
            _controlOf[v.Name] = id;
            if (!_members.TryGetValue(id, out var list)) _members[id] = list = [];
            list.Add(v.Name);
        }
    }

    /// <summary>The control a var (or an MCDU screen, or a dataref) is shown under.</summary>
    public string ControlOf(string name) =>
        _controlOf.TryGetValue(name, out var id) ? id
        : McduOf(name) is { } cdu ? $"aircraft.mcdu{cdu}.display"
        : name;

    public IReadOnlyList<string> Members(string control) => _members.TryGetValue(control, out var m) ? m : [control];

    /// <summary>The name the pilots see. Never the raw var name while the profile has a label.</summary>
    public string Label(string control)
    {
        if (_profile.Controls?.GetValueOrDefault(control) is { } label) return label;
        if (McduOf(control) is { } cdu) return $"CDU {cdu} screen";
        if (!_vars.ContainsKey(control) && _extraLabels.GetValueOrDefault(control) is { } extra) return extra;
        return _labels.Var(control);
    }

    /// <summary>A var, dataref or MCDU screen this profile compares. Anything else is not synced by this profile.</summary>
    public bool IsKnown(string name) =>
        _vars.ContainsKey(name) || McduOf(name) is not null || _profile.Datarefs?.Any(d => d.Path == name) == true;

    public string? Group(string control)
    {
        if (_vars.TryGetValue(control, out var v)) return v.Group;
        if (McduOf(control) is { } cdu) return $"cdu{cdu}";
        foreach (var m in Members(control)) if (_vars.TryGetValue(m, out var mv)) return mv.Group;
        return _profile.Datarefs?.FirstOrDefault(d => d.Path == control)?.Group;
    }

    /// <summary>2 flight-critical … 5 lights, cabin and the rest (safety lines are 1, decided by the line).</summary>
    public int Priority(string control)
    {
        var main = _vars.GetValueOrDefault(control) ?? Members(control).Select(m => _vars.GetValueOrDefault(m)).FirstOrDefault(m => m is not null);
        if (main?.Priority is { } p) return p;
        return Group(control) is { } g && _profile.GroupPriority?.GetValueOrDefault(g) is { } gp and > 0 ? gp : 5;
    }

    /// <summary>A walkaround item: one amber "outside" line, never red.</summary>
    public bool Outside(string control) => Group(control) is { } g && _outside.Contains(g);

    public VarEntry? Spec(string name) => _vars.GetValueOrDefault(name);

    private static int? McduOf(string name) =>
        name.StartsWith("aircraft.mcdu", StringComparison.Ordinal) && name.Length > 13 && char.IsDigit(name[13]) ? name[13] - '0' : null;
}
