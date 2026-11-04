// Ported from the CRO-17 follower spike (its Follower.Core/Geo.cs). Same maths, bridge namespace.

namespace Crosscheck.Bridge.Follow;

/// <summary>East, north, up in metres.</summary>
public readonly record struct Enu(double E, double N, double U)
{
    public static Enu operator +(Enu a, Enu b) => new(a.E + b.E, a.N + b.N, a.U + b.U);
    public static Enu operator -(Enu a, Enu b) => new(a.E - b.E, a.N - b.N, a.U - b.U);
    public static Enu operator *(Enu a, double k) => new(a.E * k, a.N * k, a.U * k);
    public static Enu operator *(double k, Enu a) => a * k;
    public double Length => Math.Sqrt(E * E + N * N + U * U);
    public double Horizontal => Math.Sqrt(E * E + N * N);
}

public static class Geo
{
    public const double FtToM = 0.3048;
    public const double KtToMs = 1852.0 / 3600.0;
    private const double A = 6378137.0;           // WGS84 semi-major axis
    private const double E2 = 6.69437999014e-3;   // WGS84 first eccentricity squared
    private const double DegToRad = Math.PI / 180;

    /// <summary>Meridian and prime-vertical radius of curvature at a latitude (metres).</summary>
    public static (double M, double N) Radii(double latDeg)
    {
        var s = Math.Sin(latDeg * DegToRad);
        var d = 1 - E2 * s * s;
        return (A * (1 - E2) / (d * Math.Sqrt(d)), A / Math.Sqrt(d));
    }

    /// <summary>Where lat/lon/alt lies relative to <paramref name="from"/>, in metres. Exact inverse of <see cref="Move"/>.</summary>
    public static Enu Offset(in PhysicsFrame from, double lat, double lon, double altFt)
    {
        var (m, n) = Radii(from.Lat);
        var h = from.AltFt * FtToM;
        var dLon = Wrap180(lon - from.Lon);
        return new Enu(
            dLon * DegToRad * (n + h) * Math.Cos(from.Lat * DegToRad),
            (lat - from.Lat) * DegToRad * (m + h),
            (altFt - from.AltFt) * FtToM);
    }

    public static Enu Offset(in PhysicsFrame from, in PhysicsFrame to) => Offset(from, to.Lat, to.Lon, to.AltFt);

    /// <summary>The point <paramref name="d"/> metres away from <paramref name="from"/>.</summary>
    public static (double Lat, double Lon, double AltFt) Move(in PhysicsFrame from, Enu d)
    {
        var (m, n) = Radii(from.Lat);
        var h = from.AltFt * FtToM;
        var lat = from.Lat + d.N / (m + h) / DegToRad;
        var lon = Wrap180(from.Lon + d.E / ((n + h) * Math.Cos(from.Lat * DegToRad)) / DegToRad);
        return (lat, lon, from.AltFt + d.U / FtToM);
    }

    /// <summary>World velocity of a frame in metres per second (MSFS: X east, Y up, Z north).</summary>
    public static Enu Velocity(in PhysicsFrame f) => new(f.VelWorldX * FtToM, f.VelWorldZ * FtToM, f.VelWorldY * FtToM);

    public static double Wrap180(double deg)
    {
        deg %= 360;
        if (deg > 180) deg -= 360;
        else if (deg <= -180) deg += 360;
        return deg;
    }

