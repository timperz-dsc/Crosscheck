using System.Text.RegularExpressions;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Sim;

public sealed class SafetyViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Hard limits on every write, enforced inside the sim adapters so nothing above them can
/// bypass it: only profile vars of a writable class, only L: var names, only values inside
/// the profile's set or range.
/// </summary>
public sealed partial class SimSafetyGuard
{
    private static readonly HashSet<SyncClass> Writable = [SyncClass.Verified, SyncClass.Analog, SyncClass.Owned, SyncClass.Delta, SyncClass.Pulse];
    private readonly Dictionary<string, VarEntry> _writable;
    private int _refused;

    public SimSafetyGuard(Profile profile)
    {
        _writable = profile.Vars.Where(v => Writable.Contains(v.Class)).ToDictionary(v => v.Name, StringComparer.Ordinal);
    }

    public int Refused => _refused;

    public bool IsWritable(string name) => _writable.ContainsKey(name);

    /// <summary>Whether a write would pass, without counting a refusal (for the UI's "Set both" options).</summary>
    public bool IsAllowedValue(string name, double value) => Reason(name, value) is null;

    /// <summary>Throws <see cref="SafetyViolationException"/> unless the write is allowed.</summary>
    public void CheckWrite(string name, double value)
    {
        var reason = Reason(name, value);
        if (reason is null) return;
        Interlocked.Increment(ref _refused);
        throw new SafetyViolationException($"write refused: {name} = {value}: {reason}");
    }

    private string? Reason(string name, double value)
    {
        if (!NamePattern().IsMatch(name)) return "not a plain L: var name";
        if (!_writable.TryGetValue(name, out var spec)) return "not a writable var in the profile";
        if (!double.IsFinite(value)) return "not a finite number";
        switch (spec.Class)
        {
            case SyncClass.Delta:
                return null; // encoder counters: Fenix only uses the change
            case SyncClass.Pulse when spec.Press == PressMode.Edge:
                return value >= 0 && value == Math.Floor(value) ? null : "an edge button counts in whole steps";
            case SyncClass.Pulse:
                return value is 0 or 1 ? null : "a pulse writes only 1 and 0";
        }
        if (spec.Values is { } values && !values.Any(v => Math.Abs(v - value) < 1e-9))
            return $"not one of the allowed values [{string.Join(", ", values)}]";
        if (spec.Range is [var min, var max] && (value < min - 1e-9 || value > max + 1e-9))
            return $"outside the allowed range [{min}, {max}]";
        return null;
    }

    [GeneratedRegex("^[A-Za-z0-9_]{1,128}$")]
    private static partial Regex NamePattern();
}
