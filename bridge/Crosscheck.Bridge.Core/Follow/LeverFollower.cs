// Thrust levers on the follower (0.6.2, owner decision 2026-11-05): the follower writes the PF's lever position,
// clamped to the profile range, only when it differs by more than the noise band. A change of the own lever that
// is not the read-back of our own write (EchoTracker) is the pilot's hardware: remembered as "own hardware
// position" (used for the takeover checks), and on the follower the PF's value is written back.
// Not thread-safe: PhysicsSync calls it under its lock.

using Crosscheck.Bridge.Sync;

namespace Crosscheck.Bridge.Follow;

/// <summary>A thrust lever: its profile var and the range the owner confirmed (null: no range, never written).</summary>
public sealed record LeverSpec(string Name, double? Min, double? Max);

/// <summary>One frame's result: what to write, and which levers the pilot's hardware moved (with the new position).</summary>
public sealed record LeverStep(IReadOnlyList<(string Var, double Value)> Writes, IReadOnlyList<(string Var, double Hardware)> Moved);

public sealed class LeverFollower
{
    /// <summary>Hardware noise band (throttle-results.md, 2026-11-05: noise 0, band floor 0.005).</summary>
    public const double DefaultBand = 0.005;
    /// <summary>How long a written value is expected back (same as the sync engine's echo window).</summary>
    public const double EchoWindowMs = 1000;

    private sealed class State
    {
        public double? LastSeen;
        public double? Hardware;
        public double? LastWritten;
    }

    private readonly IReadOnlyList<LeverSpec> _levers;
    private readonly Dictionary<string, State> _state = new(StringComparer.Ordinal);
    private readonly EchoTracker _echo;
    private readonly double _band;

    public LeverFollower(IEnumerable<LeverSpec> levers, IClock clock, double band = DefaultBand)
    {
        _levers = levers.ToList();
        foreach (var l in _levers) _state[l.Name] = new State();
        _echo = new EchoTracker(clock);
        _band = band;
    }

    public IReadOnlyList<LeverSpec> Levers => _levers;

    /// <summary>The position the pilot's hardware last moved this lever to; null if never seen in this session.</summary>
    public double? Hardware(string name) => _state.TryGetValue(name, out var s) ? s.Hardware : null;

    /// <summary>The value clamped to the lever's range (unchanged without a range).</summary>
    public double Clamp(string name, double value)
    {
        var l = _levers.FirstOrDefault(x => x.Name == name);
        if (l is null) return value;
        if (l.Min is { } min && value < min) value = min;
        if (l.Max is { } max && value > max) value = max;
        return value;
    }

    /// <summary>A new session: nothing is known about the hardware.</summary>
    public void Reset()
    {
        foreach (var (name, s) in _state)
        {
            s.LastSeen = s.Hardware = s.LastWritten = null;
            _echo.Forget(name);
        }
    }

    /// <summary>
    /// One own sim frame. <paramref name="pf"/> is keyed by the lower-case var name (as on the wire). Writes only
    /// when <paramref name="write"/> and <paramref name="canWrite"/> allow it for that lever.
    /// </summary>
    public LeverStep Step(IReadOnlyDictionary<string, double> own, IReadOnlyDictionary<string, double> pf, bool write, Func<string, bool> canWrite)
    {
        List<(string, double)>? writes = null;
        List<(string, double)>? moved = null;
        foreach (var l in _levers)
        {
            if (!own.TryGetValue(l.Name, out var v)) continue;
            var s = _state[l.Name];
            if (s.LastSeen is { } last && Math.Abs(v - last) > _band && !_echo.TryConsume(l.Name, v))
            {
                s.Hardware = v;
                (moved ??= []).Add((l.Name, v));
            }
            s.LastSeen = v;
            if (!write || l.Min is null || l.Max is null || !canWrite(l.Name) || !pf.TryGetValue(l.Name.ToLowerInvariant(), out var pfValue)) continue;
            var target = Clamp(l.Name, pfValue);
            if (Math.Abs(v - target) <= _band) continue;
            // Our last write of this value is still on its way: do not send it again every frame.
            if (_echo.Pending(l.Name) > 0 && s.LastWritten is { } lw && Math.Abs(lw - target) <= _band) continue;
            _echo.Expect(l.Name, target, EchoWindowMs);
            s.LastWritten = target;
            (writes ??= []).Add((l.Name, target));
        }
        return new LeverStep(writes ?? [], moved ?? []);
    }
}
