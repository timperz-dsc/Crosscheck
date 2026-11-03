using System.Text.Json.Nodes;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Profiles;

/// <summary>Rules the JSON schema cannot express: class rules, duplicates and references inside the profile.</summary>
internal sealed class SemanticValidator
{
    private static readonly string[] GraphqlVarKinds = ["switches", "indicators", "numerical", "analog"];

    private readonly Profile _profile;
    private readonly JsonNode? _root;
    private readonly LineMap _lines;
    private readonly List<ValidationIssue> _issues = [];

    private SemanticValidator(Profile profile, JsonNode? root, LineMap lines)
    {
        _profile = profile;
        _root = root;
        _lines = lines;
    }

    public static List<ValidationIssue> Validate(Profile profile, JsonNode? root, LineMap lines)
    {
        var validator = new SemanticValidator(profile, root, lines);
        validator.Run();
        return validator._issues;
    }

    private void Add(string pointer, string message) =>
        _issues.Add(new ValidationIssue(_lines.Find(pointer), PathText.Format(pointer, _root), message));

    private void Run()
    {
        var groups = _profile.Groups.ToHashSet();
        var exclude = new ExcludeFilter(_profile.Exclude);
        var varsByName = new Dictionary<string, int>();
        var datarefPaths = new Dictionary<string, int>();

        for (var i = 0; i < _profile.Vars.Count; i++)
        {
            var name = _profile.Vars[i].Name;
            if (varsByName.TryGetValue(name, out var first))
                Add($"/vars/{i}/name", $"duplicate var name, first defined at line {_lines.Find($"/vars/{first}/name")}");
            else
                varsByName[name] = i;
        }

        for (var i = 0; i < (_profile.Datarefs?.Count ?? 0); i++)
        {
            var path = _profile.Datarefs![i].Path;
            if (datarefPaths.TryGetValue(path, out var first))
                Add($"/datarefs/{i}/path", $"duplicate dataref path, first defined at line {_lines.Find($"/datarefs/{first}/path")}");
            else
                datarefPaths[path] = i;
        }

        for (var i = 0; i < _profile.Vars.Count; i++)
        {
            var v = _profile.Vars[i];
            var at = $"/vars/{i}";

            if (!groups.Contains(v.Group))
                Add($"{at}/group", $"unknown group '{v.Group}' (not listed in 'groups')");
            if (exclude.Match(v.Name) is { } pattern)
                Add($"{at}/name", $"matches exclude pattern '{pattern}', so it cannot be a profile entry");

            CheckClassFields(v, at);
            CheckValues(v, at);
            if (v.Verify is { } verify) CheckVerify(verify, v.Name, $"{at}/verify", varsByName, datarefPaths);
            if (v.Class == SyncClass.Delta && v.Display is { } display) CheckDisplay(v.Name, display, at, varsByName);
            if (v.CompareWhen is { } when) CheckCompareWhen(v, when, at, varsByName);
            if (v.ValueLabels is { } labels) CheckValueLabels(v, labels, at);
            if (v.Control is { } control) CheckControl(v, control, at, varsByName);
            if (v.Powered is { } powered) CheckPowered(v, powered, at, varsByName);
            for (var k = 0; k < (v.Upstream?.Count ?? 0); k++)
            {
                var up = v.Upstream![k];
                if (up == v.Name) Add($"{at}/upstream/{k}", "upstream cannot be the var itself");
                else if (!varsByName.ContainsKey(up)) Add($"{at}/upstream/{k}", $"upstream var '{up}' is not defined in this profile");
            }
        }

        foreach (var group in (_profile.GroupPriority ?? new Dictionary<string, int>()).Keys)
            if (!groups.Contains(group))
                Add($"/group_priority/{group}", $"group_priority names '{group}', which is not listed in 'groups'");
        for (var i = 0; i < (_profile.OutsideGroups?.Count ?? 0); i++)
            if (!groups.Contains(_profile.OutsideGroups![i]))
                Add($"/outside_groups/{i}", $"outside_groups names '{_profile.OutsideGroups[i]}', which is not listed in 'groups'");
        foreach (var group in (_profile.GroupLabels ?? new Dictionary<string, string>()).Keys)
            if (!groups.Contains(group))
                Add($"/group_labels/{group}", $"group_labels names '{group}', which is not listed in 'groups'");

        for (var i = 0; i < (_profile.Datarefs?.Count ?? 0); i++)
        {
            var d = _profile.Datarefs![i];
            var at = $"/datarefs/{i}";
            if (!groups.Contains(d.Group))
                Add($"{at}/group", $"unknown group '{d.Group}' (not listed in 'groups')");
            if (d.Class is SyncClass.Owned or SyncClass.Observe)
            {
                if (d.PollHz is null) Add($"{at}/class", $"class {Lower(d.Class)} needs 'poll_hz'");
            }
            else if (d.PollHz is not null)
                Add($"{at}/poll_hz", $"'poll_hz' is not used by class {Lower(d.Class)}");
            if (d.DriftWarn is not null && d.Class != SyncClass.Owned)
                Add($"{at}/drift_warn", $"'drift_warn' is only used by class owned");
            if (d.DriftStep is not null && d.Class != SyncClass.Owned)
                Add($"{at}/drift_step", $"'drift_step' is only used by class owned");
        }

        if (_profile.Roles?.Takeover is { } takeover) CheckTakeover(takeover, varsByName);
    }

