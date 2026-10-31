using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Crosscheck.Profiles;

/// <summary>Checks the structure of a profile against profile.schema.json (embedded in this assembly).</summary>
internal static class SchemaValidator
{
    private static readonly Lazy<JsonSchema> Schema = new(Load);

    private static JsonSchema Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("profile.schema.json")
            ?? throw new InvalidOperationException("profile.schema.json is not embedded in Crosscheck.Profiles.");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    public static List<ValidationIssue> Validate(JsonNode? root, LineMap lines)
    {
        var issues = new List<ValidationIssue>();
        var results = Schema.Value.Evaluate(JsonSerializer.SerializeToElement(root), new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
        if (results.IsValid) return issues;

        var seen = new HashSet<(string, string)>();
        Collect(results, root, lines, issues, seen);
        if (issues.Count == 0)
            issues.Add(new ValidationIssue(1, "", "The file does not match the profile schema."));
        return issues;
    }

    private static void Collect(
        EvaluationResults result, JsonNode? root, LineMap lines, List<ValidationIssue> issues, HashSet<(string, string)> seen)
    {
        if (result.IsValid) return;

        var before = issues.Count;
        foreach (var detail in result.Details ?? [])
            Collect(detail, root, lines, issues, seen);

        // A parent only says "some properties failed". When a child has the real reason, show only that.
        if (issues.Count > before || result.Errors is not { Count: > 0 } errors) return;

        var pointer = result.InstanceLocation.ToString();
        foreach (var (keyword, message) in errors)
        {
            var (path, text) = Describe(pointer, keyword, message, root);
            if (seen.Add((path, text)))
                issues.Add(new ValidationIssue(lines.Find(pointer), PathText.Format(path, root), text));
        }
    }

    private static (string Pointer, string Message) Describe(string pointer, string keyword, string message, JsonNode? root)
    {
        // "additionalProperties: false" reports the unknown key as a failed property, which reads badly.
        if (keyword == "false" || message.Contains("false schema", StringComparison.OrdinalIgnoreCase))
        {
            var cut = pointer.LastIndexOf('/');
            var key = cut >= 0 ? pointer[(cut + 1)..].Replace("~1", "/").Replace("~0", "~") : pointer;
            return (pointer, $"unknown key '{key}'");
        }
        return (pointer, message);
    }
}
