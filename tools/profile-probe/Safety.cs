// Safety stop. Checked before each var and after each write step: if the aircraft is no longer
// parked with engines off, or a setup switch changed that the probe did not touch, the current
// var is restored and the run stops.

using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Sim;

namespace ProfileProbe;

sealed class SafetyStopException(string message) : Exception(message);

/// <summary>Sim state that is not an L: var. Read by a separate, read-only SimConnect client.</summary>
sealed record SimvarSnapshot(
    DateTime At,
    double OnGround,
    double ParkingBrake,
    double GroundSpeedKt,
    double N1Eng1,
    double N1Eng2,
    double Combustion1,
    double Combustion2,
    double SoundN1Eng1,
    double SoundN1Eng2);

interface ISimvarReader : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct);
    SimvarSnapshot? Latest { get; }
    /// <summary>The TITLE simvar of the user aircraft, once read.</summary>
    string? Title { get; }
}

static class SafetyVars
{
    public const string ParkingBrake = "S_MIP_PARKING_BRAKE";
    public static readonly string[] EngineMasters = ["S_ENG_MASTER_1", "S_ENG_MASTER_2"];
    /// <summary>The confirmed test setup. Only the probe itself may change one of these, and only while probing it.</summary>
    public static readonly string[] Setup = ["S_OH_ELEC_BAT1", "S_OH_ELEC_BAT2", "S_OH_ELEC_EXT_PWR"];
    /// <summary>
    /// Aircraft electrical power and the EXT PWR ON lamp. Any change is a stop, whatever is being
    /// probed: losing bus power ruins every later test and cannot be undone by writing a var back.
    /// </summary>
    public static readonly string[] Power =
        ["B_ELEC_BUS_POWER_AC_1", "B_ELEC_BUS_POWER_AC_2", "B_ELEC_BUS_POWER_DC_1", "B_ELEC_BUS_POWER_DC_2", "I_OH_ELEC_EXT_PWR_L"];
    public static IEnumerable<string> Lvars => Setup.Concat(EngineMasters).Concat(Power).Append(ParkingBrake);
    public static readonly string[] HydraulicPressures =
        ["aircraft.hydraulics.green.pressure", "aircraft.hydraulics.blue.pressure", "aircraft.hydraulics.yellow.pressure"];
}

sealed class SafetyMonitor(ISimAdapter sim, ISimvarReader simvars, IFenixGraphQl gql)
{
    static readonly TimeSpan Stale = TimeSpan.FromSeconds(3);
    SimvarSnapshot? _base;
    readonly Dictionary<string, double> _setup = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, double> Setup => _setup;
    public SimvarSnapshot? Baseline => _base;

    /// <summary>Records the parked state. Throws if the aircraft is not parked with engines off.</summary>
    public void TakeBaseline()
    {
        var s = Fresh();
        if (s.OnGround < 0.5) throw new SafetyStopException("not on the ground");
        if (s.GroundSpeedKt > 1) throw new SafetyStopException($"moving ({s.GroundSpeedKt:0.0} kt)");
        if (Lvar(SafetyVars.ParkingBrake) < 0.5) throw new SafetyStopException("parking brake is not set");
        foreach (var m in SafetyVars.EngineMasters)
            if (Lvar(m) != 0) throw new SafetyStopException($"{m} is not off");
        if (Math.Max(s.N1Eng1, s.N1Eng2) > 2 || Math.Max(s.SoundN1Eng1, s.SoundN1Eng2) > 0.02)
            throw new SafetyStopException("an engine is turning");
        if (s.Combustion1 > 0 || s.Combustion2 > 0) throw new SafetyStopException("an engine is running");
        foreach (var v in SafetyVars.Power)
            if (Lvar(v) < 0.5) throw new SafetyStopException($"{v} is off: aircraft not on ground power");
        _base = s;
        foreach (var v in SafetyVars.Setup) _setup[v] = Lvar(v);
        foreach (var v in SafetyVars.Power) _power[v] = Lvar(v);
    }

    readonly Dictionary<string, double> _power = new(StringComparer.Ordinal);

    /// <summary>Throws <see cref="SafetyStopException"/> if anything changed unexpectedly.</summary>
    /// <param name="probing">The var being probed right now (allowed to differ from the setup baseline).</param>
    public void Check(string? probing)
    {
        if (_base is null) throw new InvalidOperationException("no safety baseline");
        if (sim.State != SimState.Connected) throw new SafetyStopException("sim disconnected");
        var s = Fresh();
        if (s.OnGround < 0.5) throw new SafetyStopException("SIM ON GROUND changed: not on the ground");
        if (s.GroundSpeedKt > 1) throw new SafetyStopException($"aircraft moving ({s.GroundSpeedKt:0.0} kt)");
        if (Lvar(SafetyVars.ParkingBrake) < 0.5) throw new SafetyStopException("parking brake released");
        if (Math.Abs(s.ParkingBrake - _base.ParkingBrake) > 0.1) throw new SafetyStopException("BRAKE PARKING POSITION changed");
        foreach (var m in SafetyVars.EngineMasters)
            if (Lvar(m) != 0) throw new SafetyStopException($"{m} changed");
        if (s.N1Eng1 > _base.N1Eng1 + 1 || s.N1Eng2 > _base.N1Eng2 + 1) throw new SafetyStopException("engine N1 rising");
        if (s.SoundN1Eng1 > _base.SoundN1Eng1 + 0.01 || s.SoundN1Eng2 > _base.SoundN1Eng2 + 0.01) throw new SafetyStopException("engine N1 rising (Fenix sound var)");
        if (s.Combustion1 != _base.Combustion1 || s.Combustion2 != _base.Combustion2) throw new SafetyStopException("engine combustion changed");
        foreach (var (name, value) in _setup)
            if (name != probing && Lvar(name) != value) throw new SafetyStopException($"setup switch {name} changed ({value} -> {Lvar(name)})");
        foreach (var (name, value) in _power)
            if (Lvar(name) != value) throw new SafetyStopException($"aircraft power changed: {name} {value} -> {Lvar(name)}");
    }

    /// <summary>Hydraulics unpressurised (so a flaps or speedbrake lever cannot move anything). null = unknown.</summary>
    public async Task<bool?> HydraulicsDry(CancellationToken ct)
    {
        foreach (var path in SafetyVars.HydraulicPressures)
        {
            if (await gql.GetAsync(path, ct) is not double p) return null;
            if (p > 100) return false; // pressurised is ~3000 psi; the parked aircraft reads ~3
        }
        return true;
    }

    SimvarSnapshot Fresh()
    {
        var s = simvars.Latest ?? throw new SafetyStopException("no safety data from the sim (SIM ON GROUND, parking brake, N1)");
        if (DateTime.UtcNow - s.At > Stale) throw new SafetyStopException("safety data from the sim is stale");
        return s;
    }

    double Lvar(string name) =>
        sim.TryGet(name, out var v) ? v : throw new SafetyStopException($"cannot read {name}");
}
