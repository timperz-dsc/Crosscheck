using System.Diagnostics;

namespace Crosscheck.Bridge;

/// <summary>Monotonic milliseconds. Injected so timing logic (echo windows) can be tested.</summary>
public interface IClock
{
    double NowMs { get; }
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    public double NowMs => _watch.Elapsed.TotalMilliseconds;
}

/// <summary>A clock that only moves when told to (tests).</summary>
public sealed class ManualClock : IClock
{
    public double NowMs { get; private set; }
    public void Advance(double ms) => NowMs += ms;
}
