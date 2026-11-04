// Ported from the CRO-17 follower spike (its Follower.Core/TimeBase.cs, FrameClock): the sim's own
// clock times the frames (research section 6.8: the PC stopwatch is noisy when two sim frames are handled in
// one go), with the stopwatch as fallback when the sim clock stops or jumps.

namespace Crosscheck.Bridge.Follow;

/// <summary>
/// Turns each state message (stopwatch now + SIMULATION TIME) into a monotonic clock. A repeat of the same sim
/// frame is not a new frame (false); a stopped sim clock falls back to the stopwatch; jumps are bridged.
/// </summary>
public sealed class FrameClock
{
    /// <summary>Repeats in a row before the sim clock counts as stopped.</summary>
    public const int MaxRepeats = 3;
    /// <summary>No sim clock movement for this long (stopwatch) counts as stopped, seconds.</summary>
    public const double StallS = 0.25;
    /// <summary>A sim clock step longer than this is a jump, not a frame, seconds.</summary>
    public const double MaxStepS = 1.0;

    private double _lastSw = double.NaN;
    private double _lastSim = double.NaN;
    private double _lastAdvanceSw;
    private int _repeatsInRow;

    /// <summary>True while the sim clock is used; false after a fallback to the stopwatch.</summary>
    public bool UsesSimClock { get; private set; } = true;
    /// <summary>Seconds since the first message.</summary>
    public double Now { get; private set; }
    public int Repeats { get; private set; }
    public int Jumps { get; private set; }
    public string? FallbackReason { get; private set; }

    /// <summary>Returns false for a repeat that should not be played.</summary>
    public bool Next(double stopwatch, double simClock)
    {
        if (double.IsNaN(_lastSw))
        {
            (_lastSw, _lastSim, _lastAdvanceSw) = (stopwatch, simClock, stopwatch);
            Now = 0;
            return true;
        }
        var dsw = Math.Max(0, stopwatch - _lastSw);
        _lastSw = stopwatch;
        if (!UsesSimClock)
        {
            Now += dsw;
            return true;
        }
        var d = simClock - _lastSim;
        if (d > 0 && d <= MaxStepS && double.IsFinite(d))
        {
            Now += d;
            (_lastSim, _lastAdvanceSw, _repeatsInRow) = (simClock, stopwatch, 0);
            return true;
        }
        if (d == 0)
        {
            _repeatsInRow++;
            var stalled = stopwatch - _lastAdvanceSw;
            if (_repeatsInRow < MaxRepeats && stalled <= StallS)
            {
                Repeats++;
                return false;
            }
            // The sim clock stands still (a stopped sim delivers no frames at all, so this is rare).
            FallbackReason = $"sim clock stopped moving ({_repeatsInRow} repeats, {stalled * 1000:0} ms); stopwatch from {Now + stalled:0.0} s";
            UsesSimClock = false;
            Now += stalled;
            return true;
        }
        Jumps++;
        Now += dsw;
        (_lastSim, _lastAdvanceSw, _repeatsInRow) = (simClock, stopwatch, 0);
        return true;
    }
}
