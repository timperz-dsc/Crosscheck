namespace Crosscheck.Follower;

public sealed class SafetyViolationException(string message) : InvalidOperationException(message);

/// <summary>Placement methods compared by the spike.</summary>
public enum Method
{
    /// <summary>Set position, attitude and velocities every frame; nothing else.</summary>
    A,
    /// <summary>A, with the sim's freeze lat/lon, altitude and attitude switched on.</summary>
    B,
    /// <summary>Slew mode on; position and attitude set every frame.</summary>
    C,
}

/// <summary>
/// The only sim events the follower spike may send (owner decision, CRO-17: only in this tool,
/// only offline, own sim only). Everything else is refused, in particular pause and sim rate.
/// </summary>
public static class AllowedEvents
{
    public const string FreezeLatLon = "FREEZE_LATITUDE_LONGITUDE_SET";
    public const string FreezeAltitude = "FREEZE_ALTITUDE_SET";
    public const string FreezeAttitude = "FREEZE_ATTITUDE_SET";
    public const string SlewOn = "SLEW_ON";
    public const string SlewOff = "SLEW_OFF";

    public static readonly IReadOnlyList<string> All = [FreezeLatLon, FreezeAltitude, FreezeAttitude, SlewOn, SlewOff];

    /// <summary>Read-only sim vars that show whether each freeze / slew is on.</summary>
    public static readonly IReadOnlyList<(string Event, string StateVar)> StateVars =
    [
        (FreezeLatLon, "IS LATITUDE LONGITUDE FREEZE ON"),
        (FreezeAltitude, "IS ALTITUDE FREEZE ON"),
        (FreezeAttitude, "IS ATTITUDE FREEZE ON"),
        (SlewOn, "IS SLEW ACTIVE"),
    ];

    public static string Check(string name) =>
        All.Contains(name, StringComparer.Ordinal) ? name : throw new SafetyViolationException($"event {name} is not allowed in the follower spike");

    /// <summary>What a method switches on at the start.</summary>
    public static IReadOnlyList<string> Engage(Method m) => m switch
    {
        Method.A => [],
        Method.B => [FreezeLatLon, FreezeAltitude, FreezeAttitude],
        Method.C => [SlewOn],
        _ => throw new ArgumentOutOfRangeException(nameof(m)),
    };

    /// <summary>The event (and data) that switches <paramref name="engaged"/> off again.</summary>
    public static (string Event, uint Data) UndoOf(string engaged) => engaged switch
    {
        FreezeLatLon or FreezeAltitude or FreezeAttitude => (engaged, 0),
        SlewOn => (SlewOff, 0),
        _ => throw new SafetyViolationException($"nothing to undo for {engaged}"),
    };

    /// <summary>Everything off, whatever was on: used by the restore command.</summary>
    public static IReadOnlyList<(string Event, uint Data)> RestoreAll =>
        [(FreezeLatLon, 0), (FreezeAltitude, 0), (FreezeAttitude, 0), (SlewOff, 0)];
}

/// <summary>
/// Remembers every freeze or slew switched on, so all of them are switched off again exactly
/// once: on the stop key, Ctrl+C, a crash, process exit or the sim going away. The undo is
/// registered before the event is sent, so a half-done start is undone too.
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

    public int Reasserts { get; private set; }

    public void Engage(string ev)
    {
        AllowedEvents.Check(ev);
        if (ev == AllowedEvents.SlewOff) throw new SafetyViolationException("SLEW_OFF is an undo, not something to engage");
        lock (_lock)
        {
            if (_undone) throw new InvalidOperationException("placement already undone");
            if (!_engaged.Contains(ev)) _engaged.Add(ev);
            send(ev, 1);
        }
    }

    /// <summary>Sends an engaged event again (a freeze that reads off). Ignored after the undo.</summary>
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
    public IReadOnlyList<(string Event, uint Data)> Undo()
    {
        lock (_lock)
        {
            if (_undone) return [];
            _undone = true;
            var done = new List<(string, uint)>();
            Exception? first = null;
            foreach (var ev in Enumerable.Reverse(_engaged))
            {
                var (undo, data) = AllowedEvents.UndoOf(ev);
                try
                {
                    send(AllowedEvents.Check(undo), data);
                    done.Add((undo, data));
                }
                catch (Exception e)
                {
                    first ??= e;
                }
            }
            if (first is not null) throw new AggregateException("an undo failed", first);
            return done;
        }
    }
}

/// <summary>The player asks the pilot to confirm this sim is offline before it connects.</summary>
public static class OfflineConfirmation
{
    public const string Word = "OFFLINE";

    public static readonly string Prompt =
        "The follower spike moves YOUR aircraft (position, attitude, velocity" +
        " and, for methods B and C, the sim's freeze or slew).\n" +
        "Use it only offline, with your own sim as the follower.\n" +
        "Confirm you are NOT connected to VATSIM, IVAO, PilotEdge or any other network or multiplayer session.\n" +
        $"Type {Word} and press Enter to continue (anything else quits): ";

    public static bool Accept(string? input) => string.Equals(input?.Trim(), Word, StringComparison.Ordinal);
}

/// <summary>Known online-network pilot clients. If one runs, the player refuses to start.</summary>
public static class NetworkClients
{
    public static readonly IReadOnlyList<string> ProcessNames =
        ["vPilot", "xPilot", "swiftguistd", "swiftcore", "swiftdata", "Altitude", "IvAp", "IVAO_Pilot_Core", "PilotEdge", "PECLIENT"];

    public static IReadOnlyList<string> Find(IEnumerable<string> runningProcessNames) =>
        runningProcessNames.Where(p => ProcessNames.Any(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}

/// <summary>Playbacks end on the ground and stopped unless the pilot explicitly allows an end in the air.</summary>
public static class EndCheck
{
    public const double StoppedKt = 2;

    /// <summary>Null if the recording may be played; otherwise why not.</summary>
    public static string? Problem(IReadOnlyList<Frame> frames, bool allowAirEnd)
    {
        if (frames.Count < 2) return "the recording has fewer than two frames";
        var last = frames[^1];
        if (allowAirEnd) return null;
        if (!last.OnGround)
            return $"the recording ends in the air ({last.AglFt:0} ft above ground). Record a full circuit that ends stopped on the ground, or pass --allow-air-end";
        if (last.GroundSpeedKt > StoppedKt)
            return $"the recording ends moving ({last.GroundSpeedKt:0} kt). Record until the aircraft is stopped, or pass --allow-air-end";
        return null;
    }
}
