namespace Crosscheck.Follower;

/// <summary>
/// One sim frame of the user aircraft, in the units SimConnect delivers them (see
/// <see cref="Fields"/>). Angles keep the sim's own signs: in MSFS, PLANE PITCH DEGREES is
/// negative when the nose is up. Nothing in this project depends on the signs: attitude is
/// interpolated as a rotation, and errors are angles between rotations.
/// World velocity axes (MSFS): X east, Y up, Z north.
/// </summary>
public readonly record struct Frame
{
    /// <summary>
    /// Seconds, monotonic. In a .cxrec file: the recorder's stopwatch when the message was
    /// handled. When played, replaced by the chosen clock (see <see cref="TimeBase"/>).
    /// </summary>
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
    public double VelBodyX { get; init; }
    public double VelBodyY { get; init; }
    public double VelBodyZ { get; init; }
    public double RotBodyX { get; init; }
    public double RotBodyY { get; init; }
    public double RotBodyZ { get; init; }
    public bool OnGround { get; init; }
    public double GroundSpeedKt { get; init; }
    public double AglFt { get; init; }
    public double Gear { get; init; }
    public double FlapsIndex { get; init; }

    // Controls: only ever read, never written. Used to tell the aircraft's own reaction
    // (the Fenix fly-by-wire) apart from our placement.
    public double YokeX { get; init; }
    public double YokeY { get; init; }
    public double RudderPedal { get; init; }
    public double ElevatorDeg { get; init; }
    public double AileronDeg { get; init; }
    public double RudderDeg { get; init; }
    public double SpoilersLeft { get; init; }
    public double FenixStickCaptPitch { get; init; }
    public double FenixStickCaptBank { get; init; }
    public double FenixStickFoPitch { get; init; }
    public double FenixStickFoBank { get; init; }

    // Clocks and extras (added 2026-11-04; 0 in older recordings, see Recording.Has).
    /// <summary>SIMULATION TIME, seconds: the sim's own clock for this frame.</summary>
    public double SimTimeS { get; init; }
    /// <summary>ABSOLUTE TIME, seconds: a second sim clock candidate.</summary>
    public double AbsTimeS { get; init; }
    public double GForce { get; init; }
}

public enum FieldType : byte
{
    F64 = 1,
    F32 = 2,
    Bool = 3,
}

/// <summary>One recorded field: name in the file, the SimConnect var it comes from, and its unit.</summary>
public sealed record FieldSpec(string Name, string SimVar, string Unit, FieldType Type, Func<Frame, double> Get, Func<Frame, double, Frame> Set)
{
    public int Size => Type switch { FieldType.F64 => 8, FieldType.F32 => 4, _ => 1 };
}

/// <summary>The field table: one place for names, sim vars, units and storage widths.</summary>
public static class Fields
{
    /// <summary>Fields read from the sim (all except <see cref="Frame.T"/>), in this order.</summary>
    public const int SimVarCount = 34;

