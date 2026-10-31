using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Crosscheck.Profiles;

/// <summary>Turns the YAML node tree into a JsonNode tree and records the line of every key and item.</summary>
internal static partial class YamlConverter
{
    public static JsonNode? Convert(YamlNode node, string pointer, LineMap lines)
    {
        switch (node)
        {
            case YamlMappingNode map:
            {
                var obj = new JsonObject();
                foreach (var (keyNode, valueNode) in map.Children)
                {
                    var key = (keyNode as YamlScalarNode)?.Value ?? "";
                    var child = LineMap.Child(pointer, key);
                    lines.Set(child, (int)keyNode.Start.Line);
                    obj[key] = Convert(valueNode, child, lines);
                }
                return obj;
            }
            case YamlSequenceNode seq:
            {
                var arr = new JsonArray();
                var i = 0;
                foreach (var item in seq.Children)
                {
                    var child = LineMap.Child(pointer, i++);
                    lines.Set(child, (int)item.Start.Line);
                    arr.Add(Convert(item, child, lines));
                }
                return arr;
            }
            case YamlScalarNode scalar:
                return Scalar(scalar);
            default:
                return null;
        }
    }

    private static JsonNode? Scalar(YamlScalarNode scalar)
    {
        var text = scalar.Value ?? "";
        if (scalar.Style != ScalarStyle.Plain) return JsonValue.Create(text);

        switch (text)
        {
            case "" or "~" or "null" or "Null" or "NULL":
                return null;
            case "true" or "True" or "TRUE":
                return JsonValue.Create(true);
            case "false" or "False" or "FALSE":
                return JsonValue.Create(false);
        }

        if (IntegerRegex().IsMatch(text) && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (FloatRegex().IsMatch(text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(text);
    }

    [GeneratedRegex(@"^[-+]?[0-9]+$")]
    private static partial Regex IntegerRegex();

    [GeneratedRegex(@"^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$")]
    private static partial Regex FloatRegex();
}
