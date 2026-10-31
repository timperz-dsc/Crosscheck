using System.Text;
using System.Text.Json.Nodes;

namespace Crosscheck.Profiles;

internal static class PathText
{
    /// <summary>"/vars/3/verify/settle_ms" becomes `vars[3] "NAME" verify.settle_ms`, so nobody has to count entries.</summary>
    public static string Format(string pointer, JsonNode? root)
    {
        if (string.IsNullOrEmpty(pointer)) return "";
        var parts = pointer.TrimStart('/').Split('/').Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToArray();

        var text = new StringBuilder(parts[0]);
        var node = (root as JsonObject)?[parts[0]];
        var next = 1;

        if (parts.Length > 1 && int.TryParse(parts[1], out var index))
        {
            text.Append('[').Append(index).Append(']');
            node = node is JsonArray arr && index < arr.Count ? arr[index] : null;
            next = 2;
            if (node is JsonObject item && parts[0] is "vars" or "datarefs")
            {
                var label = Text(item["name"]) ?? Text(item["path"]);
                if (label is not null) text.Append(" \"").Append(label).Append('"');
            }
        }

        if (next < parts.Length) text.Append(' ').Append(string.Join('.', parts[next..]));
        return text.ToString();
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
