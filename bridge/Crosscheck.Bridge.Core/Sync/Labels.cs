using System.Globalization;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Sync;

/// <summary>Names the pilots see, from the profile's optional labels. Falls back to the var name and the number.</summary>
public sealed class Labels(Profile profile)
{
    private readonly Dictionary<string, VarEntry> _vars = profile.Vars.ToDictionary(v => v.Name, StringComparer.Ordinal);
    private readonly Dictionary<string, DatarefEntry> _datarefs = (profile.Datarefs ?? []).ToDictionary(d => d.Path, StringComparer.Ordinal);

    public string Var(string name) =>
        _vars.TryGetValue(name, out var v) && v.Label is { } l ? l
        : _datarefs.TryGetValue(name, out var d) && d.Label is { } dl ? dl
        : name;

    public bool HasLabel(string name) =>
        (_vars.TryGetValue(name, out var v) && v.Label is not null) || (_datarefs.TryGetValue(name, out var d) && d.Label is not null);

    public string Group(string id) => profile.GroupLabels?.GetValueOrDefault(id) ?? id;

    public string? Unit(string path) => _datarefs.TryGetValue(path, out var d) ? d.Unit : null;

    /// <summary>"ON" for 1 when value_labels says so; otherwise the number as written.</summary>
    public string Value(string name, double? value)
    {
        if (value is not { } v) return "unknown";
        var key = Number(v);
        return _vars.TryGetValue(name, out var spec) && spec.ValueLabels?.GetValueOrDefault(key) is { } label ? label : key;
    }

    public static string Number(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>What a pilot did, for the recent list: "APU MASTER on", "FCU altitude 5 clicks up", "CDU 1 F-PLN pressed".</summary>
    public string Action(string name, SyncClass cls, double? value, double? delta)
    {
        var label = Var(name);
        if (cls == SyncClass.Pulse) return $"{label} pressed";
        if (delta is { } d)
        {
            var clicks = Math.Abs(d);
            return $"{label} {Number(clicks)} {(clicks == 1 ? "click" : "clicks")} {(d >= 0 ? "up" : "down")}";
        }
        var text = Value(name, value);
        return text is "ON" or "OFF" ? $"{label} {text.ToLowerInvariant()}" : $"{label} set to {text}";
    }
}