    private void CheckValueLabels(VarEntry v, IReadOnlyDictionary<string, string> labels, string at)
    {
        foreach (var key in labels.Keys)
        {
            var value = double.Parse(key, System.Globalization.CultureInfo.InvariantCulture); // the schema checked the format
            var allowed = v.Values is { } values ? values.Contains(value)
                : v.Range is [var min, var max] ? value >= min && value <= max
                : true;
            if (!allowed)
                Add($"{at}/value_labels/{key}", $"value_labels has {key}, which is not an allowed value of {v.Name}");
        }
    }

    private void CheckTakeover(TakeoverRole takeover, Dictionary<string, int> varsByName)
    {
        foreach (var (seat, name) in new[] { ("captain", takeover.Captain), ("fo", takeover.Fo) })
        {
            var at = $"/roles/takeover/{seat}";
            if (!varsByName.TryGetValue(name, out var i))
                Add(at, $"takeover var '{name}' is not defined in this profile");
            else if (_profile.Vars[i] is not { Class: SyncClass.Pulse, Press: PressMode.Edge })
                Add(at, $"takeover var '{name}' must be a push button with press: edge (its press is held back until control is granted)");
        }
        if (takeover.Captain == takeover.Fo)
            Add("/roles/takeover/fo", "captain and fo need different takeover vars");
    }

