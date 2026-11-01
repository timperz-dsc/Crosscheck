using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Crosscheck.Bridge.Protocol;

/// <summary>
/// Validates protocol messages against protocol/schema (embedded at build time), the same
/// files the server validates against.
/// </summary>
public sealed class MessageValidator
{
    private const string Prefix = "protocol-schema/";
    private static readonly Lazy<MessageValidator> SharedInstance = new(() => new MessageValidator());
    private readonly Dictionary<string, JsonSchema> _byType = new(StringComparer.Ordinal);

    public static MessageValidator Shared => SharedInstance.Value;

    private MessageValidator()
    {
        var asm = Assembly.GetExecutingAssembly();
        string Read(string name)
        {
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        var names = asm.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var common = names.Single(n => n.EndsWith("common.schema.json", StringComparison.Ordinal));
        JsonSchema.FromText(Read(common), options);
        foreach (var name in names.Where(n => n.Contains("messages", StringComparison.Ordinal)))
        {
            var type = Path.GetFileName(name.Replace('\\', '/')).Replace(".schema.json", "", StringComparison.Ordinal);
            _byType[type] = JsonSchema.FromText(Read(name), options);
        }
        if (_byType.Count == 0) throw new InvalidOperationException("protocol schemas are not embedded");
    }

    public IReadOnlyCollection<string> Types => _byType.Keys;

    /// <summary>null if valid, else a short reason.</summary>
    public string? Validate(JsonNode message)
    {
        var type = (message as JsonObject)?["type"]?.GetValue<string>() ?? "";
        if (!_byType.TryGetValue(type, out var schema)) return $"unknown message type \"{type}\"";
        var result = schema.Evaluate(JsonSerializer.SerializeToElement(message), new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return null;
        var first = result.Details?.FirstOrDefault(d => d.Errors is { Count: > 0 });
        var detail = first is null ? "" : $"{first.InstanceLocation}: {string.Join("; ", first.Errors!.Values)}";
        return $"{type} does not match the schema {detail}".TrimEnd();
    }
}
