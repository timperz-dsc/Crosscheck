// `profile-gen diff <old.yaml> <new.yaml>`: lists every var that was added, removed or changed
// between two profiles (field by field), and the new profile hash. Used to prove that a profile
// change touched only the intended vars. Reads the YAML as plain data, so an old profile in an
// older format can be compared too.

using System.Security.Cryptography;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace ProfileGen;

static class ProfileDiff
{
    public static int Run(string oldPath, string newPath, TextWriter output)
    {
        var before = Vars(oldPath);
        var after = Vars(newPath);
        var added = after.Keys.Except(before.Keys).Order(StringComparer.Ordinal).ToList();
        var removed = before.Keys.Except(after.Keys).Order(StringComparer.Ordinal).ToList();
        var changed = before.Keys.Intersect(after.Keys).Where(n => !Same(before[n], after[n])).Order(StringComparer.Ordinal).ToList();

        output.WriteLine($"added {added.Count}, removed {removed.Count}, changed {changed.Count}");
        foreach (var n in added) output.WriteLine($"+ {n}: {Text(after[n])}");
        foreach (var n in removed) output.WriteLine($"- {n}: {Text(before[n])}");
        foreach (var n in changed)
        {
            var diffs = before[n].Keys.Union(after[n].Keys)
                .Where(k => before[n].GetValueOrDefault(k) != after[n].GetValueOrDefault(k))
                .Select(k => $"{k} {before[n].GetValueOrDefault(k) ?? "-"} -> {after[n].GetValueOrDefault(k) ?? "-"}");
            output.WriteLine($"~ {n}: {string.Join("; ", diffs)}");
        }
        output.WriteLine($"profile hash: {Hash(newPath)}");
        return 0;
    }

    /// <summary>Same as the bridge's ProfileHash: sha256 of the file with CRLF normalised to LF.</summary>
    public static string Hash(string path)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>Every entry of vars and datarefs (by name or path) with its fields as text; notes are left out.</summary>
    static Dictionary<string, Dictionary<string, string>> Vars(string path)
    {
        var yaml = new YamlStream();
        using (var reader = new StreamReader(path)) yaml.Load(reader);
        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var key in new[] { "vars", "datarefs" })
        {
            if (!root.Children.TryGetValue(key, out var list) || list is not YamlSequenceNode seq) continue;
            foreach (var item in seq.Children.OfType<YamlMappingNode>())
            {
                var fields = item.Children.ToDictionary(kv => ((YamlScalarNode)kv.Key).Value!, kv => Flat(kv.Value));
                var name = fields.GetValueOrDefault("name") ?? fields.GetValueOrDefault("path") ?? "?";
                fields.Remove("name");
                fields.Remove("path");
                fields.Remove("note");
                result[name] = fields;
            }
        }
        if (root.Children.TryGetValue("roles", out var roles)) result["(roles)"] = new() { ["roles"] = Flat(roles) };
        return result;
    }

    static string Flat(YamlNode n) => n switch
    {
        YamlScalarNode s => s.Value ?? "",
        YamlSequenceNode q => "[" + string.Join(",", q.Children.Select(Flat)) + "]",
        YamlMappingNode m => "{" + string.Join(",", m.Children.Select(kv => Flat(kv.Key) + ":" + Flat(kv.Value))) + "}",
        _ => "",
    };

    static bool Same(Dictionary<string, string> x, Dictionary<string, string> y) =>
        x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var v) && v == kv.Value);

    static string Text(Dictionary<string, string> f) => string.Join("; ", f.Select(kv => $"{kv.Key} {kv.Value}"));
}
