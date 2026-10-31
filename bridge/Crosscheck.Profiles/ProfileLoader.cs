using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Crosscheck.Profiles.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Crosscheck.Profiles;

public sealed record ProfileLoadResult(string Source, Profile? Profile, IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Profile is not null && Issues.Count == 0;

    public IEnumerable<string> FormatIssues() => Issues.Select(i => i.Format(Source));
}

public static class ProfileLoader
{
    /// <summary>The newest profile format this build understands.</summary>
    public const int SupportedFormat = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static ProfileLoadResult LoadFile(string path) =>
        LoadText(File.ReadAllText(path), Path.GetFileName(path));

    public static ProfileLoadResult LoadText(string yaml, string source = "profile.yaml")
    {
        YamlMappingNode rootNode;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode map)
                return Fail(source, 1, "", "The file must be a YAML mapping with 'format', 'aircraft', 'groups' and 'vars'.");
            if (stream.Documents.Count > 1)
                return Fail(source, (int)stream.Documents[1].RootNode.Start.Line, "", "The file has more than one YAML document, use exactly one.");
            rootNode = map;
        }
        catch (YamlException e)
        {
            return Fail(source, (int)Math.Max(1, e.Start.Line), "", $"YAML error: {Inner(e)}");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            // YamlDotNet reports a repeated key as ArgumentException, and some syntax errors as
            // InvalidOperationException, both without a position.
            return Fail(source, 1, "", e is ArgumentException
                ? $"YAML error: {e.Message} (a key is written twice in the same block)"
                : "YAML error: the file is not valid YAML (check brackets, quotes and indentation).");
        }

        var lines = new LineMap();
        var root = YamlConverter.Convert(rootNode, "", lines);

        if (root is JsonObject obj && obj["format"] is JsonValue fv && fv.TryGetValue<long>(out var format) && format != SupportedFormat)
        {
            var hint = format > SupportedFormat ? "This profile is newer than this bridge, update Crosscheck." : "This profile is too old, convert it to the current format.";
            return Fail(source, lines.Find("/format"), "format", $"profile format {format} is not supported (this bridge understands {SupportedFormat}). {hint}");
        }

        var schemaIssues = SchemaValidator.Validate(root, lines);
        if (schemaIssues.Count > 0)
            return new ProfileLoadResult(source, null, Sorted(schemaIssues));

        Profile profile;
        try
        {
            profile = root.Deserialize<Profile>(JsonOptions)!;
        }
        catch (JsonException e)
        {
            return Fail(source, lines.Find(e.Path?.Replace('.', '/').TrimStart('$') ?? ""), "", $"Cannot read the profile: {e.Message}");
        }

        var issues = SemanticValidator.Validate(profile, root, lines);
        return new ProfileLoadResult(source, issues.Count == 0 ? profile : null, Sorted(issues));
    }

    private static List<ValidationIssue> Sorted(List<ValidationIssue> issues) =>
        issues.OrderBy(i => i.Line).ToList();

    private static ProfileLoadResult Fail(string source, int line, string path, string message) =>
        new(source, null, [new ValidationIssue(line, path, message)]);

    private static string Inner(YamlException e) => e.InnerException?.Message ?? e.Message;
}
