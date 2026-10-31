using Crosscheck.Profiles.Model;

namespace Crosscheck.Profiles;

/// <summary>Counts of entries (vars and datarefs) per class and per group.</summary>
public sealed record ProfileSummary(
    int VarCount,
    int DatarefCount,
    IReadOnlyDictionary<SyncClass, int> PerClass,
    IReadOnlyDictionary<string, int> PerGroup)
{
    public static ProfileSummary Of(Profile profile)
    {
        var datarefs = profile.Datarefs ?? [];
        var classes = profile.Vars.Select(v => v.Class).Concat(datarefs.Select(d => d.Class));
        var groups = profile.Vars.Select(v => v.Group).Concat(datarefs.Select(d => d.Group));

        return new ProfileSummary(
            profile.Vars.Count,
            datarefs.Count,
            Enum.GetValues<SyncClass>().ToDictionary(c => c, c => classes.Count(x => x == c)),
            profile.Groups.ToDictionary(g => g, g => groups.Count(x => x == g)));
    }
}