    private void CheckClassFields(VarEntry v, string at)
    {
        var cls = Lower(v.Class);

        switch (v.Class)
        {
            case SyncClass.Verified when v.Values is null && v.Range is null && !v.NeverWrite:
                Add(at, "class verified needs 'values' or 'range'");
                break;
            case SyncClass.Analog when v.Range is null:
                Add(at, "class analog needs 'range'");
                break;
            case SyncClass.Pulse when v.Press == PressMode.Edge && v.PulseMs is not null:
                Add($"{at}/pulse_ms", "press edge mirrors each press and release as it happens; 'pulse_ms' is not used");
                break;
            case SyncClass.Pulse when v.Press != PressMode.Edge && v.PulseMs is null:
                Add(at, "class pulse needs 'pulse_ms' (or press: edge)");
                break;
        }

        if (v.Values is not null && v.Range is not null)
            Add($"{at}/range", "use either 'values' or 'range', not both");

        Unused(v.Values is not null, "values", [SyncClass.Verified]);
        Unused(v.Range is not null, "range", [SyncClass.Verified, SyncClass.Analog]);
        Unused(v.Verify is not null, "verify", [SyncClass.Verified, SyncClass.Delta, SyncClass.Pulse, SyncClass.Analog, SyncClass.Owned]);
        Unused(v.PulseMs is not null, "pulse_ms", [SyncClass.Pulse]);
        Unused(v.Press is not null, "press", [SyncClass.Pulse]);
        Unused(v.Display is not null, "display", [SyncClass.Delta]);
        Unused(v.RateHz is not null, "rate_hz", [SyncClass.Stream]);
        Unused(v.Rest is not null, "rest", [SyncClass.Verified]);
        Unused(v.Alarm is not null, "alarm", [SyncClass.Observe]);
        Unused(v.NeverWrite, "never_write", [SyncClass.Verified, SyncClass.Pulse]);
        Unused(v.CompareWhen is not null, "compare_when", [SyncClass.Delta]);
        Unused(v.Tolerance is not null, "tolerance", [SyncClass.Observe]);
        Unused(v.Powered is not null, "powered", [SyncClass.Observe]);
        Unused(v.Notice is not null, "notice", [SyncClass.Observe]);
        SyncClass[] onPanel = [SyncClass.Verified, SyncClass.Delta, SyncClass.Pulse, SyncClass.Analog, SyncClass.Observe];
        Unused(v.Control is not null, "control", onPanel);
        Unused(v.Priority is not null, "priority", onPanel);
        Unused(v.Upstream is not null, "upstream", onPanel);
        if (v.Tolerance is not null && v.Alarm is not null)
            Add($"{at}/tolerance", "an alarm indicator shows a pilot input and is compared exactly; 'tolerance' is not used with 'alarm'");
        if (v.Notice is not null && v.Alarm is not null)
            Add($"{at}/notice", "an indicator is either red ('alarm') or amber ('notice'), not both");
        if (v.NeverWrite && v.Rest is not null)
            Add($"{at}/rest", "a never_write var is never written, so it has no 'rest' to return to");
        if (v is { Class: SyncClass.Verified, Rest: { } rest })
        {
            var ok = v.Values?.Contains(rest) ?? (v.Range is { Count: 2 } r && rest >= r[0] && rest <= r[1]);
            if (!ok) Add($"{at}/rest", $"rest {rest} must be one of the allowed values");
        }

        void Unused(bool present, string field, SyncClass[] allowed)
        {
            if (present && !allowed.Contains(v.Class))
                Add($"{at}/{field}", $"'{field}' is not used by class {cls}");
        }
    }

    private void CheckControl(VarEntry v, string control, string at, Dictionary<string, int> varsByName)
    {
        if (_profile.Controls?.ContainsKey(control) == true) return;
        if (control == v.Name)
            Add($"{at}/control", "control cannot be the var itself (leave it out: a var is its own control)");
        else if (!varsByName.TryGetValue(control, out var target))
            Add($"{at}/control", $"control '{control}' is neither a var of this profile nor listed in 'controls'");
        else if (_profile.Vars[target].Control is { } next)
            Add($"{at}/control", $"control '{control}' belongs to control '{next}' itself; name '{next}' directly");
        else if (_profile.Vars[target].Class == SyncClass.Ignore)
            Add($"{at}/control", $"control '{control}' has class ignore, so it is never on the panel");
    }

    private void CheckPowered(VarEntry v, string powered, string at, Dictionary<string, int> varsByName)
    {
        if (powered == v.Name)
            Add($"{at}/powered", "powered cannot be the var itself");
        else if (!varsByName.TryGetValue(powered, out var target))
            Add($"{at}/powered", $"powered var '{powered}' is not defined in this profile");
        else if (_profile.Vars[target].Class != SyncClass.Observe)
            Add($"{at}/powered", $"powered var '{powered}' must have class observe (it is only read), it has {Lower(_profile.Vars[target].Class)}");
        else if (_profile.Vars[target].Tolerance is not null || _profile.Vars[target].Powered is not null)
            Add($"{at}/powered", $"powered var '{powered}' must be a plain flag (no tolerance, no powered of its own)");
    }

