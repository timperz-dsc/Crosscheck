using System.Text.Json.Nodes;

namespace Crosscheck.Bridge.Follow;

/// <summary>
/// One sim frame of the user aircraft as the physics stream carries it (CRO-13, protocol.md section 9).
/// Units as SimConnect delivers them. Angles keep the sim's own signs (PLANE PITCH DEGREES is negative
/// nose up); nothing here depends on the signs. World velocity axes (MSFS): X east, Y up, Z north.
/// Ported from the CRO-17 follower spike (its Follower.Core/Frame.cs), trimmed to the wire set.
/// </summary>
public readonly record struct PhysicsFrame
{
    /// <summary>Seconds, monotonic: the sending sim's own clock (SIMULATION TIME, see <see cref="FrameClock"/>).</summary>
    public double T { get; init; }
    public double Lat { get; init; }
    public double Lon { get; init; }
    public double AltFt { get; init; }
    public double PitchDeg { get; init; }
    public double BankDeg { get; init; }
    public double HeadingDeg { get; init; }
    public double VelWorldX { get; init; }
    public double VelWorldY { get; init; }
    public double VelWorldZ { get; init; }
    public double VelBodyY { get; init; }
    public double VelBodyZ { get; init; }
    public double GForce { get; init; }
    public bool OnGround { get; init; }
    public double GroundSpeedKt { get; init; }
    /// <summary>ELEVATOR POSITION, AILERON POSITION, RUDDER POSITION (position, -1 … 1).</summary>
    public double Elevator { get; init; }
    public double Aileron { get; init; }
    public double Rudder { get; init; }
}

/// <summary>The stream's field names (protocol.md section 9). The body's <c>data</c> map uses exactly these.</summary>
public static class StreamFields
{
    /// <summary>Fields a frame must carry to be played; a frame without one of them is ignored and counted.</summary>
    public static readonly IReadOnlyList<string> Required = ["t", "lat", "lon", "alt_ft", "pitch", "bank", "hdg", "vby", "vbz", "vwx", "vwy", "vwz", "on_ground"];

    /// <summary>Prefix of profile stream vars (L: vars such as the thrust levers) in the data map: "lv_" + lower-case name.</summary>
    public const string LvarPrefix = "lv_";

    public static string WireName(string lvar) => LvarPrefix + lvar.ToLowerInvariant();

    public static JsonObject ToWire(in PhysicsFrame f, IReadOnlyDictionary<string, double>? lvars = null)
    {
        var o = new JsonObject
        {
            ["t"] = f.T,
            ["lat"] = f.Lat,
            ["lon"] = f.Lon,
            ["alt_ft"] = f.AltFt,
            ["pitch"] = f.PitchDeg,
            ["bank"] = f.BankDeg,
            ["hdg"] = f.HeadingDeg,
            ["vby"] = f.VelBodyY,
            ["vbz"] = f.VelBodyZ,
            ["vwx"] = f.VelWorldX,
            ["vwy"] = f.VelWorldY,
            ["vwz"] = f.VelWorldZ,
            ["g"] = f.GForce,
            ["on_ground"] = f.OnGround ? 1.0 : 0.0,
            ["gs_kt"] = f.GroundSpeedKt,
            ["elev"] = f.Elevator,
            ["ail"] = f.Aileron,
            ["rud"] = f.Rudder,
        };
        foreach (var (name, value) in lvars ?? new Dictionary<string, double>())
            if (double.IsFinite(value)) o[WireName(name)] = value;
        return o;
    }

    /// <summary>Reads a frame from the data map; false if a required field is missing or not a finite number.</summary>
    public static bool TryFromWire(JsonObject data, out PhysicsFrame frame, out Dictionary<string, double> lvars)
    {
        frame = default;
        lvars = new Dictionary<string, double>(StringComparer.Ordinal);
        var v = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, node) in data)
        {
            if (node is not JsonValue jv || !TryNumber(jv, out var d) || !double.IsFinite(d)) continue;
            if (key.StartsWith(LvarPrefix, StringComparison.Ordinal)) lvars[key[LvarPrefix.Length..]] = d;
            else v[key] = d;
        }
        if (Required.Any(r => !v.ContainsKey(r))) return false;
        double G(string k) => v.GetValueOrDefault(k);
        frame = new PhysicsFrame
        {
            T = G("t"), Lat = G("lat"), Lon = G("lon"), AltFt = G("alt_ft"),
            PitchDeg = G("pitch"), BankDeg = G("bank"), HeadingDeg = G("hdg"),
            VelBodyY = G("vby"), VelBodyZ = G("vbz"),
            VelWorldX = G("vwx"), VelWorldY = G("vwy"), VelWorldZ = G("vwz"),
            GForce = v.TryGetValue("g", out var g) ? g : 1,
            OnGround = G("on_ground") >= 0.5,
            GroundSpeedKt = G("gs_kt"),
            Elevator = G("elev"), Aileron = G("ail"), Rudder = G("rud"),
        };
        return Math.Abs(frame.Lat) <= 90 && Math.Abs(frame.Lon) <= 360;
    }

    /// <summary>A number from the wire (parsed JSON) or built in memory (any numeric type).</summary>
    private static bool TryNumber(JsonValue v, out double d)
    {
        if (v.TryGetValue(out d)) return true;
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        if (v.TryGetValue<float>(out var f)) { d = f; return true; }
        d = 0;
        return false;
    }
}
