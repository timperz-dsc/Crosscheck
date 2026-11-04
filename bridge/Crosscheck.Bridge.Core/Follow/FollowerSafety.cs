// The follower's allow list (owner decision 2026-11-04, CRO-13; CLAUDE.md hard safety rules).
//
// The bridge may freeze and place the aircraft ONLY while it is the follower (PM), never on the PF. Allowed:
// the three FREEZE_*_SET events, and writing the follower's position, attitude, the mixed velocity set and
// the control surfaces (thrust levers go through the profile and SimSafetyGuard like every L: var).
// Stopping the sim, changing its speed, moving the aircraft any other way, and every other event or sim var
// stay forbidden (SafetyTripwireTests scans the bridge for those names). This file is the only place
// that names them; SafetyTripwireTests fails if an event or position var name appears anywhere else.

using Crosscheck.Bridge.Sim;

namespace Crosscheck.Bridge.Follow;

public static class FollowerSafety
{
    public const string FreezeLatLon = "FREEZE_LATITUDE_LONGITUDE_SET";
    public const string FreezeAltitude = "FREEZE_ALTITUDE_SET";
    public const string FreezeAttitude = "FREEZE_ATTITUDE_SET";

    /// <summary>The only sim events the bridge can ever map or send. Data 1 = on, 0 = off.</summary>
    public static readonly IReadOnlyList<string> Events = [FreezeLatLon, FreezeAltitude, FreezeAttitude];

    /// <summary>Read-only sim vars that show whether each freeze is on.</summary>
    public static readonly IReadOnlyList<(string Event, string StateVar)> StateVars =
    [
        (FreezeLatLon, "IS LATITUDE LONGITUDE FREEZE ON"),
        (FreezeAltitude, "IS ALTITUDE FREEZE ON"),
        (FreezeAttitude, "IS ATTITUDE FREEZE ON"),
    ];

    /// <summary>
    /// The follower's placement, written in one definition every sim frame: position, attitude and the mixed
    /// velocity set YourControls and FS Copilot write (research section 6.1), in this order.
    /// </summary>
    public static readonly IReadOnlyList<(string Var, string Unit)> Placement =
    [
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("PLANE ALTITUDE", "feet"),
        ("PLANE PITCH DEGREES", "degrees"),
        ("PLANE BANK DEGREES", "degrees"),
        ("PLANE HEADING DEGREES TRUE", "degrees"),
        ("VELOCITY BODY Y", "feet per second"),
        ("VELOCITY BODY Z", "feet per second"),
        ("VELOCITY WORLD X", "feet per second"),
        ("VELOCITY WORLD Z", "feet per second"),
    ];

    /// <summary>Extra vars, each written on its own definition, so a refusal by the sim stops only that one.</summary>
    public static readonly IReadOnlyList<(string Var, string Unit)> Extras =
    [
        ("VERTICAL SPEED", "feet per second"),
        ("G FORCE", "gforce"),
        ("ELEVATOR POSITION", "position"),
        ("AILERON POSITION", "position"),
        ("RUDDER POSITION", "position"),
    ];

    /// <summary>What both sides read of their own aircraft every sim frame (read only), in this order.</summary>
    public static readonly IReadOnlyList<(string Var, string Unit)> Read =
    [
        ("SIMULATION TIME", "seconds"),
        ("PLANE LATITUDE", "degrees"),
        ("PLANE LONGITUDE", "degrees"),
        ("PLANE ALTITUDE", "feet"),
        ("PLANE PITCH DEGREES", "degrees"),
        ("PLANE BANK DEGREES", "degrees"),
        ("PLANE HEADING DEGREES TRUE", "degrees"),
        ("VELOCITY WORLD X", "feet per second"),
        ("VELOCITY WORLD Y", "feet per second"),
        ("VELOCITY WORLD Z", "feet per second"),
        ("VELOCITY BODY Y", "feet per second"),
        ("VELOCITY BODY Z", "feet per second"),
        ("G FORCE", "gforce"),
        ("SIM ON GROUND", "bool"),
        ("GROUND VELOCITY", "knots"),
        ("ELEVATOR POSITION", "position"),
        ("AILERON POSITION", "position"),
        ("RUDDER POSITION", "position"),
    ];

