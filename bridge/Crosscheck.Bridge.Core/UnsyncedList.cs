using System.Text.RegularExpressions;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge;

/// <summary>
/// Controls that exist but are not synced yet (profiles/&lt;aircraft&gt;/unsynced.csv, made by
/// profile-gen from the review list). The bridge watches them read-only so that moving one is
/// never silent: both pilots see "You moved &lt;label&gt; – not synced yet". They are never written:
/// the safety guard only allows profile vars.
/// </summary>
public sealed partial class UnsyncedList
{
    public const string FileName = "unsynced.csv";

    [GeneratedRegex("^[SA]_[A-Za-z0-9_]{1,126}$")]
    private static partial Regex NamePattern();

    private UnsyncedList(IReadOnlyDictionary<string, string> labels) => Labels = labels;

    public static UnsyncedList Empty { get; } = new(new Dictionary<string, string>());

    /// <summary>Name → label. Never contains a profile var.</summary>
    public IReadOnlyDictionary<string, string> Labels { get; }

    public IEnumerable<string> Names => Labels.Keys;

    /// <summary>unsynced.csv next to the profile; empty if there is none.</summary>
    public static UnsyncedList LoadNextTo(string profilePath, Profile profile)
    {
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(profilePath))!, FileName);
        return File.Exists(path) ? Parse(File.ReadAllText(path), profile) : Empty;
    }

    /// <summary>CSV with the header name,label. Only S_/A_ names; profile vars and bad rows are skipped.</summary>
    public static UnsyncedList Parse(string csv, Profile profile)
    {
        var inProfile = profile.Vars.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in csv.Split('\n').Skip(1))
        {
            var row = line.TrimEnd('\r');
            if (row.Length == 0) continue;
            var comma = row.IndexOf(',');
            if (comma <= 0) continue;
            var name = row[..comma];
            var label = Unquote(row[(comma + 1)..]);
            if (!NamePattern().IsMatch(name) || inProfile.Contains(name) || label.Length == 0) continue;
            labels.TryAdd(name, label.Length > 120 ? label[..120] : label);
        }
        return new UnsyncedList(labels);
    }

    private static string Unquote(string s) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace("\"\"", "\"") : s;
}
