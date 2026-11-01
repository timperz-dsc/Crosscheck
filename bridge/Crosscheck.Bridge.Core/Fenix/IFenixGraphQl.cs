using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Crosscheck.Bridge.Fenix;

/// <summary>
/// Read-only access to the Fenix EFB backend (GraphQL on localhost:8083). Nothing is ever
/// written through it. Values are double, string or null (unknown: Fenix answers "" for
/// names it does not know).
/// </summary>
public interface IFenixGraphQl : IAsyncDisposable
{
    /// <summary>Starts polling these datarefs (HTTP) and the MCDU subscription.</summary>
    Task StartAsync(IReadOnlyCollection<string> pollPaths, CancellationToken ct);

    /// <summary>Last polled value, or null.</summary>
    object? Cached(string path);

    /// <summary>A fresh read over HTTP (~70 ms). null if unknown or Fenix is not reachable.</summary>
    Task<object?> GetAsync(string path, CancellationToken ct = default);

    /// <summary>The Fenix app version ("GetSoftwareVersion"), or null if not readable.</summary>
    Task<string?> SoftwareVersionAsync(CancellationToken ct = default);

    /// <summary>Latest screen of CDU 1 or 2, or null if not known.</summary>
    McduScreen? Mcdu(int cdu);

    event Action<int>? McduChanged;
}

/// <summary>One MCDU screen as plain text: 14 rows (title, 12 lines, scratchpad).</summary>
public sealed record McduScreen(IReadOnlyList<string> Rows)
{
    public string Text => string.Join("\n", Rows);

    /// <summary>Short hash of the plain text, used in snapshots (aircraft.mcdu{n}.display).</summary>
    public string Hash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Text)))[..16].ToLowerInvariant();

    /// <summary>
    /// Parses the XML-ish screen Fenix serves (see spike/McduReader/PROTOCOL.md). Lowercase
    /// letters are style modifiers and are removed; displayed text is always uppercase.
    /// </summary>
    public static McduScreen? Parse(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        try
        {
            var root = XElement.Parse(xml);
            var rows = root.Elements().Select(e => StripModifiers(e.Value)).ToList();
            return rows.Count == 0 ? null : new McduScreen(rows);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    public static McduScreen FromText(string text) => new([StripModifiers(text)]);

    private static string StripModifiers(string s) => new(s.Where(c => !char.IsAsciiLetterLower(c)).ToArray());
}

internal static class GraphQlValue
{
    /// <summary>Raw JSON value → double, string or null. "" means unknown.</summary>
    public static object? From(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Number => e.GetDouble(),
        JsonValueKind.True => 1.0,
        JsonValueKind.False => 0.0,
        JsonValueKind.String => e.GetString() is { Length: > 0 } s
            ? (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : s)
            : null,
        _ => null,
    };
}
