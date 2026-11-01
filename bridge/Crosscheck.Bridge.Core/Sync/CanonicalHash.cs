using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Crosscheck.Bridge.Sync;

/// <summary>
/// Group hash in the canonical form of docs/protocol.md §7, so both bridges get the same
/// hash for the same values: sorted "NAME=VALUE\n" lines, SHA-256, first 16 hex characters.
/// Must stay identical to canonicalHash in server/test/fake/fakeSim.ts
/// (shared vectors: protocol/test-vectors/canonical-hash.json).
/// </summary>
public static class CanonicalHash
{
    public static string Compute(IEnumerable<(string Var, object? Value)> values)
    {
        var sb = new StringBuilder();
        foreach (var (name, value) in values.OrderBy(v => v.Var, StringComparer.Ordinal))
            sb.Append(name).Append('=').Append(FormatValue(value)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    /// <summary>Integers as is; other numbers with up to 6 decimals, trailing zeros removed; -0 as 0; strings as JSON strings; null as null.</summary>
    public static string FormatValue(object? value) => value switch
    {
        null => "null",
        string s => JsonString(s),
        double d => FormatNumber(d),
        float f => FormatNumber(f),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException($"unsupported value type {value.GetType().Name}"),
    };

    private static string FormatNumber(double v)
    {
        if (v == 0) return "0"; // also -0
        if (v == Math.Floor(v) && Math.Abs(v) < 9e15) return ((long)v).ToString(CultureInfo.InvariantCulture);
        var s = v.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return s == "-0" ? "0" : s;
    }

    /// <summary>Same output as JavaScript's JSON.stringify for a string.</summary>
    private static string JsonString(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
