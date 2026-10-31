using System.Text.RegularExpressions;

namespace Crosscheck.Profiles;

/// <summary>Glob matching (* any characters, ? one character, case-sensitive) for the profile's exclude patterns.</summary>
public sealed class ExcludeFilter
{
    private readonly List<(string Pattern, Regex Regex)> _patterns;

    public ExcludeFilter(IEnumerable<string>? patterns)
    {
        _patterns = (patterns ?? [])
            .Select(p => (p, new Regex(
                "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.CultureInvariant | RegexOptions.Singleline)))
            .ToList();
    }

    public bool IsExcluded(string name) => Match(name) is not null;

    /// <summary>The first pattern that matches, or null.</summary>
    public string? Match(string name)
    {
        foreach (var (pattern, regex) in _patterns)
            if (regex.IsMatch(name)) return pattern;
        return null;
    }
}
