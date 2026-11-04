namespace Crosscheck.Bridge.Follow;

/// <summary>One new sim frame of this sim's own aircraft. T = this sim's clock (FrameClock), seconds.</summary>
/// <param name="Lvars">Profile stream vars read with it (thrust levers), by L: var name.</param>
public sealed record OwnFrame(PhysicsFrame Frame, IReadOnlyDictionary<string, double> Lvars);

/// <summary>
/// The physics side of the sim (CRO-13): reads the own aircraft every sim frame and, ONLY while this side is
/// the follower, places it and sends the three freeze events. Separate from <see cref="Sim.ISimAdapter"/>,
/// which can never touch position or events. Every write passes <see cref="FollowerSafety"/> and the role gate
/// (<see cref="SetFollower"/>): while not the follower, Place and a freeze "on" are refused. A freeze "off" is
/// always allowed (releasing can never hurt).
/// </summary>
public interface IPhysicsLink : IAsyncDisposable
{
    bool Connected { get; }

    event Action<bool>? ConnectedChanged;

    /// <summary>Each new sim frame (repeats of the same sim frame are skipped). Raised on the link's own thread, never under its lock.</summary>
    event Action<OwnFrame>? FrameRead;

    /// <summary>The freeze flags, a few times per second. Link thread.</summary>
    event Action<FreezeFlags>? FlagsRead;

    /// <summary>The sim refused a placement var: (var, why). It is no longer written. Link thread.</summary>
    event Action<string, string>? WriteRefused;

    /// <summary>Starts reading (connects in the background and keeps retrying). <paramref name="lvars"/>: profile stream L: vars to read.</summary>
    void Start(IReadOnlyList<string> lvars);

    /// <summary>The role gate: true only while this side is the follower (PM). Set false at once when this side becomes PF.</summary>
    void SetFollower(bool follower);

    bool IsFollower { get; }

    /// <summary>
    /// Writes the follower's placement (one write per sim frame). Throws SafetyViolationException unless follower.
    /// With <paramref name="writeSurfaces"/> false, elevator, aileron and rudder are left to the own pilot (0.6.1:
    /// not written while the PF is on the ground below 40 kt); everything else is written as usual.
    /// </summary>
    void Place(in PhysicsFrame frame, bool writeSurfaces = true);

    /// <summary>Sends one of the three freeze events. "on" throws SafetyViolationException unless follower; "off" is always allowed.</summary>
    void SendFreeze(string ev, bool on);
}
