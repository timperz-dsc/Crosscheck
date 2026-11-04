// Ported from the CRO-17 follower spike (its Follower.Core/Interpolator.cs): velocity tangents only.

namespace Crosscheck.Bridge.Follow;

public static class Interpolator
{
    /// <summary>
    /// The state at <paramref name="t"/> between <paramref name="a"/> and <paramref name="b"/>: position on a
    /// cubic Hermite curve in metres around <paramref name="a"/> with the frames' world velocities as tangents,
    /// attitude by slerp, everything else linear.
    /// </summary>
    public static PhysicsFrame Between(in PhysicsFrame a, in PhysicsFrame b, double t)
    {
        var h = b.T - a.T;
        if (h <= 1e-9) return b with { T = t };
        var s = Math.Clamp((t - a.T) / h, 0, 1);
        var p1 = Geo.Offset(a, b);
        var m0 = Geo.Velocity(a) * h;
        var m1 = Geo.Velocity(b) * h;
        double s2 = s * s, s3 = s2 * s;
        var p = m0 * (s3 - 2 * s2 + s) + p1 * (-2 * s3 + 3 * s2) + m1 * (s3 - s2);
        var (lat, lon, alt) = Geo.Move(a, p);
        var q = Quat.Slerp(Geo.Attitude(a), Geo.Attitude(b), s);
        var f = Lerp(a, b, s) with { T = t, Lat = lat, Lon = lon, AltFt = alt, OnGround = s < 0.5 ? a.OnGround : b.OnGround };
        return Geo.WithAttitude(f, q);
    }

    /// <summary>Largest acceleration dead reckoning will assume, m/s².</summary>
    public const double MaxExtrapolationAccel = 15;

    /// <summary>
    /// Dead reckoning from <paramref name="last"/> to <paramref name="t"/>: along the world velocity plus the
    /// acceleration seen between the two frames (a turn keeps turning), attitude turning at the observed rate.
    /// The caller caps how far ahead this goes.
    /// </summary>
    public static PhysicsFrame Extrapolate(in PhysicsFrame last, PhysicsFrame? prev, double t)
    {
        var dt = t - last.T;
        if (dt <= 0) return last with { T = t };
        var v = Geo.Velocity(last);
        var a = new Enu(0, 0, 0);
        var q = Geo.Attitude(last);
        if (prev is { } p && last.T - p.T > 1e-6)
        {
            var h = last.T - p.T;
            a = (v - Geo.Velocity(p)) * (1 / h);
            if (a.Length > MaxExtrapolationAccel) a = a * (MaxExtrapolationAccel / a.Length);
            var step = Geo.Attitude(last) * Geo.Attitude(p).Conjugate;
            q = (step.Pow(dt / h) * q).Normalized();
        }
        var (lat, lon, alt) = Geo.Move(last, v * dt + a * (0.5 * dt * dt));
        var vNow = v + a * dt;
        var f = last with
        {
            T = t, Lat = lat, Lon = lon, AltFt = alt,
            VelWorldX = vNow.E / Geo.FtToM, VelWorldY = vNow.U / Geo.FtToM, VelWorldZ = vNow.N / Geo.FtToM,
        };
        return Geo.WithAttitude(f, q);
    }

    private static PhysicsFrame Lerp(in PhysicsFrame a, in PhysicsFrame b, double s)
    {
        double L(double x, double y) => x + (y - x) * s;
        return a with
        {
            VelWorldX = L(a.VelWorldX, b.VelWorldX),
            VelWorldY = L(a.VelWorldY, b.VelWorldY),
            VelWorldZ = L(a.VelWorldZ, b.VelWorldZ),
            VelBodyY = L(a.VelBodyY, b.VelBodyY),
            VelBodyZ = L(a.VelBodyZ, b.VelBodyZ),
            GForce = L(a.GForce, b.GForce),
            GroundSpeedKt = L(a.GroundSpeedKt, b.GroundSpeedKt),
            Elevator = L(a.Elevator, b.Elevator),
            Aileron = L(a.Aileron, b.Aileron),
            Rudder = L(a.Rudder, b.Rudder),
        };
    }

    /// <summary>A list of frames' state at <paramref name="t"/> (tests: the truth). Clamped to the ends.</summary>
    public static PhysicsFrame Sample(IReadOnlyList<PhysicsFrame> frames, double t)
    {
        if (frames.Count == 0) throw new ArgumentException("no frames");
        if (t <= frames[0].T) return frames[0] with { T = t };
        if (t >= frames[^1].T) return frames[^1] with { T = t };
        var i = IndexAtOrBefore(frames, t);
        return Between(frames[i], frames[i + 1], t);
    }

    /// <summary>Index of the last frame with T ≤ t (0 if none).</summary>
    public static int IndexAtOrBefore(IReadOnlyList<PhysicsFrame> frames, double t)
    {
        int lo = 0, hi = frames.Count - 1;
        if (t < frames[0].T) return 0;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (frames[mid].T <= t) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }
}