    public static readonly IReadOnlyList<FieldSpec> All =
    [
        new("t", "", "seconds", FieldType.F64, f => f.T, (f, v) => f with { T = v }),
        new("lat", "PLANE LATITUDE", "degrees", FieldType.F64, f => f.Lat, (f, v) => f with { Lat = v }),
        new("lon", "PLANE LONGITUDE", "degrees", FieldType.F64, f => f.Lon, (f, v) => f with { Lon = v }),
        new("alt_ft", "PLANE ALTITUDE", "feet", FieldType.F64, f => f.AltFt, (f, v) => f with { AltFt = v }),
        new("pitch_deg", "PLANE PITCH DEGREES", "degrees", FieldType.F32, f => f.PitchDeg, (f, v) => f with { PitchDeg = v }),
        new("bank_deg", "PLANE BANK DEGREES", "degrees", FieldType.F32, f => f.BankDeg, (f, v) => f with { BankDeg = v }),
        new("heading_deg", "PLANE HEADING DEGREES TRUE", "degrees", FieldType.F32, f => f.HeadingDeg, (f, v) => f with { HeadingDeg = v }),
        new("vel_world_x", "VELOCITY WORLD X", "feet per second", FieldType.F32, f => f.VelWorldX, (f, v) => f with { VelWorldX = v }),
        new("vel_world_y", "VELOCITY WORLD Y", "feet per second", FieldType.F32, f => f.VelWorldY, (f, v) => f with { VelWorldY = v }),
        new("vel_world_z", "VELOCITY WORLD Z", "feet per second", FieldType.F32, f => f.VelWorldZ, (f, v) => f with { VelWorldZ = v }),
        new("vel_body_x", "VELOCITY BODY X", "feet per second", FieldType.F32, f => f.VelBodyX, (f, v) => f with { VelBodyX = v }),
        new("vel_body_y", "VELOCITY BODY Y", "feet per second", FieldType.F32, f => f.VelBodyY, (f, v) => f with { VelBodyY = v }),
        new("vel_body_z", "VELOCITY BODY Z", "feet per second", FieldType.F32, f => f.VelBodyZ, (f, v) => f with { VelBodyZ = v }),
        new("rot_body_x", "ROTATION VELOCITY BODY X", "radians per second", FieldType.F32, f => f.RotBodyX, (f, v) => f with { RotBodyX = v }),
        new("rot_body_y", "ROTATION VELOCITY BODY Y", "radians per second", FieldType.F32, f => f.RotBodyY, (f, v) => f with { RotBodyY = v }),
        new("rot_body_z", "ROTATION VELOCITY BODY Z", "radians per second", FieldType.F32, f => f.RotBodyZ, (f, v) => f with { RotBodyZ = v }),
        new("on_ground", "SIM ON GROUND", "bool", FieldType.Bool, f => f.OnGround ? 1 : 0, (f, v) => f with { OnGround = v >= 0.5 }),
        new("gs_kt", "GROUND VELOCITY", "knots", FieldType.F32, f => f.GroundSpeedKt, (f, v) => f with { GroundSpeedKt = v }),
        new("agl_ft", "PLANE ALT ABOVE GROUND", "feet", FieldType.F32, f => f.AglFt, (f, v) => f with { AglFt = v }),
        new("gear", "GEAR TOTAL PCT EXTENDED", "percent over 100", FieldType.F32, f => f.Gear, (f, v) => f with { Gear = v }),
        new("flaps_index", "FLAPS HANDLE INDEX", "number", FieldType.F32, f => f.FlapsIndex, (f, v) => f with { FlapsIndex = v }),
        new("yoke_x", "YOKE X POSITION", "position", FieldType.F32, f => f.YokeX, (f, v) => f with { YokeX = v }),
        new("yoke_y", "YOKE Y POSITION", "position", FieldType.F32, f => f.YokeY, (f, v) => f with { YokeY = v }),
        new("rudder_pedal", "RUDDER PEDAL POSITION", "position", FieldType.F32, f => f.RudderPedal, (f, v) => f with { RudderPedal = v }),
        new("elevator_deg", "ELEVATOR DEFLECTION", "degrees", FieldType.F32, f => f.ElevatorDeg, (f, v) => f with { ElevatorDeg = v }),
        new("aileron_deg", "AILERON AVERAGE DEFLECTION", "degrees", FieldType.F32, f => f.AileronDeg, (f, v) => f with { AileronDeg = v }),
        new("rudder_deg", "RUDDER DEFLECTION", "degrees", FieldType.F32, f => f.RudderDeg, (f, v) => f with { RudderDeg = v }),
        new("spoilers_left", "SPOILERS LEFT POSITION", "percent over 100", FieldType.F32, f => f.SpoilersLeft, (f, v) => f with { SpoilersLeft = v }),
        new("fnx_stick_capt_pitch", "L:N_FC_SIDESTICK_CAPT_PITCH", "number", FieldType.F32, f => f.FenixStickCaptPitch, (f, v) => f with { FenixStickCaptPitch = v }),
        new("fnx_stick_capt_bank", "L:N_FC_SIDESTICK_CAPT_BANK", "number", FieldType.F32, f => f.FenixStickCaptBank, (f, v) => f with { FenixStickCaptBank = v }),
        new("fnx_stick_fo_pitch", "L:N_FC_SIDESTICK_FO_PITCH", "number", FieldType.F32, f => f.FenixStickFoPitch, (f, v) => f with { FenixStickFoPitch = v }),
        new("fnx_stick_fo_bank", "L:N_FC_SIDESTICK_FO_BANK", "number", FieldType.F32, f => f.FenixStickFoBank, (f, v) => f with { FenixStickFoBank = v }),
        new("sim_time", "SIMULATION TIME", "seconds", FieldType.F64, f => f.SimTimeS, (f, v) => f with { SimTimeS = v }),
        new("abs_time", "ABSOLUTE TIME", "seconds", FieldType.F64, f => f.AbsTimeS, (f, v) => f with { AbsTimeS = v }),
        new("g_force", "G FORCE", "gforce", FieldType.F32, f => f.GForce, (f, v) => f with { GForce = v }),
    ];

    /// <summary>The fields read from the sim, in data definition order.</summary>
    public static readonly IReadOnlyList<FieldSpec> SimVars = All.Where(f => f.SimVar != "").ToArray();

    public static FieldSpec? Find(string name) => All.FirstOrDefault(f => f.Name == name);

    /// <summary>Builds a frame from sim values in <see cref="SimVars"/> order, stamped with <paramref name="t"/>.</summary>
    public static Frame FromSimValues(double t, ReadOnlySpan<double> values)
    {
        if (values.Length < SimVarCount) throw new ArgumentException($"expected {SimVarCount} values, got {values.Length}");
        var f = new Frame { T = t };
        for (var i = 0; i < SimVarCount; i++) f = SimVars[i].Set(f, values[i]);
        return f;
    }
}
