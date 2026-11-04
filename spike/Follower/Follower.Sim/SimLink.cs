// SimConnect plumbing shared by the recorder, the player and restore: connecting, the
// read-only state definition, and (player and restore only) the one place where sim events
// are mapped and sent. Only names from AllowedEvents can ever be mapped or sent.

using System.Runtime.InteropServices;
using Crosscheck.Follower;
using Microsoft.FlightSimulator.SimConnect;
using SC = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace Crosscheck.Follower.Sim;

enum Def : uint { State = 1, Title = 2, Flags = 3, PlacePos = 10, PlaceBody = 11, PlaceWorld = 12 }

enum Req : uint { State = 1, Title = 2, Flags = 3 }

enum Ev : uint { }

enum Group : uint { Highest = 1 }

[StructLayout(LayoutKind.Sequential)]
struct StateData
{
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = Fields.SimVarCount)]
    public double[] V;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
struct TitleData
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Title;
}

[StructLayout(LayoutKind.Sequential)]
struct FlagsData
{
    public double LatLon, Altitude, Attitude, Slew;
}

/// <summary>Position and attitude only.</summary>
[StructLayout(LayoutKind.Sequential)]
struct Pos6
{
    public double Lat, Lon, AltFt, Pitch, Bank, Heading;
}

/// <summary>Position, attitude and six velocities (body or world, plus body rotation rates).</summary>
[StructLayout(LayoutKind.Sequential)]
struct Pos12
{
    public double Lat, Lon, AltFt, Pitch, Bank, Heading, V1, V2, V3, R1, R2, R3;
}

static class SimLink
{
    public static SC? TryConnect(string name, WaitHandle wake)
    {
        try { return new SC(name, IntPtr.Zero, 0, wake, 0); }
        catch (COMException) { return null; }
    }

    /// <summary>Read-only: the recorded fields of the user aircraft, every sim frame.</summary>
    public static void RequestState(SC sc)
    {
        foreach (var f in Fields.SimVars)
            sc.AddToDataDefinition(Def.State, f.SimVar, f.Unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<StateData>(Def.State);
        sc.AddToDataDefinition(Def.Title, "TITLE", null, SIMCONNECT_DATATYPE.STRING256, 0, SC.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<TitleData>(Def.Title);
        sc.RequestDataOnSimObject(Req.State, Def.State, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        sc.RequestDataOnSimObject(Req.Title, Def.Title, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
    }

    /// <summary>Read-only: whether each freeze and slew is on, every 6th frame.</summary>
    public static void RequestFlags(SC sc)
    {
        foreach (var (_, stateVar) in AllowedEvents.StateVars)
            sc.AddToDataDefinition(Def.Flags, stateVar, "bool", SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
        sc.RegisterDataDefineStruct<FlagsData>(Def.Flags);
        sc.RequestDataOnSimObject(Req.Flags, Def.Flags, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 6, 0);
    }

    public static Frame ToFrame(double t, in StateData d) => Fields.FromSimValues(t, d.V);

    public static double FlagFor(string ev, in FlagsData f) => ev switch
    {
        AllowedEvents.FreezeLatLon => f.LatLon,
        AllowedEvents.FreezeAltitude => f.Altitude,
        AllowedEvents.FreezeAttitude => f.Attitude,
        AllowedEvents.SlewOn => f.Slew,
        _ => double.NaN,
    };

    // ---------------------------------------------------------------- player and restore only

    /// <summary>Maps the allowed events, and nothing else. Event id = index in AllowedEvents.All.</summary>
    public static void MapAllowedEvents(SC sc)
    {
        for (var i = 0; i < AllowedEvents.All.Count; i++)
            sc.MapClientEventToSimEvent((Ev)(uint)i, AllowedEvents.Check(AllowedEvents.All[i]));
    }

    /// <summary>Sends one allowed event to the user aircraft. Throws for anything not in AllowedEvents.</summary>
    public static void Send(SC sc, string name, uint data)
    {
        var id = (uint)AllowedEvents.All.ToList().IndexOf(AllowedEvents.Check(name));
        sc.TransmitClientEvent(SC.SIMCONNECT_OBJECT_ID_USER, (Ev)id, data, Group.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    /// <summary>The follower's place: position and attitude, plus velocities unless slewing or told not to.</summary>
    public static void DefinePlacement(SC sc)
    {
        string[] pos = ["PLANE LATITUDE", "PLANE LONGITUDE", "PLANE ALTITUDE", "PLANE PITCH DEGREES", "PLANE BANK DEGREES", "PLANE HEADING DEGREES TRUE"];
        string[] posUnits = ["degrees", "degrees", "feet", "degrees", "degrees", "degrees"];
        string[] body = ["VELOCITY BODY X", "VELOCITY BODY Y", "VELOCITY BODY Z"];
        string[] world = ["VELOCITY WORLD X", "VELOCITY WORLD Y", "VELOCITY WORLD Z"];
        string[] rot = ["ROTATION VELOCITY BODY X", "ROTATION VELOCITY BODY Y", "ROTATION VELOCITY BODY Z"];
        void Add(Def def, IEnumerable<(string Var, string Unit)> vars)
        {
            foreach (var (v, u) in vars) sc.AddToDataDefinition(def, v, u, SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
        }
        var p = pos.Zip(posUnits).ToArray();
        Add(Def.PlacePos, p);
        sc.RegisterDataDefineStruct<Pos6>(Def.PlacePos);
        Add(Def.PlaceBody, p.Concat(body.Select(v => (v, "feet per second"))).Concat(rot.Select(v => (v, "radians per second"))));
        sc.RegisterDataDefineStruct<Pos12>(Def.PlaceBody);
        Add(Def.PlaceWorld, p.Concat(world.Select(v => (v, "feet per second"))).Concat(rot.Select(v => (v, "radians per second"))));
        sc.RegisterDataDefineStruct<Pos12>(Def.PlaceWorld);
    }

    /// <summary>Writes the follower's place. The only SetDataOnSimObject in this project.</summary>
    public static void Place(SC sc, in Frame f, VelocityMode velocities, bool slewing)
    {
        if (slewing || velocities == VelocityMode.None)
        {
            sc.SetDataOnSimObject(Def.PlacePos, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                new Pos6 { Lat = f.Lat, Lon = f.Lon, AltFt = f.AltFt, Pitch = f.PitchDeg, Bank = f.BankDeg, Heading = f.HeadingDeg });
            return;
        }
        var world = velocities == VelocityMode.World;
        sc.SetDataOnSimObject(world ? Def.PlaceWorld : Def.PlaceBody, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new Pos12
        {
            Lat = f.Lat, Lon = f.Lon, AltFt = f.AltFt, Pitch = f.PitchDeg, Bank = f.BankDeg, Heading = f.HeadingDeg,
            V1 = world ? f.VelWorldX : f.VelBodyX,
            V2 = world ? f.VelWorldY : f.VelBodyY,
            V3 = world ? f.VelWorldZ : f.VelBodyZ,
            R1 = f.RotBodyX, R2 = f.RotBodyY, R3 = f.RotBodyZ,
        });
    }
}
