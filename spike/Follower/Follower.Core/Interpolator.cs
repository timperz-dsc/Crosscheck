namespace Crosscheck.Follower;

/// <summary>How the position curve between two frames gets its tangents.</summary>
public enum Tangents
{
    /// <summary>From the frames' world velocities (smooth and exact for steady motion).</summary>
    Velocity,
    /// <summary>From neighbouring positions (Catmull-Rom); for when world velocities look wrong.</summary>
    FiniteDifference,
}

public static class Interpolator
{
    /// <summary>
    /// The state at <paramref name="t"/> between <paramref name="a"/> and <paramref name="b"/>:
    /// position on a cubic Hermite curve in metres around <paramref name="a"/>, attitude by slerp,
    /// everything else linear. <paramref name="before"/>/<paramref name="after"/> are only used for
    /// <see cref="Tangents.FiniteDifference"/>.
    /// </summary>
    public static Frame Between(in Frame a, in Frame b, double t, Tangents mode = Tangents.Velocity, Frame? before = null, Frame? after = null)
    {
        var h = b.T - a.T;
        if (h <= 1e-9) return b with { T = t };
        var s = Math.Clamp((t - a.T) / h, 0, 1);
        var p1 = Geo.Offset(a, b);
        Enu m0, m1;
        if (mode == Tangents.Velocity)
        {
            m0 = Geo.Velocity(a) * h;
            m1 = Geo.Velocity(b) * h;
        }
        else
        {
            // Catmull-Rom with uneven spacing, scaled to this segment.
            m0 = before is { } pb && a.T - pb.T > 1e-9 ? (p1 - Geo.Offset(a, pb)) * (h / (b.T - pb.T)) : p1;
            m1 = after is { } pa && pa.T - b.T > 1e-9 ? Geo.Offset(a, pa) * (h / (pa.T - a.T)) : p1;
        }
        double s2 = s * s, s3 = s2 * s;
        var p = m0 * (s3 - 2 * s2 + s) + p1 * (-2 * s3 + 3 * s2) + m1 * (s3 - s2);
        var (lat, lon, alt) = Geo.Move(a, p);
        var q = Quat.Slerp(Geo.Attitude(a), Geo.Attitude(b), s);
        var f = Lerp(a, b, s) with { T = t, Lat = lat, Lon = lon, AltFt = alt, OnGround = s < 0.5 ? a.OnGround : b.OnGround };
        return Geo.WithAttitude(f, q);
    }

    /// <summary>Largest acceleration dead reckoning will assume, m/s² (keeps noise in two close frames from running away).</summary>
    public const double MaxExtrapolationAccel = 15;

    /// <summary>
    /// Dead reckoning from <paramref name="last"/> to <paramref name="t"/>. With a previous frame:
    /// position along its world velocity plus the acceleration seen between the two frames
    /// (so a turn keeps turning), attitude turning at the rate seen between them. Without one:
    /// straight on, attitude held. Other fields stay as in <paramref name="last"/>. The caller
    /// caps how far ahead this goes.
    /// </summary>
    public static Frame Extrapolate(in Frame last, Frame? prev, double t)
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