    private void CheckValues(VarEntry v, string at)
    {
        if (v.Range is { Count: 2 } range && range[0] >= range[1])
            Add($"{at}/range", $"range must be [min, max] with min < max, got [{range[0]}, {range[1]}]");
    }

    private void CheckDisplay(string name, string display, string at, Dictionary<string, int> varsByName)
    {
        if (display == name)
            Add($"{at}/display", "display cannot be the var itself");
        else if (!varsByName.TryGetValue(display, out var target))
            Add($"{at}/display", $"display var '{display}' is not defined in this profile");
        else if (_profile.Vars[target].Class != SyncClass.Observe)
            Add($"{at}/display", $"display var '{display}' must have class observe, it has {Lower(_profile.Vars[target].Class)}");
    }

    private void CheckCompareWhen(VarEntry v, CompareWhen when, string at, Dictionary<string, int> varsByName)
    {
        if (v.Display is null)
            Add($"{at}/compare_when", "'compare_when' needs 'display' (only a display is compared between the sims)");
        foreach (var (name, pointer) in new[] { (when.Powered, $"{at}/compare_when/powered") }
            .Concat((when.Same ?? []).Select((s, k) => ((string?)s, $"{at}/compare_when/same/{k}"))))
        {
            if (name is null) continue;
            if (!varsByName.TryGetValue(name, out var target))
                Add(pointer, $"compare_when var '{name}' is not defined in this profile");
            else if (_profile.Vars[target].Class is not SyncClass.Observe)
                Add(pointer, $"compare_when var '{name}' must have class observe (it is only read), it has {Lower(_profile.Vars[target].Class)}");
        }
    }

    private void CheckVerify(
        VerifySpec verify, string owner, string at, Dictionary<string, int> varsByName, Dictionary<string, int> datarefPaths)
    {
        var sources = (verify.Lvar is not null ? 1 : 0) + (verify.Mcdu is not null ? 1 : 0) + (verify.Graphql is not null ? 1 : 0);
        if (sources != 1)
        {
            Add(at, sources == 0
                ? "verify needs exactly one source: 'lvar', 'mcdu' or 'graphql'"
                : "verify has more than one source, use exactly one of 'lvar', 'mcdu' or 'graphql'");
            return;
        }

        if (verify.Lvar is { } lvar)
        {
            if (lvar == owner)
                Add($"{at}/lvar", "verify cannot point at the var itself, the read-back of its own value proves nothing");
            else if (!varsByName.ContainsKey(lvar))
                Add($"{at}/lvar", $"verify target '{lvar}' is not defined in this profile");
        }

        if (verify.Mcdu is not (null or 1 or 2))
            Add($"{at}/mcdu", "mcdu must be 1 or 2");
        if (verify.Contains is not null && verify.Mcdu is null)
            Add($"{at}/contains", "'contains' is only used with 'mcdu'");
        if (verify.Scale is not null && verify.Graphql is null)
            Add($"{at}/scale", "'scale' is only used with 'graphql'");

        if (verify.Graphql is { } path)
        {
            var parts = path.Split('.');
            if (parts.Length >= 3 && parts[0] == "system" && GraphqlVarKinds.Contains(parts[1]))
            {
                var name = string.Join('.', parts[2..]);
                if (!varsByName.ContainsKey(name))
                    Add($"{at}/graphql", $"'{path}' mirrors var '{name}', which is not defined in this profile");
            }
            else if (!datarefPaths.ContainsKey(path))
                Add($"{at}/graphql", $"verify target '{path}' is not listed in 'datarefs'");
        }
    }

    private static string Lower(SyncClass c) => c.ToString().ToLowerInvariant();
}
