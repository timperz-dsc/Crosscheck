// Sim vars (A:) the positions run reads next to the Fenix L: vars, to find what Fenix really reads for
// a control whose L: var write did not stick in gate test 2 (flaps, spoilers, rudder trim, the door).
// Read only: ObserveReader asks for them like the L: vars and never writes. All are documented SimConnect
// sim vars; whether Fenix drives them is what the run finds out (a var it ignores just never moves).

namespace ProfileProbe;

static class SimVars
{
    /// <summary>Every sim var a run may read, with its unit.</summary>
    public static readonly IReadOnlyDictionary<string, string> Units = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["FLAPS HANDLE INDEX"] = "number",
        ["FLAPS HANDLE PERCENT"] = "percent",
        ["TRAILING EDGE FLAPS LEFT PERCENT"] = "percent",
        ["SPOILERS HANDLE POSITION"] = "percent",
        ["SPOILERS ARMED"] = "bool",
        ["SPOILERS LEFT POSITION"] = "percent",
        ["ANTISKID BRAKES ACTIVE"] = "bool",
        ["NOSEWHEEL LOCK ON GROUND"] = "bool",
        ["RUDDER TRIM PCT"] = "percent over 100",
        ["GEAR HANDLE POSITION"] = "bool",
        ["GEAR EMERGENCY HANDLE POSITION"] = "bool",
        ["AUTO BRAKE SWITCH CB"] = "number",
        ["AUTOPILOT SPEED SLOT INDEX"] = "number",
        ["AUTOPILOT HEADING SLOT INDEX"] = "number",
        ["AUTOPILOT ALTITUDE SLOT INDEX"] = "number",
        ["AUTOPILOT VS SLOT INDEX"] = "number",
        ["KOHLSMAN SETTING STD:1"] = "bool",
        ["KOHLSMAN SETTING STD:2"] = "bool",
        ["INTERACTIVE POINT OPEN:0"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:1"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:2"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:3"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:4"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:5"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:6"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:7"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:8"] = "percent over 100",
        ["INTERACTIVE POINT OPEN:9"] = "percent over 100",
        // The `throttle` run (flight-1 diagnosis item 7): documented SDK sim vars, not in our L: var scan.
        ["GENERAL ENG THROTTLE LEVER POSITION:1"] = "percent",
        ["GENERAL ENG THROTTLE LEVER POSITION:2"] = "percent",
    };

    public const string Prefix = "A:";

    public static bool IsSimVar(string var) => var.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The name SimConnect knows ("FLAPS HANDLE INDEX").</summary>
    public static string Name(string var) => var[Prefix.Length..];

    public static string Unit(string var) =>
        Units.TryGetValue(Name(var), out var unit) ? unit : throw new ArgumentException($"no unit for sim var {var}");

    /// <summary>"A:FLAPS HANDLE INDEX", as the run lists it.</summary>
    public static string A(string name) => Units.ContainsKey(name) ? Prefix + name : throw new ArgumentException($"unknown sim var {name}");
}
