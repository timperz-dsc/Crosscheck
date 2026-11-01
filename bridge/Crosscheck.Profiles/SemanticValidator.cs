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
        }

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
        }

        if (_profile.Roles?.Takeover is { } takeover) CheckTakeover(takeover, varsByName);
    }

    private void CheckTakeover(TakeoverRole takeover, Dictionary<string, int> varsByName)
    {
        foreach (var (seat, name) in new[] { ("captain", takeover.Captain), ("fo", takeover.Fo) })
        {
            var at = $"/roles/takeover/{seat}";
            if (!varsByName.TryGetValue(name, out var i))
                Add(at, $"takeover var '{name}' is not defined in this profile");
            else if (_profile.Vars[i].Class is not (SyncClass.Verified or SyncClass.Pulse))
                Add(at, $"takeover var '{name}' must be a pilot input (class verified or pulse), it has {Lower(_profile.Vars[i].Class)}");
        }
        if (takeover.Captain == takeover.Fo)
            Add("/roles/takeover/fo", "captain and fo need different takeover vars");
    }

    private void CheckClassFields(VarEntry v, string at)
    {
        var cls = Lower(v.Class);

        switch (v.Class)
        {
            case SyncClass.Verified when v.Values is null && v.Range is null:
                Add(at, "class verified needs 'values' or 'range'");
                break;
            case SyncClass.Analog when v.Range is null:
                Add(at, "class analog needs 'range'");
                break;
            case SyncClass.Delta when v.Display is null:
                Add(at, "class delta needs 'display' (the var that shows the value the encoder changes)");
                break;
            case SyncClass.Pulse when v.PulseMs is null:
                Add(at, "class pulse needs 'pulse_ms'");
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

        void Unused(bool present, string field, SyncClass[] allowed)
        {
            if (present && !allowed.Contains(v.Class))
                Add($"{at}/{field}", $"'{field}' is not used by class {cls}");
        }
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
