namespace Crosscheck.Follower.Tests;

static class Repo
{
    /// <summary>The repository root (the folder that holds spike/Follower/Follower.sln).</summary>
    public static readonly string Root = Find();

    public static string Follower => Path.Combine(Root, "spike", "Follower");

    private static string Find()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "spike", "Follower", "Follower.sln"))) return d.FullName;
        throw new InvalidOperationException("repository root not found");
    }
}

static class Make
{
    /// <summary>A frame moving at a constant velocity (m/s, east/north/up) from a fixed point, at time t.</summary>
    public static Frame Moving(double t, double ve, double vn, double vu = 0, double headingDeg = 90)
    {
        var start = new Frame { Lat = 59.65, Lon = 17.93, AltFt = 1000 };
        var (lat, lon, alt) = Geo.Move(start, new Enu(ve * t, vn * t, vu * t));
        return start with
        {
            T = t, Lat = lat, Lon = lon, AltFt = alt, HeadingDeg = headingDeg,
            VelWorldX = ve / Geo.FtToM, VelWorldY = vu / Geo.FtToM, VelWorldZ = vn / Geo.FtToM,
            GroundSpeedKt = Math.Sqrt(ve * ve + vn * vn) / Geo.KtToMs,
        };
    }

    public static IReadOnlyList<Frame> Track(double seconds, double fps, double ve, double vn) =>
        Enumerable.Range(0, (int)(seconds * fps) + 1).Select(i => Moving(i / fps, ve, vn)).ToArray();

    private static readonly Lazy<Recording> _circuit = new(() => Synthetic.Circuit());

    /// <summary>The synthetic circuit, made once per test run.</summary>
    public static Recording Circuit => _circuit.Value;
}
