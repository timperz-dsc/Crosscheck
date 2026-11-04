namespace Crosscheck.Follower;

/// <summary>Which clock times the frames.</summary>
public enum ClockSource
{
    /// <summary>Pick the best clock the recording has (see <see cref="TimeBase.Choose"/>).</summary>
    Auto,
    /// <summary>The PC's stopwatch when the SimConnect message was handled. Noisy: two sim frames handled in one go get almost the same time.</summary>
    Stopwatch,
    /// <summary>SIMULATION TIME.</summary>
    Sim,
    /// <summary>ABSOLUTE TIME.</summary>
    Abs,
}

/// <summary>How well one clock times a recording.</summary>
/// <param name="Available">The recording has this clock and it moves.</param>
/// <param name="Steps">Frame-to-frame steps looked at.</param>
/// <param name="Duplicates">Frames where position and this clock both repeat the frame before (the same sim frame delivered twice).</param>
/// <param name="NotAdvancing">Other frames where this clock does not move forward.</param>
/// <param name="Tiny">Steps under 3 ms (two frames handled in one go).</param>
/// <param name="SpeedErrP50">Median of |position change ÷ clock step − recorded velocity| ÷ speed, frame to frame, while moving.</param>
/// <param name="SpeedErrP95">The same, 95th percentile. The smoothness number: lower is better.</param>
public sealed record ClockQuality(
    ClockSource Source, bool Available, int Steps, int Duplicates, int NotAdvancing, int Tiny,
    double StepMedianMs, double StepP99Ms, double SpeedErrP50, double SpeedErrP95)
{
    /// <summary>Usable to play: available, and moves forward on nearly every frame that is not a duplicate.</summary>
    public bool Usable => Available && NotAdvancing <= Math.Max(2, Steps / 100);
}

/// <summary>What <see cref="TimeBase.Apply"/> did to a recording.</summary>
public sealed record ClockInfo(ClockSource Source, int DroppedDuplicates, int DroppedNotAdvancing, string Why);

/// <summary>
/// Chooses the clock for a recording and re-times the frames with it. Sim time should advance
/// exactly with the sim's frames; the stopwatch does not (CRO-17 research, section 6.8).
/// </summary>
public static class TimeBase
{
    /// <summary>Order of preference when clocks are equally good.</summary>
    public static readonly IReadOnlyList<ClockSource> Candidates = [ClockSource.Sim, ClockSource.Abs, ClockSource.Stopwatch];

    public static double Value(in Frame f, ClockSource s) => s switch
    {
        ClockSource.Stopwatch => f.T,
        ClockSource.Sim => f.SimTimeS,
        ClockSource.Abs => f.AbsTimeS,
        _ => throw new ArgumentOutOfRangeException(nameof(s), "resolve Auto first"),
    };

    /// <summary>Same place and attitude as the frame before (a repeated delivery when the clock repeats too).</summary>
    public static bool SamePlace(in Frame a, in Frame b) =>
        a.Lat == b.Lat && a.Lon == b.Lon && a.AltFt == b.AltFt && a.HeadingDeg == b.HeadingDeg && a.PitchDeg == b.PitchDeg && a.BankDeg == b.BankDeg;

    public static ClockQuality Quality(IReadOnlyList<Frame> frames, ClockSource source)
    {
        if (frames.Count < 2) return new ClockQuality(source, false, 0, 0, 0, 0, double.NaN, double.NaN, double.NaN, double.NaN);
        var first = Value(frames[0], source);
        var available = frames.Any(f => Value(f, source) != first) && frames.All(f => double.IsFinite(Value(f, source)));
        int duplicates = 0, notAdvancing = 0, tiny = 0;
        var steps = new List<double>();
        var errs = new List<double>();
        for (var i = 1; i < frames.Count; i++)
        {
            var (a, b) = (frames[i - 1], frames[i]);
            var dt = Value(b, source) - Value(a, source);
            if (dt <= 0)
            {
                if (dt == 0 && SamePlace(a, b)) duplicates++;
                else notAdvancing++;
                continue;
            }
            steps.Add(dt * 1000);
            if (dt < 0.003) tiny++;
            var v = (Geo.Velocity(a) + Geo.Velocity(b)) * 0.5;
            var speed = Math.Sqrt(v.E * v.E + v.N * v.N);
            if (speed < 2) continue; // below ~4 kt the noise dominates
            var fd = Geo.Offset(a, b) * (1 / dt);
            var d = fd - v;
            errs.Add(Math.Sqrt(d.E * d.E + d.N * d.N) / speed);
        }
        steps.Sort();
        errs.Sort();
        return new ClockQuality(source, available, frames.Count - 1, duplicates, notAdvancing, tiny,
            Pct(steps, 0.5), Pct(steps, 0.99), Pct(errs, 0.5), Pct(errs, 0.95));
    }

