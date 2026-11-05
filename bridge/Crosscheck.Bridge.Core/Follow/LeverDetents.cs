// Thrust lever detent names, from the profile note ("detents MAX REV 0, REV IDLE 1, IDLE 2, CL 3, FLX/MCT 4, TOGA 5",
// measured by the read-only throttle probe, owner confirmed 2026-11-05). Used for the pilots' lines only.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Crosscheck.Bridge.Follow;

public sealed record Detent(string Name, double Value);

public static partial class LeverDetents
{
    /// <summary>A value within this of a detent is called by its name.</summary>
    public const double Reach = 0.1;

    [GeneratedRegex(@"detents\s+([^""]+)", RegexOptions.CultureInvariant)]
    private static partial Regex List();

    [GeneratedRegex(@"^(.+?)\s+(-?\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant)]
    private static partial Regex Item();

    /// <summary>The detents in a profile note; empty if it lists none.</summary>
    public static IReadOnlyList<Detent> Parse(string? note)
    {
        if (note is null || List().Match(note) is not { Success: true } m) return [];
        var list = new List<Detent>();
        foreach (var part in m.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            if (Item().Match(part) is { Success: true } i)
                list.Add(new Detent(i.Groups[1].Value, double.Parse(i.Groups[2].Value, CultureInfo.InvariantCulture)));
        return list;
    }

    /// <summary>"CL" for 3.02; the number itself ("3.4") when no detent is within <see cref="Reach"/>.</summary>
    public static string NameOf(IReadOnlyList<Detent> detents, double value)
    {
        var best = detents.OrderBy(d => Math.Abs(d.Value - value)).FirstOrDefault();
        return best is not null && Math.Abs(best.Value - value) <= Reach
            ? best.Name
            : (Math.Round(value * 100) / 100).ToString("0.##", CultureInfo.InvariantCulture);
    }
}