    private static Frame Lerp(in Frame a, in Frame b, double s)
    {
        double L(double x, double y) => x + (y - x) * s;
        return a with
        {
            VelWorldX = L(a.VelWorldX, b.VelWorldX),
            VelWorldY = L(a.VelWorldY, b.VelWorldY),
            VelWorldZ = L(a.VelWorldZ, b.VelWorldZ),
            VelBodyX = L(a.VelBodyX, b.VelBodyX),
            VelBodyY = L(a.VelBodyY, b.VelBodyY),
            VelBodyZ = L(a.VelBodyZ, b.VelBodyZ),
            RotBodyX = L(a.RotBodyX, b.RotBodyX),
            RotBodyY = L(a.RotBodyY, b.RotBodyY),
            RotBodyZ = L(a.RotBodyZ, b.RotBodyZ),
            GroundSpeedKt = L(a.GroundSpeedKt, b.GroundSpeedKt),
            AglFt = L(a.AglFt, b.AglFt),
            Gear = L(a.Gear, b.Gear),
            FlapsIndex = s < 0.5 ? a.FlapsIndex : b.FlapsIndex,
            YokeX = L(a.YokeX, b.YokeX),
            YokeY = L(a.YokeY, b.YokeY),
            RudderPedal = L(a.RudderPedal, b.RudderPedal),
            ElevatorDeg = L(a.ElevatorDeg, b.ElevatorDeg),
            AileronDeg = L(a.AileronDeg, b.AileronDeg),
            RudderDeg = L(a.RudderDeg, b.RudderDeg),
            SpoilersLeft = L(a.SpoilersLeft, b.SpoilersLeft),
            FenixStickCaptPitch = L(a.FenixStickCaptPitch, b.FenixStickCaptPitch),
            FenixStickCaptBank = L(a.FenixStickCaptBank, b.FenixStickCaptBank),
            FenixStickFoPitch = L(a.FenixStickFoPitch, b.FenixStickFoPitch),
            FenixStickFoBank = L(a.FenixStickFoBank, b.FenixStickFoBank),
            GForce = L(a.GForce, b.GForce),
        };
    }

    /// <summary>The recording's state at <paramref name="t"/> (truth for the metrics). Clamped to the ends.</summary>
    public static Frame Sample(IReadOnlyList<Frame> frames, double t, Tangents mode = Tangents.Velocity)
    {
        if (frames.Count == 0) throw new ArgumentException("no frames");
        if (t <= frames[0].T) return frames[0] with { T = t };
        if (t >= frames[^1].T) return frames[^1] with { T = t };
        var i = IndexAtOrBefore(frames, t);
        Frame? before = i > 0 ? frames[i - 1] : null;
        Frame? after = i + 2 < frames.Count ? frames[i + 2] : null;
        return Between(frames[i], frames[i + 1], t, mode, before, after);
    }

    /// <summary>Index of the last frame with T ≤ t (0 if none).</summary>
    public static int IndexAtOrBefore(IReadOnlyList<Frame> frames, double t)
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

    /// <summary>Window for <see cref="VelocityMismatch"/>, seconds.</summary>
    public const double MismatchWindowS = 0.5;

    /// <summary>
    /// Checks that the recorded world velocities match the position changes (sign and axis
    /// conventions). Over windows of <see cref="MismatchWindowS"/>: the displacement against the
    /// velocity integrated over the window. A window is long enough that a few ms of noise in
    /// the frame times (stopwatch) do not count, and the integral follows turns. Returns the
    /// median relative mismatch over moving windows; NaN if it never moves.
    /// </summary>
    public static double VelocityMismatch(IReadOnlyList<Frame> frames, double windowS = MismatchWindowS)
    {
        var errs = new List<double>();
        var i = 0;
        while (i < frames.Count - 1)
        {
            var j = i;
            var integral = new Enu(0, 0, 0);
            while (j + 1 < frames.Count && frames[j].T - frames[i].T < windowS)
            {
                var dt = frames[j + 1].T - frames[j].T;
                if (dt > 0) integral += (Geo.Velocity(frames[j]) + Geo.Velocity(frames[j + 1])) * (0.5 * dt);
                j++;
            }
            var span = frames[j].T - frames[i].T;
            if (span < windowS * 0.5) break; // the short rest at the end
            var disp = Geo.Offset(frames[i], frames[j]);
            var h = new Enu(disp.E, disp.N, 0);
            var hv = new Enu(integral.E, integral.N, 0);
            if (h.Length / span >= 2) errs.Add((h - hv).Length / h.Length); // below ~4 kt the noise dominates
            i = j;
        }
        if (errs.Count == 0) return double.NaN;
        errs.Sort();
        return errs[errs.Count / 2];
    }
}
