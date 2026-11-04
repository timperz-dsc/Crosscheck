namespace Crosscheck.Bridge.Sim;

public enum SimState
{
    /// <summary>No sim found yet, or it exited. The adapter keeps retrying.</summary>
    Disconnected,
    Connected,
}

/// <summary>
/// What the adapter watches: profile vars, plus a few known L: vars that are only read for the diagnosis log
/// (<c>DiagnosisLog.ExtraLvars</c>; the engine ignores them, nothing is compared, synced or shown).
/// </summary>
public sealed record SimPlan(IReadOnlyList<string> Watched);

/// <summary>
/// How the bridge reads and writes cockpit L: vars. It can read and write profile L: vars and nothing else:
/// there is no API for sim events, simvars, stopping or speeding up the sim, or position. (The follower's
/// placement has its own, narrower link: Follow.IPhysicsLink.) Every write goes through
/// <see cref="SimSafetyGuard"/> inside the adapter.
/// </summary>
public interface ISimAdapter : IAsyncDisposable
{
    SimState State { get; }

    event Action<SimState>? StateChanged;

    /// <summary>A watched var changed (raised from the adapter's own thread, at most once per frame per var).</summary>
    event Action<string, double>? ValueChanged;

    /// <summary>Starts watching. Returns at once; connects in the background and keeps retrying.</summary>
    Task StartAsync(SimPlan plan, CancellationToken ct);

    /// <summary>Latest known value of a watched var.</summary>
    bool TryGet(string name, out double value);

    /// <summary>
    /// true when the value <see cref="TryGet"/> returns already reflects our own last write to
    /// this var (SimConnect reports it one or two frames later). Used to tell a pilot's click
    /// apart from a read-back that is still on its way.
    /// </summary>
    bool IsFresh(string name);

    /// <summary>Writes an L: var. Throws <see cref="SafetyViolationException"/> if the guard refuses.</summary>
    Task WriteAsync(string name, double value);

    /// <summary>The write guard every write passes (inside the adapter).</summary>
    SimSafetyGuard Guard { get; }

    /// <summary>Follower writer for stream frames; refuses while this side is PF.</summary>
    GuardedStreamWriter Stream { get; }

    /// <summary>
    /// SimConnect refused a write (or the definition it needs) for this var: (var, why). Raised from the
    /// adapter's own thread. The bridge blocks the var and shows a red line; never silent (gate test 3 cause A).
    /// </summary>
    event Action<string, string>? WriteRejected;

    /// <summary>The link to the sim could not be set up completely (why). Raised from the adapter's own thread.</summary>
    event Action<string>? SetupError;

    /// <summary>
    /// Every SimConnect exception, in words, with the var it belongs to when known (for the session log). Raised
    /// from the adapter's own thread, in addition to <see cref="WriteRejected"/> or <see cref="SetupError"/>.
    /// </summary>
    event Action<string>? SimException;

    /// <summary>Setup errors of the current connection (empty when the link is complete).</summary>
    IReadOnlyList<string> SetupErrors { get; }
}
