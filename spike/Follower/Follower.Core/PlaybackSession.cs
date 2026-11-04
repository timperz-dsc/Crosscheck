namespace Crosscheck.Follower;

/// <summary>Which velocities the player writes along with position and attitude.</summary>
public enum VelocityMode
{
    /// <summary>VELOCITY BODY X/Y/Z and the body rotation rates.</summary>
    Body,
    /// <summary>VELOCITY WORLD X/Y/Z and the body rotation rates.</summary>
    World,
    None,
    /// <summary>
    /// The set YourControls and FS Copilot write: VELOCITY BODY Y/Z, VELOCITY WORLD X/Z,
    /// VERTICAL SPEED and G FORCE (each of the last two only while the sim accepts it). No
    /// rotation rates. Default for method B.
    /// </summary>
    Mixed,
}

public sealed record PlaybackSettings
{
    public Method Method { get; init; } = Method.A;
    public double RateHz { get; init; } = 30;
    public NetworkOptions Network { get; init; } = new();
    public PlayoutOptions Playout { get; init; } = new();
    public MetricsOptions Metrics { get; init; } = new();
    /// <summary>Seconds the follower is held at the first frame before playback starts (lets the sim settle after the move).</summary>
    public double SettleS { get; init; } = 3;
    public bool AllowAirEnd { get; init; }
    public double? FromS { get; init; }
    public double? ToS { get; init; }
    public VelocityMode Velocities { get; init; } = VelocityMode.Body;
    /// <summary>Which clock times the recording and the player (default: the best one the recording has).</summary>
    public ClockSource Clock { get; init; } = ClockSource.Auto;
    /// <summary>Seconds into the recording at which a handover test releases the freezes (null: no test).</summary>
    public double? HandoverTestS { get; init; }

    /// <summary>Velocities written by default: the YourControls/FS Copilot set for B, body velocities otherwise.</summary>
    public static VelocityMode DefaultVelocities(Method m) => m == Method.B ? VelocityMode.Mixed : VelocityMode.Body;
}

/// <summary>Handover test: how long the follower is logged after the release.</summary>
public static class HandoverTest
{
    public const double LogS = 5;
    public const double MinKt = 5;
    public const double MaxKt = 40;

    /// <summary>Null if <paramref name="atS"/> (seconds into the recording) is a valid handover point; otherwise why not.</summary>
    public static string? Problem(IReadOnlyList<Frame> frames, double atS, Method method)
    {
        if (method == Method.A) return "--handover-test needs method B or C (method A has nothing to release)";
        if (frames.Count < 2) return "the recording is too short";
        var t = frames[0].T + atS;
        if (t + LogS > frames[^1].T) return $"--handover-test {atS:0.#}: the recording must go on for {LogS:0} s after that point";
        var f = Interpolator.Sample(frames, t);
        if (!f.OnGround || f.GroundSpeedKt < MinKt || f.GroundSpeedKt >= MaxKt)
            return $"--handover-test {atS:0.#}: the aircraft must be taxiing there (on the ground, {MinKt:0}–{MaxKt:0} kt); it is {(f.OnGround ? $"on the ground at {f.GroundSpeedKt:0} kt" : "in the air")}. `follower inspect` lists taxi stretches";
        return null;
    }

    /// <summary>
    /// Steady taxi stretches suitable for a handover test: on the ground, 8–35 kt without a
    /// break for at least 10 s. Times are seconds into the recording.
    /// </summary>
    public static IReadOnlyList<(double FromS, double ToS, double MeanKt)> Candidates(IReadOnlyList<Frame> frames)
    {
        var list = new List<(double, double, double)>();
        if (frames.Count < 2) return list;
        var t0 = frames[0].T;
        int? start = null;
        for (var i = 0; i <= frames.Count; i++)
        {
            var ok = i < frames.Count && frames[i].OnGround && frames[i].GroundSpeedKt is >= 8 and <= 35;
            if (ok)
            {
                start ??= i;
                continue;
            }
            if (start is { } s)
            {
                var end = i - 1;
                // Leave room for the log after the release.
                var to = Math.Min(frames[end].T, frames[^1].T - LogS) - t0;
                var from = frames[s].T - t0;
                if (to - from >= 10)
                {
                    var mean = frames.Skip(s).Take(end - s + 1).Average(f => f.GroundSpeedKt);
                    list.Add((from, to, mean));
                }
                start = null;
            }
        }
        return list;
    }
}

/// <summary>
/// One playback: the recording sent as a PF would send it, through the simulated network, into
/// the playout buffer, with metrics. Call <see cref="OnSimFrame"/> once per sim frame with the
/// time since the session started and the follower as read back; it returns what to write.
/// </summary>
public sealed class PlaybackSession
{
    private readonly IReadOnlyList<Arrival> _arrivals;
    private int _next;
    private readonly double _t0;

