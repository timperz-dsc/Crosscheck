// Ported from the CRO-17 follower spike (its Follower.Core/PlayoutBuffer.cs). Product defaults:
// buffer 100 ms, extrapolation capped at 600 ms (the spike's stall runs: 250 ms made the follower stop dead
// and jump after a 400 ms stall; 600 ms rode it out), blend 200 ms.

namespace Crosscheck.Bridge.Follow;

public sealed record PlayoutOptions
{
    /// <summary>How far behind the newest expected frame the follower plays.</summary>
    public double BufferMs { get; init; } = 100;
    /// <summary>Longest dead reckoning when frames are late; after that the follower holds.</summary>
    public double MaxExtrapolationMs { get; init; } = 600;
    /// <summary>After a late stretch, the jump back onto the interpolated path is spread over this time.</summary>
    public double BlendMs { get; init; } = 200;
    /// <summary>Window for the clock offset estimate (the fastest arrival in it counts).</summary>
    public double OffsetWindowMs { get; init; } = 2000;
    /// <summary>How fast the render clock may be pulled toward a new offset, ms per second.</summary>
    public double OffsetPullMsPerS { get; init; } = 5;
    /// <summary>How fast that pull may change, ms per second per second.</summary>
    public double OffsetRampMsPerS2 { get; init; } = 5;
}

public enum PlayoutStatus
{
    /// <summary>No frame yet.</summary>
    Empty,
    /// <summary>Render time is before the oldest frame: holding it (start of following).</summary>
    Waiting,
    Interpolated,
    /// <summary>Frames are late: dead reckoning from the newest one.</summary>
    Extrapolated,
    /// <summary>Frames are later than the extrapolation limit: holding the extrapolated state.</summary>
    Holding,
}

/// <param name="LateBy">How far render time is past the newest frame, seconds (0 when interpolating).</param>
public readonly record struct PlayoutResult(PhysicsFrame State, PlayoutStatus Status, double RenderTime, double LateBy);

/// <summary>
/// The follower's receive side. Frames carry the PF's clock; arrivals the follower's. The offset between
/// the clocks is the fastest arrival seen recently (so jitter does not move the render clock), followed
/// slowly. Render time = now - offset - buffer. Latest wins: a frame older than the newest is dropped.
/// </summary>
public sealed class PlayoutBuffer(PlayoutOptions options)
{
    private readonly List<PhysicsFrame> _frames = [];
    private readonly LinkedList<(double Arrival, double Offset)> _offsets = new();
    private double? _offset;
    private double _lastSampleNow = double.NaN;
    private double _offsetRate;
    private PlayoutStatus _lastStatus = PlayoutStatus.Empty;
    private PhysicsFrame? _lastOut;
    private PhysicsFrame? _prevOut;
    private Enu _blendPos;
    private Quat _blendAtt = Quat.Identity;
    private double _blendStart = double.NaN;

    public PlayoutOptions Options { get; } = options;
    public int Received { get; private set; }
    /// <summary>Frames that arrived after a newer one (latest wins).</summary>
    public int DroppedOld { get; private set; }
    public double? ClockOffset => _offset;
    public PhysicsFrame? Newest => _frames.Count > 0 ? _frames[^1] : null;
    /// <summary>How late the newest arrival was against the fastest one in the window, seconds (jitter).</summary>
    public double LastLateness { get; private set; }

    /// <summary>Adds a frame that arrived at <paramref name="arrivalTime"/> (follower clock). False if dropped as older than the newest.</summary>
    public bool Push(in PhysicsFrame frame, double arrivalTime)
    {
        Received++;
        var o = arrivalTime - frame.T;
        _offsets.AddLast((arrivalTime, o));
        while (_offsets.First is { } first && first.Value.Arrival < arrivalTime - Options.OffsetWindowMs / 1000) _offsets.RemoveFirst();
        _offset ??= o;
        var min = double.PositiveInfinity;
        foreach (var (_, x) in _offsets) min = Math.Min(min, x);
        LastLateness = o - min;
        if (_frames.Count > 0 && frame.T <= _frames[^1].T)
        {
            DroppedOld++;
            return false;
        }
        _frames.Add(frame);
        return true;
    }

