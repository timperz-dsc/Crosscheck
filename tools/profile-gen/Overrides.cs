// overrides.yaml: corrections the generator applies on top of its naming rules, so results
// from the sim (CRO-8 step 2 probe, owner decisions) survive every re-run of the tool.
// Entries inside the generated block must never be edited by hand: change this file and re-run.
//
//   overrides:
//     - name: S_X                      # a generated entry (patched) or a review-list var (promoted)
//       class: verified                # optional for a patch; required to promote
//       group: overhead                # optional; required to promote
//       values: [0, 1]                 # only values backed by scan, probe or owner (range rule)
//       range: [-3, 7]                 # analog: min and max backed by scan, pilot input or owner
//       display: N_X                   # delta only
//       verify: { lvar: I_X_L, settle_ms: 150 }    # or { graphql: path, settle_ms: 100 }
//       value_labels: { "0": "OFF", "1": "ON" }
//       pulse_ms: 150
//       press: counter
//       note: "..."                    # replaces the rule notes after "label generated"
//       comment: "..."                 # written as a comment line above the entry
//       evidence: "..."                # what backs it (not written to the profile)
//   open:                              # still open, listed in draft-report.md
//     - what: "..."
//       vars: [S_A, S_B]               # optional
//       why: "..."

using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace ProfileGen;

sealed record Override(
    string Name, string? Class, string? Group, double[]? Values, double[]? Range, string? Display, Verify? Verify,
    Dictionary<string, string>? ValueLabels, int? PulseMs, bool? Counter, string? Note, string? Comment, string Evidence);

sealed record OpenItem(string What, IReadOnlyList<string> Vars, string Why);

sealed class OverrideFile
{
    public List<Override> Overrides { get; } = [];
    public List<OpenItem> Open { get; } = [];

