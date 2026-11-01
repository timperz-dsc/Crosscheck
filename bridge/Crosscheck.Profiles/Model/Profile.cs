namespace Crosscheck.Profiles.Model;

public enum SyncClass
{
    Verified,
    Delta,
    Pulse,
    Analog,
    Stream,
    Owned,
    Observe,
    Ignore,
}

public enum PressMode
{
    Edge,
    Counter,
}

public enum VerifyExpect
{
    Same,
    Parity,
}

public sealed record MatchInfo(
    IReadOnlyList<string>? Titles = null,
    IReadOnlyList<string>? Packages = null);

public sealed record AircraftInfo(
    string Id,
    string Name,
    MatchInfo Match,
    IReadOnlyList<string> Builds);

/// <summary>Where the effect of a write is checked. Exactly one of Lvar / Mcdu / Graphql is set.</summary>
public sealed record VerifySpec(
    int SettleMs,
    string? Lvar = null,
    int? Mcdu = null,
    string? Graphql = null,
    VerifyExpect Expect = VerifyExpect.Same,
    double? Scale = null,
    string? Contains = null);

public sealed record VarEntry(
    string Name,
    SyncClass Class,
    string Group,
    IReadOnlyList<double>? Values = null,
    IReadOnlyList<double>? Range = null,
    VerifySpec? Verify = null,
    int? PulseMs = null,
    PressMode? Press = null,
    string? Display = null,
    double? RateHz = null,
    string? Note = null,
    string? Label = null,
    IReadOnlyDictionary<string, string>? ValueLabels = null);

public sealed record DatarefEntry(
    string Path,
    SyncClass Class,
    string Group,
    double? PollHz = null,
    string? Note = null,
    string? Label = null,
    string? Unit = null,
    double? DriftWarn = null);

/// <summary>The sidestick takeover pushbutton of each seat (left = captain, right = first officer).</summary>
public sealed record TakeoverRole(string Captain, string Fo);

/// <summary>Vars with a special meaning for Crosscheck itself, beyond being synced.</summary>
public sealed record ProfileRoles(TakeoverRole? Takeover = null);

public sealed record Profile(
    int Format,
    AircraftInfo Aircraft,
    IReadOnlyList<string> Groups,
    IReadOnlyList<VarEntry> Vars,
    IReadOnlyList<string>? Exclude = null,
    IReadOnlyList<DatarefEntry>? Datarefs = null,
    ProfileRoles? Roles = null,
    IReadOnlyDictionary<string, string>? GroupLabels = null);