    /// <summary>Builds a frame from values in <see cref="Read"/> order; T is set by the caller's clock.</summary>
    public static PhysicsFrame FromRead(ReadOnlySpan<double> v) => new()
    {
        Lat = v[1], Lon = v[2], AltFt = v[3], PitchDeg = v[4], BankDeg = v[5], HeadingDeg = v[6],
        VelWorldX = v[7], VelWorldY = v[8], VelWorldZ = v[9], VelBodyY = v[10], VelBodyZ = v[11],
        GForce = v[12], OnGround = v[13] >= 0.5, GroundSpeedKt = v[14], Elevator = v[15], Aileron = v[16], Rudder = v[17],
    };

    /// <summary>The value written for each <see cref="Placement"/> var, in order.</summary>
    public static double[] PlacementValues(in PhysicsFrame f) =>
        [f.Lat, f.Lon, f.AltFt, f.PitchDeg, f.BankDeg, f.HeadingDeg, f.VelBodyY, f.VelBodyZ, f.VelWorldX, f.VelWorldZ];

    /// <summary>The value written for each <see cref="Extras"/> var, in order.</summary>
    public static double[] ExtraValues(in PhysicsFrame f) => [f.VelWorldY, f.GForce, f.Elevator, f.Aileron, f.Rudder];

    /// <summary>Throws unless <paramref name="name"/> is one of the three freeze events.</summary>
    public static string CheckEvent(string name) =>
        Events.Contains(name, StringComparer.Ordinal) ? name : throw new SafetyViolationException($"event {name} is not allowed: Crosscheck only sends the three FREEZE_*_SET events, and only on the follower");

    /// <summary>Throws unless <paramref name="name"/> is a placement or extra var.</summary>
    public static string CheckWriteVar(string name) =>
        Placement.Any(p => p.Var == name) || Extras.Any(p => p.Var == name)
            ? name
            : throw new SafetyViolationException($"sim var {name} is not on the follower's allow list");
}

/// <summary>The three freeze flags as the sim reports them.</summary>
public readonly record struct FreezeFlags(bool LatLon, bool Altitude, bool Attitude)
{
    public bool AnyOn => LatLon || Altitude || Attitude;
    public bool AllOn => LatLon && Altitude && Attitude;

    public bool IsOn(string ev) => ev switch
    {
        FollowerSafety.FreezeLatLon => LatLon,
        FollowerSafety.FreezeAltitude => Altitude,
        FollowerSafety.FreezeAttitude => Attitude,
        _ => false,
    };
}

/// <summary>
/// Remembers every freeze switched on, so all of them are switched off again exactly once (ported from the
/// spike). The undo is registered before the event is sent, so a half-done start is undone too.
/// </summary>
public sealed class PlacementState(Action<string, uint> send)
{
    private readonly object _lock = new();
    private readonly List<string> _engaged = [];
    private bool _undone;

    public IReadOnlyList<string> Engaged
    {
        get { lock (_lock) return _engaged.ToArray(); }
    }

    public bool IsUndone
    {
        get { lock (_lock) return _undone; }
    }

    public bool IsEngaged
    {
        get { lock (_lock) return !_undone && _engaged.Count > 0; }
    }

    public int Reasserts { get; private set; }

    public void Engage(string ev)
    {
        FollowerSafety.CheckEvent(ev);
        lock (_lock)
        {
            if (_undone) throw new InvalidOperationException("placement already undone");
            if (!_engaged.Contains(ev)) _engaged.Add(ev);
            send(ev, 1);
        }
    }

    /// <summary>Sends an engaged freeze again (it reads off). Ignored after the undo.</summary>
    public bool Reassert(string ev)
    {
        lock (_lock)
        {
            if (_undone || !_engaged.Contains(ev)) return false;
            Reasserts++;
            send(ev, 1);
            return true;
        }
    }

    /// <summary>Switches everything engaged off. Runs once; later calls return an empty list. Every undo is tried even if one fails.</summary>
    public IReadOnlyList<string> Undo()
    {
        lock (_lock)
        {
            if (_undone) return [];
            _undone = true;
            var done = new List<string>();
            Exception? first = null;
            foreach (var ev in Enumerable.Reverse(_engaged))
            {
                try
                {
                    send(FollowerSafety.CheckEvent(ev), 0);
                    done.Add(ev);
                }
                catch (Exception e)
                {
                    first ??= e;
                }
            }
            if (first is not null) throw new AggregateException("a freeze release failed", first);
            return done;
        }
    }
}