    /// <summary>
    /// The best usable clock: lowest frame-to-frame speed error (p95); a sim clock wins a near tie.
    /// Falls back to the stopwatch (older recordings have no sim clock).
    /// </summary>
    public static (ClockSource Source, string Why) Choose(IReadOnlyList<Frame> frames)
    {
        var q = Candidates.Select(c => Quality(frames, c)).Where(x => x.Usable).ToList();
        if (q.Count == 0 || q.All(x => x.Source == ClockSource.Stopwatch))
            return (ClockSource.Stopwatch, "the recording has no usable sim clock");
        // Rank by p95 speed error in steps of 1 percentage point, so a near tie goes to the earlier candidate.
        double Score(ClockQuality x) => double.IsNaN(x.SpeedErrP95) ? 0 : Math.Floor(x.SpeedErrP95 * 100);
        var best = q.OrderBy(Score).ThenBy(x => Candidates.ToList().IndexOf(x.Source)).First();
        var why = double.IsNaN(best.SpeedErrP95)
            ? "never moving; preferred clock"
            : $"smoothest: frame-to-frame speed error p95 {best.SpeedErrP95 * 100:0.0} %";
        return (best.Source, why);
    }

    /// <summary>
    /// Re-times the frames with <paramref name="source"/>: frame time = clock − clock at the first
    /// frame + the first frame's stopwatch time. Drops frames whose clock does not move forward
    /// (counted as duplicates when the position repeats too). The stopwatch leaves times as they are.
    /// </summary>
    public static (IReadOnlyList<Frame> Frames, ClockInfo Info) Apply(IReadOnlyList<Frame> frames, ClockSource source, string why = "")
    {
        if (source == ClockSource.Auto) throw new ArgumentException("resolve Auto first", nameof(source));
        if (frames.Count == 0) return (frames, new ClockInfo(source, 0, 0, why));
        var c0 = Value(frames[0], source);
        var t0 = frames[0].T;
        var kept = new List<Frame>(frames.Count) { frames[0] with { T = t0 } };
        int dup = 0, back = 0;
        for (var i = 1; i < frames.Count; i++)
        {
            var f = frames[i];
            var t = source == ClockSource.Stopwatch ? f.T : Value(f, source) - c0 + t0;
            var last = kept[^1];
            if (t <= last.T)
            {
                if (t == last.T && SamePlace(last, f)) dup++;
                else back++;
                continue;
            }
            kept.Add(f with { T = t });
        }
        return (kept, new ClockInfo(source, dup, back, why));
    }

    private static double Pct(List<double> sorted, double q) =>
        sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(q * (sorted.Count - 1)))];
}

/// <summary>
/// The player's clock: advances by the sim's own frame step (SIMULATION TIME or ABSOLUTE TIME of
/// the follower's sim), so each sim frame moves the follower by exactly one frame's worth. A
/// message whose sim time does not move is a repeat and is not played. Falls back to the
/// stopwatch for the rest of the run if the sim clock stops moving, and bridges a jump in the
/// sim clock with the stopwatch.
/// </summary>
public sealed class FrameClock(ClockSource source)
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

    public ClockSource Source { get; private set; } = source == ClockSource.Auto ? throw new ArgumentException("resolve Auto first") : source;
    /// <summary>Seconds since the first message.</summary>
    public double Now { get; private set; }
    /// <summary>Messages skipped because the sim clock did not move.</summary>
    public int Repeats { get; private set; }
    /// <summary>Sim clock jumps bridged with the stopwatch.</summary>
    public int Jumps { get; private set; }
    /// <summary>Why the clock fell back to the stopwatch, or null.</summary>
    public string? FallbackReason { get; private set; }

    /// <summary>
    /// One state message: the stopwatch now and the sim clock it carries (ignored for the
    /// stopwatch source). Returns false for a repeat that should not be played.
    /// </summary>
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
        if (Source == ClockSource.Stopwatch)
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
            FallbackReason = $"{Source} clock stopped moving ({_repeatsInRow} repeats, {stalled * 1000:0} ms); stopwatch from {Now + stalled:0.0} s";
            Source = ClockSource.Stopwatch;
            Now += stalled;
            return true;
        }
        // Backwards, not finite, or a jump: carry on with the stopwatch step.
        Jumps++;
        Now += dsw;
        (_lastSim, _lastAdvanceSw, _repeatsInRow) = (simClock, stopwatch, 0);
        return true;
    }
}
