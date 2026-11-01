namespace Crosscheck.Bridge.Sim;

public enum SimState
{
    /// <summary>No sim found yet, or it exited. The adapter keeps retrying.</summary>
    Disconnected,
    Connected,
}

/// <summary>What the adapter watches. Only profile vars are ever in here.</summary>
public sealed record SimPlan(IReadOnlyList<string> Watched);

/// <summary>
/// The only way the bridge touches the sim. It can read and write profile L: vars and nothing
/// else: there is no API for sim events, simvars, pause, sim rate, slew or position. Every
/// write goes through <see cref="SimSafetyGuard"/> inside the adapter.
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
}