    public PlayoutResult Sample(double now)
    {
        if (_frames.Count == 0 || _offset is null) return new PlayoutResult(default, PlayoutStatus.Empty, double.NaN, 0);
        UpdateOffset(now);
        var render = now - _offset.Value - Options.BufferMs / 1000;
        Prune(render);

        PhysicsFrame state;
        PlayoutStatus status;
        double lateBy = 0;
        if (render <= _frames[0].T)
        {
            state = _frames[0] with { T = render };
            status = PlayoutStatus.Waiting;
        }
        else if (render <= _frames[^1].T)
        {
            var i = Interpolator.IndexAtOrBefore(_frames, render);
            if (i >= _frames.Count - 1) i = _frames.Count - 2;
            state = Interpolator.Between(_frames[i], _frames[i + 1], render);
            status = PlayoutStatus.Interpolated;
        }
        else
        {
            var newest = _frames[^1];
            PhysicsFrame? prev = _frames.Count > 1 ? _frames[^2] : null;
            lateBy = render - newest.T;
            var cap = Options.MaxExtrapolationMs / 1000;
            status = lateBy <= cap ? PlayoutStatus.Extrapolated : PlayoutStatus.Holding;
            state = Interpolator.Extrapolate(newest, prev, newest.T + Math.Min(lateBy, cap)) with { T = render };
        }

        state = Blend(now, render, state, status);
        _lastStatus = status;
        return new PlayoutResult(state, status, render, lateBy);
    }

    private void UpdateOffset(double now)
    {
        var target = double.PositiveInfinity;
        foreach (var (_, o) in _offsets) target = Math.Min(target, o);
        if (double.IsInfinity(target)) return;
        if (double.IsNaN(_lastSampleNow))
        {
            _lastSampleNow = now;
            _offset = target;
            return;
        }
        var dt = Math.Max(0, now - _lastSampleNow);
        _lastSampleNow = now;
        var error = target - _offset!.Value;
        var maxRate = Options.OffsetPullMsPerS / 1000;
        var wanted = Math.Abs(error) < 0.001 ? 0 : Math.Clamp(error * 2, -maxRate, maxRate);
        var ramp = Options.OffsetRampMsPerS2 / 1000 * dt;
        _offsetRate += Math.Clamp(wanted - _offsetRate, -ramp, ramp);
        _offset += _offsetRate * dt;
    }

    private PhysicsFrame Blend(double now, double render, PhysicsFrame state, PlayoutStatus status)
    {
        var wasLate = _lastStatus is PlayoutStatus.Extrapolated or PlayoutStatus.Holding;
        if (status == PlayoutStatus.Interpolated && wasLate && Options.BlendMs > 0 && _lastOut is { } last)
        {
            var carriedOn = Interpolator.Extrapolate(last, _prevOut, render);
            _blendPos = Geo.Offset(state, carriedOn);
            _blendAtt = (Geo.Attitude(carriedOn) * Geo.Attitude(state).Conjugate).Normalized();
            _blendStart = now;
        }
        var result = state;
        if (!double.IsNaN(_blendStart))
        {
            var u = (now - _blendStart) / (Options.BlendMs / 1000);
            if (u >= 1) _blendStart = double.NaN;
            else
            {
                // Smoothstep: the correction starts and ends without a step in speed.
                var k = 1 - u * u * (3 - 2 * u);
                var (lat, lon, alt) = Geo.Move(state, _blendPos * k);
                var q = (_blendAtt.Pow(k) * Geo.Attitude(state)).Normalized();
                result = Geo.WithAttitude(state with { Lat = lat, Lon = lon, AltFt = alt }, q);
            }
        }
        _prevOut = _lastOut;
        _lastOut = result;
        return result;
    }

    private void Prune(double render)
    {
        // Keep one second behind render time, and always the last three frames.
        var keepFrom = render - 1.0;
        var drop = 0;
        while (drop < _frames.Count - 3 && _frames[drop + 1].T < keepFrom) drop++;
        if (drop > 0) _frames.RemoveRange(0, drop);
    }
}
