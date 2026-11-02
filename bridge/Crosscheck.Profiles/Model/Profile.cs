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
    /// <summary>Default: write 1, then 0 after pulse_ms.</summary>
    Momentary,
    /// <summary>
    /// Fenix push buttons (CRO-29/30): the var counts +1 on press and +1 on release (odd = down).
    /// Each down/up edge is mirrored on its own; the receiver writes its own value + 1.
    /// </summary>
    Edge,
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
    IReadOnlyDictionary<string, string>? ValueLabels = null,
    double? Rest = null,
    AlarmSpec? Alarm = null);

/// <summary>
/// observe only: this indicator shows a pilot input that Crosscheck cannot sync (yet). A difference
/// between the sims is red (input tier) after 2 s, never quiet, with this text. Never written.
/// </summary>
/// <param name="As">Report the difference under this control (several indicators of one knob share one line).</param>
public sealed record AlarmSpec(string Text, string? As = null);

public sealed record DatarefEntry(
    string Path,
    SyncClass Class,
    string Group,
    double? PollHz = null,
    string? Note = null,
    string? Label = null,
    string? Unit = null,
    double? DriftWarn = null,
    double? DriftStep = null)
{
    /// <summary>
    /// owned: a drift change smaller than this is sent at most every 10 s. Default: half of
    /// drift_warn (50 kg for fuel with drift_warn 100); null without both.
    /// </summary>
    public double? EffectiveDriftStep => DriftStep ?? (DriftWarn is { } w ? w / 2 : null);
}

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
