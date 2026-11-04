namespace Crosscheck.Follower;

/// <summary>Which velocities the player writes along with position and attitude.</summary>
public enum VelocityMode
{
    Body,
    World,
    None,
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
        Recording = recording.Cut(settings.FromS, settings.ToS);
        if (EndCheck.Problem(Recording.Frames, settings.AllowAirEnd) is { } problem) throw new ArgumentException(problem);
        _t0 = Recording.Frames[0].T;
        Packets = PfSource.Sends(Recording.Frames, settings.RateHz);
        Schedule = NetworkSimulator.Schedule(Packets, settings.Network);
        _arrivals = Schedule.Arrivals;
        Buffer = new PlayoutBuffer(settings.Playout);
        Metrics = new MetricsCollector(Recording.Frames, settings.Metrics with { WritesAdvance = settings.Method == Method.A }, settings.Playout.Tangents);
    }

    public PlaybackSettings Settings { get; }
    public Recording Recording { get; }
    public IReadOnlyList<Packet> Packets { get; }
    public NetworkSchedule Schedule { get; }
    public PlayoutBuffer Buffer { get; }
    public MetricsCollector Metrics { get; }
    public Frame Start => Recording.Frames[0];
    public bool Finished { get; private set; }
    public PlayoutResult Last { get; private set; }

    /// <summary>Recording-clock time for a session time (negative session time after start = settling).</summary>
    public double ClockAt(double sessionTime) => _t0 + (sessionTime - Settings.SettleS);

    public PlayoutResult OnSimFrame(double sessionTime, in Frame actual)
    {
        var now = ClockAt(sessionTime);
        while (_next < _arrivals.Count && _arrivals[_next].ArrivalTime <= now)
        {
            var a = _arrivals[_next++];
            Buffer.Push(a.Packet.Frame, a.ArrivalTime);
        }
        var target = Buffer.Sample(now);
        if (target.Status == PlayoutStatus.Empty) target = new PlayoutResult(Start with { T = now }, PlayoutStatus.Waiting, now, 0);
        Metrics.Add(now, target, actual);
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