    public static OverrideFile Load(string path)
    {
        var file = new OverrideFile();
        if (!File.Exists(path)) return file;
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path)) yaml.Load(reader);
        if (yaml.Documents.Count == 0 || yaml.Documents[0].RootNode is not YamlMappingNode root) return file;

        if (root.Children.TryGetValue("overrides", out var list) && list is YamlSequenceNode seq)
            foreach (var item in seq.Children.Cast<YamlMappingNode>())
            {
                string? S(string key) => item.Children.TryGetValue(key, out var v) && v is YamlScalarNode s ? s.Value : null;
                var name = S("name") ?? throw new InvalidDataException($"{path}: an override has no name");
                double[]? values = item.Children.TryGetValue("values", out var vn) && vn is YamlSequenceNode vs
                    ? vs.Children.Select(c => double.Parse(((YamlScalarNode)c).Value!, CultureInfo.InvariantCulture)).ToArray() : null;
                double[]? range = item.Children.TryGetValue("range", out var rn) && rn is YamlSequenceNode rs
                    ? rs.Children.Select(c => double.Parse(((YamlScalarNode)c).Value!, CultureInfo.InvariantCulture)).ToArray() : null;
                if (range is not null && (range.Length != 2 || range[0] >= range[1]))
                    throw new InvalidDataException($"{path}: {name}: range needs [min, max] with min < max");
                Dictionary<string, string>? labels = item.Children.TryGetValue("value_labels", out var ln) && ln is YamlMappingNode lm
                    ? lm.Children.ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => ((YamlScalarNode)kv.Value).Value!) : null;
                Verify? verify = null;
                if (item.Children.TryGetValue("verify", out var vfn) && vfn is YamlMappingNode vf)
                {
                    string? V(string key) => vf.Children.TryGetValue(key, out var x) ? ((YamlScalarNode)x).Value : null;
                    var settle = int.Parse(V("settle_ms") ?? "150", CultureInfo.InvariantCulture);
                    verify = V("lvar") is { } lv ? new Verify("lvar", lv, settle)
                        : V("graphql") is { } gp ? new Verify("graphql", gp, settle, V("scale") is { } sc ? double.Parse(sc, CultureInfo.InvariantCulture) : null)
                        : throw new InvalidDataException($"{path}: {name}: verify needs lvar or graphql");
                }
                file.Overrides.Add(new Override(
                    name, S("class"), S("group"), values, range, S("display"), verify, labels,
                    S("pulse_ms") is { } pm ? int.Parse(pm, CultureInfo.InvariantCulture) : null,
                    S("press") is { } press ? press == "counter" : null,
                    S("note"), S("comment"),
                    S("evidence") ?? throw new InvalidDataException($"{path}: {name}: every override needs evidence")));
            }

        if (root.Children.TryGetValue("open", out var open) && open is YamlSequenceNode os)
            foreach (var item in os.Children.Cast<YamlMappingNode>())
            {
                string S(string key) => item.Children.TryGetValue(key, out var v) && v is YamlScalarNode s ? s.Value ?? "" : "";
                var vars = item.Children.TryGetValue("vars", out var vn) && vn is YamlSequenceNode vs
                    ? vs.Children.Select(c => ((YamlScalarNode)c).Value!).ToList() : [];
                file.Open.Add(new OpenItem(S("what"), vars, S("why")));
            }

        var dup = file.Overrides.GroupBy(o => o.Name).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidDataException($"{path}: {dup.Key} is overridden twice");
        return file;
    }

    /// <summary>
    /// Patches generated entries and promotes review vars. Returns the problems (unknown,
    /// excluded or hand-written var, a promotion without class or group); empty = all applied.
    /// </summary>
    public List<string> Apply(List<Entry> entries, List<Review> reviews, IReadOnlyDictionary<string, string> scanValues,
        ISet<string> handNames, Func<string, bool> excluded)
    {
        var problems = new List<string>();
        foreach (var o in Overrides)
        {
            if (handNames.Contains(o.Name)) { problems.Add($"{o.Name}: hand-written entry, edit it outside the generated block"); continue; }
            if (!scanValues.ContainsKey(o.Name)) { problems.Add($"{o.Name}: not in the scan"); continue; }
            if (excluded(o.Name)) { problems.Add($"{o.Name}: excluded by the profile"); continue; }

            var i = entries.FindIndex(e => e.Name == o.Name);
            if (i < 0)
            {
                if (o.Class is null || o.Group is null) { problems.Add($"{o.Name}: on the review list, promoting it needs class and group"); continue; }
                entries.Add(Build(o, o.Class, o.Group, scanValues[o.Name], null));
                reviews.RemoveAll(r => r.Name == o.Name);
                continue;
            }

            var e = entries[i];
            entries[i] = o.Class is not null && o.Class != e.Class
                ? Build(o, o.Class, o.Group ?? e.Group, e.Value, null) // a new class: only the override's fields
                : Build(o, e.Class, o.Group ?? e.Group, e.Value, e);
        }
        return problems;
    }

    static Entry Build(Override o, string cls, string group, string value, Entry? keep)
    {
        var silent = cls is "observe" or "ignore";
        var e = new Entry
        {
            Name = o.Name, Class = cls, Group = group, Value = value,
            Values = silent ? null : o.Values ?? keep?.Values,
            Range = silent ? null : o.Range ?? keep?.Range,
            PulseMs = cls == "pulse" ? o.PulseMs ?? keep?.PulseMs ?? 150 : null,
            Counter = cls == "pulse" && (o.Counter ?? keep?.Counter ?? false),
            ValueLabels = o.ValueLabels ?? keep?.ValueLabels,
            Comment = o.Comment ?? keep?.Comment,
        };
        e.Display = cls == "delta" ? o.Display ?? keep?.Display : null;
        e.Verify = silent ? null : o.Verify ?? keep?.Verify;
        if (o.Note is { } note) e.Notes.Add(note);
        else if (keep is not null) e.Notes.AddRange(keep.Notes.Skip(1));
        return e;
    }
}