    public static double Wrap360(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    /// <summary>Signed smallest difference b - a, in (-180, 180].</summary>
    public static double AngleDiff(double a, double b) => Wrap180(b - a);

    public static Quat Attitude(in PhysicsFrame f) => Quat.FromEuler(f.HeadingDeg, f.PitchDeg, f.BankDeg);

    public static PhysicsFrame WithAttitude(in PhysicsFrame f, Quat q)
    {
        var (h, p, b) = q.ToEuler();
        return f with { HeadingDeg = h, PitchDeg = p, BankDeg = b };
    }

    /// <summary>Distance between two frames' positions in metres.</summary>
    public static double Distance(in PhysicsFrame a, in PhysicsFrame b) => Offset(a, b).Length;
}

/// <summary>A unit quaternion in double precision. Euler order: heading (Z), then pitch (Y), then bank (X).</summary>
public readonly record struct Quat(double W, double X, double Y, double Z)
{
    private const double DegToRad = Math.PI / 180;
    public static readonly Quat Identity = new(1, 0, 0, 0);

    public static Quat FromEuler(double headingDeg, double pitchDeg, double bankDeg)
    {
        double cy = Math.Cos(headingDeg * DegToRad / 2), sy = Math.Sin(headingDeg * DegToRad / 2);
        double cp = Math.Cos(pitchDeg * DegToRad / 2), sp = Math.Sin(pitchDeg * DegToRad / 2);
        double cr = Math.Cos(bankDeg * DegToRad / 2), sr = Math.Sin(bankDeg * DegToRad / 2);
        return new Quat(
            cr * cp * cy + sr * sp * sy,
            sr * cp * cy - cr * sp * sy,
            cr * sp * cy + sr * cp * sy,
            cr * cp * sy - sr * sp * cy);
    }

    /// <summary>(heading in [0, 360), pitch in [-90, 90], bank in (-180, 180]).</summary>
    public (double HeadingDeg, double PitchDeg, double BankDeg) ToEuler()
    {
        var bank = Math.Atan2(2 * (W * X + Y * Z), 1 - 2 * (X * X + Y * Y));
        var pitch = Math.Asin(Math.Clamp(2 * (W * Y - Z * X), -1, 1));
        var heading = Math.Atan2(2 * (W * Z + X * Y), 1 - 2 * (Y * Y + Z * Z));
        return (Geo.Wrap360(heading / DegToRad), pitch / DegToRad, Geo.Wrap180(bank / DegToRad));
    }

    public static Quat operator *(Quat a, Quat b) => new(
        a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z,
        a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
        a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X,
        a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    public Quat Conjugate => new(W, -X, -Y, -Z);

    public double Dot(Quat o) => W * o.W + X * o.X + Y * o.Y + Z * o.Z;

    public Quat Normalized()
    {
        var n = Math.Sqrt(W * W + X * X + Y * Y + Z * Z);
        return n < 1e-15 ? Identity : new Quat(W / n, X / n, Y / n, Z / n);
    }

    /// <summary>Angle between the two attitudes in degrees (0..180).</summary>
    public double AngleTo(Quat o) => 2 * Math.Acos(Math.Min(1, Math.Abs(Dot(o)))) / DegToRad;

    /// <summary>Rotation vector (axis times angle, radians) of this rotation, shortest way.</summary>
    public (double X, double Y, double Z) Log()
    {
        var q = W < 0 ? new Quat(-W, -X, -Y, -Z) : this;
        var s = Math.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z);
        if (s < 1e-12) return (2 * q.X, 2 * q.Y, 2 * q.Z);
        var angle = 2 * Math.Atan2(s, q.W);
        return (q.X / s * angle, q.Y / s * angle, q.Z / s * angle);
    }

    public static Quat Exp(double x, double y, double z)
    {
        var angle = Math.Sqrt(x * x + y * y + z * z);
        if (angle < 1e-12) return new Quat(1, x / 2, y / 2, z / 2).Normalized();
        var s = Math.Sin(angle / 2) / angle;
        return new Quat(Math.Cos(angle / 2), x * s, y * s, z * s);
    }

    /// <summary>This rotation scaled by <paramref name="k"/> (k = 0.5: half the rotation).</summary>
    public Quat Pow(double k)
    {
        var (x, y, z) = Log();
        return Exp(x * k, y * k, z * k);
    }

    public static Quat Slerp(Quat a, Quat b, double t)
    {
        if (a.Dot(b) < 0) b = new Quat(-b.W, -b.X, -b.Y, -b.Z);
        return ((b * a.Conjugate).Pow(t) * a).Normalized();
    }
}