    public PlaybackSession(Recording recording, PlaybackSettings settings)
    {
        Settings = settings;
        var (source, why) = settings.Clock == ClockSource.Auto ? TimeBase.Choose(recording.Frames) : (settings.Clock, "chosen with --clock");
        if (source != ClockSource.Stopwatch && !TimeBase.Quality(recording.Frames, source).Usable)
            throw new ArgumentException($"--clock {source.ToString().ToLowerInvariant()}: the recording has no usable {source} clock (older recordings have none); use --clock auto");
        var (frames, info) = TimeBase.Apply(recording.Frames, source, why);
        Clock = info;
        var timed = recording with { Frames = frames };
        if (settings.HandoverTestS is { } h)
        {
            if (HandoverTest.Problem(timed.Frames, h, settings.Method) is { } hp) throw new ArgumentException(hp);
            HandoverAt = timed.Frames[0].T + h;
        }
        Recording = timed.Cut(settings.FromS, settings.ToS);
        if (Recording.Frames.Count < 2) throw new ArgumentException("--from/--to leave fewer than two frames");
        if (HandoverAt is { } at && (at < Recording.Frames[0].T || at + HandoverTest.LogS > Recording.Frames[^1].T))
            throw new ArgumentException("--handover-test must lie inside --from/--to, at least 5 s before the end");
        // A handover test ends on the ground after the release, so the end of the recording does not matter.
        if (HandoverAt is null && EndCheck.Problem(Recording.Frames, settings.AllowAirEnd) is { } problem) throw new ArgumentException(problem);
        _t0 = Recording.Frames[0].T;
        Packets = PfSource.Sends(Recording.Frames, settings.RateHz);
        Schedule = NetworkSimulator.Schedule(Packets, settings.Network);
        _arrivals = Schedule.Arrivals;
        Buffer = new PlayoutBuffer(settings.Playout);
        Metrics = new MetricsCollector(Recording.Frames, settings.Metrics with { WritesAdvance = settings.Method == Method.A }, settings.Playout.Tangents);
    }

    public PlaybackSettings Settings { get; }
    /// <summary>The recording, re-timed with the chosen clock and cut.</summary>
    public Recording Recording { get; }
    /// <summary>The clock the recording was timed with, and the frames it dropped.</summary>
    public ClockInfo Clock { get; }
    /// <summary>Recording time of the handover test release, or null.</summary>
    public double? HandoverAt { get; }
    /// <summary>True from the first played frame at or after <see cref="HandoverAt"/>.</summary>
    public bool HandoverDue => HandoverAt is { } at && Last.Status is not (PlayoutStatus.Empty or PlayoutStatus.Waiting) && Last.RenderTime >= at;
    public IReadOnlyList<Packet> Packets { get; }
    public NetworkSchedule Schedule { get; }
    public PlayoutBuffer Buffer { get; }
    public MetricsCollector Metrics { get; }
    public Frame Start => Recording.Frames[0];
    public bool Finished { get; private set; }
    public PlayoutResult Last { get; private set; }

    /// <summary>Recording-clock time for a session time (negative session time after start = settling).</summary>
    public double ClockAt(double sessionTime) => _t0 + (sessionTime - Settings.SettleS);

    /// <param name="sessionTime">Seconds since the first sim frame, on the player's clock (sim time when available).</param>
    /// <param name="actual">The follower as read back this frame.</param>
    /// <param name="wallMs">PC stopwatch since the last call, ms (report only).</param>
    public PlayoutResult OnSimFrame(double sessionTime, in Frame actual, double wallMs = double.NaN)
    {
        var now = ClockAt(sessionTime);
        while (_next < _arrivals.Count && _arrivals[_next].ArrivalTime <= now)
        {
            var a = _arrivals[_next++];
            Buffer.Push(a.Packet.Frame, a.ArrivalTime);
        }
        var target = Buffer.Sample(now);
        if (target.Status == PlayoutStatus.Empty) target = new PlayoutResult(Start with { T = now }, PlayoutStatus.Waiting, now, 0);
        Metrics.Add(now, target, actual, wallMs);
        if (target.RenderTime >= Recording.Frames[^1].T && target.Status != PlayoutStatus.Waiting) Finished = true;
        Last = target;
        return target;
    }
}

/// <summary>The whole pipeline against a perfect sim: each write is obeyed and moved on by its own velocity for one frame.</summary>
public static class OfflineRun
{
    public static PlaybackSession Run(Recording recording, PlaybackSettings settings, double simFps = 60)
    {
        var session = new PlaybackSession(recording, settings);
        var dt = 1 / simFps;
        var actual = session.Start;
        Frame? before = null;
        var limit = settings.SettleS + session.Recording.Duration + 30;
        for (var t = 0.0; t < limit && !session.Finished; t += dt)
        {
            var target = session.OnSimFrame(t, actual);
            var written = target.State;
            // Method A: the sim moves on from the written state for one frame (position by its velocity, attitude by its turn rate).
            actual = settings.Method == Method.A ? Interpolator.Extrapolate(written, before, written.T + dt) : written;
            before = written;
            // Nobody touches the follower's stick; its surfaces do what the recording's did (no reaction).
            var surfaces = Interpolator.Sample(session.Recording.Frames, actual.T);
            actual = actual with
            {
                YokeX = 0, YokeY = 0, RudderPedal = 0, FenixStickCaptPitch = 0, FenixStickCaptBank = 0, FenixStickFoPitch = 0, FenixStickFoBank = 0,
                ElevatorDeg = surfaces.ElevatorDeg, AileronDeg = surfaces.AileronDeg, RudderDeg = surfaces.RudderDeg, SpoilersLeft = surfaces.SpoilersLeft,
            };
        }
        return session;
    }
}
