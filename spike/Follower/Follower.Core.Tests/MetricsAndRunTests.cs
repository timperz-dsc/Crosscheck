namespace Crosscheck.Follower.Tests;

public class MetricsTests
{
    private static PlayoutResult Target(Frame f, PlayoutStatus status = PlayoutStatus.Interpolated) => new(f, status, f.T, 0);

    /// <summary>Feeds a follower that is exactly the recording (frozen-style: read-back = what was written).</summary>
    private static MetricsCollector Perfect(IReadOnlyList<Frame> truth, Func<int, Frame, Frame>? tamper = null, MetricsOptions? o = null)
    {
        var m = new MetricsCollector(truth, (o ?? new MetricsOptions()) with { WritesAdvance = false });
        for (var i = 0; i < truth.Count; i++)
        {
            var actual = i > 0 ? truth[i - 1] : truth[0];
            if (tamper is not null) actual = tamper(i, actual);
            m.Add(truth[i].T, Target(truth[i]), actual);
        }
        return m;
    }

    [Fact]
    public void A_perfect_follower_has_no_error_and_no_spikes()
    {
        var truth = Make.Circuit.Frames.Take(60 * 120).ToArray();
        var s = Perfect(truth).Summarize();
        Assert.True(s.All.PosErrM.Max < 1e-6);
        Assert.True(s.All.AttErrDeg.Max < 1e-3);
        Assert.True(s.All.AccelResid.Max < 1e-3);
        Assert.Equal(0, s.All.Spikes);
        Assert.Equal(0, s.All.Extrapolated + s.All.Holding);
    }

    [Fact]
    public void A_one_metre_jump_is_a_spike_in_its_phase()
    {
        var truth = Make.Circuit.Frames.Take(60 * 70).ToArray(); // parked, then taxiing
        var jumpAt = 60 * 50;
        var m = Perfect(truth, (i, f) =>
        {
            if (i < jumpAt) return f;
            var (lat, lon, alt) = Geo.Move(f, new Enu(0, 1, 0));
            return f with { Lat = lat, Lon = lon, AltFt = alt };
        });
        var s = m.Summarize();
        var taxi = s.Phases.Single(p => p.Phase == "Taxi");
        Assert.InRange(taxi.Spikes, 1, 3);
        Assert.Equal(0, s.Phases.Single(p => p.Phase == "Gate").Spikes);
        Assert.True(taxi.PosErrM.Max > 0.99);
        Assert.Equal(Phase.Taxi, m.Rows.First(r => r.Spike).Phase);
    }

    [Fact]
    public void Along_and_cross_track_are_separated()
    {
        var truth = Make.Track(5, 60, 50, 0); // flying east
        var m = new MetricsCollector(truth, new MetricsOptions { WritesAdvance = false });
        for (var i = 1; i < truth.Count; i++)
        {
            var (lat, lon, alt) = Geo.Move(truth[i - 1], new Enu(2, 3, -1)); // 2 m ahead, 3 m left, 1 m low
            m.Add(truth[i].T, Target(truth[i]), truth[i - 1] with { Lat = lat, Lon = lon, AltFt = alt });
        }
        var r = m.Rows[^1];
        Assert.Equal(2, r.AlongErrM, 3);
        Assert.Equal(3, r.CrossErrM, 3);
        Assert.Equal(-1, r.VertErrM, 3);
    }

    [Fact]
    public void Late_frames_are_counted_as_one_stretch()
    {
        var truth = Make.Track(4, 60, 10, 0);
        var m = new MetricsCollector(truth, new MetricsOptions { WritesAdvance = false });
        for (var i = 0; i < truth.Count; i++)
        {
            var status = i is >= 100 and < 130 ? PlayoutStatus.Extrapolated : i is >= 130 and < 136 ? PlayoutStatus.Holding : PlayoutStatus.Interpolated;
            m.Add(truth[i].T, new PlayoutResult(truth[i], status, truth[i].T, 0), i > 0 ? truth[i - 1] : truth[0]);
        }
        var s = m.Summarize();
        Assert.Equal(30, s.All.Extrapolated);
        Assert.Equal(6, s.All.Holding);
        Assert.Equal(1, s.LateStretches);
        Assert.Equal(36 / 60.0 * 1000, s.LongestLateMs, 0);
    }

    [Fact]
    public void Surfaces_moving_with_a_neutral_stick_are_the_aircraft_reacting()
    {
        var truth = Make.Track(3, 60, 50, 0);
        // Frames 60..89: follower stick neutral, elevator 5° off: reaction.
        // Frames 120..149: elevator 5° off but the stick is pulled: someone flew it, not a reaction.
        // Frames 150..: elevator 1° off: within the limit.
        var m = Perfect(truth, (i, f) => i switch
        {
            >= 60 and < 90 => f with { ElevatorDeg = f.ElevatorDeg + 5, PitchDeg = 2 },
            >= 120 and < 150 => f with { ElevatorDeg = f.ElevatorDeg + 5, FenixStickCaptPitch = 0.4 },
            >= 150 => f with { ElevatorDeg = f.ElevatorDeg + 1 },
            _ => f,
        });
        var s = m.Summarize();
        Assert.Equal(30, s.All.ReactionFrames);
        Assert.Equal(30, s.All.StickTouchedFrames);
        Assert.True(s.All.AttErrDeg.Max > 1.9);
        Assert.True(s.All.AttErrNoReactionDeg.Max < 1e-3); // the pitch error came only with the reaction
    }

    [Fact]
    public void Fenix_stick_scale_is_applied()
    {
        var m = new MetricsCollector([], new MetricsOptions { FenixStickScale = 16384 });
        Assert.True(m.Neutral(new Frame { FenixStickFoBank = 500 }));
        Assert.False(m.Neutral(new Frame { FenixStickFoBank = 2000 }));
        Assert.False(m.Neutral(new Frame { YokeY = 0.2 }));
    }

    [Theory]
    [InlineData(true, 0.5, Phase.Gate)]
    [InlineData(true, 15, Phase.Taxi)]
    [InlineData(true, 120, Phase.Runway)]
    [InlineData(false, 120, Phase.Flight)]
    public void Phases(bool onGround, double kt, Phase want) =>
        Assert.Equal(want, MetricsCollector.PhaseOf(new Frame { OnGround = onGround, GroundSpeedKt = kt }));
}

public class SyntheticTests
{
    [Fact]
    public void Is_a_full_circuit_that_ends_stopped_on_the_ground()
    {
        var f = Make.Circuit.Frames;
        Assert.True(f[0].OnGround && f[0].GroundSpeedKt == 0);
        Assert.True(f[^1].OnGround && f[^1].GroundSpeedKt == 0);
        Assert.Null(EndCheck.Problem(f, allowAirEnd: false));
        foreach (var ph in Enum.GetValues<Phase>())
            Assert.True(f.Count(x => MetricsCollector.PhaseOf(x) == ph) > 60 * 10, $"{ph} too short");
        Assert.InRange(f.Max(x => x.AglFt), 1400, 1600);
        Assert.InRange(f.Max(x => x.GroundSpeedKt), 170, 185);
        Assert.Equal(1, f.Zip(f.Skip(1)).Count(p => p.First.OnGround && !p.Second.OnGround)); // one takeoff
        Assert.Equal(1, f.Zip(f.Skip(1)).Count(p => !p.First.OnGround && p.Second.OnGround)); // one landing
    }

    [Fact]
    public void Velocities_agree_with_positions()
    {
        Assert.True(Interpolator.VelocityMismatch(Make.Circuit.Frames) < 0.001);
    }

    [Fact]
    public void Ground_end_check_refuses_a_cut_that_ends_in_the_air()
    {
        var rec = Make.Circuit;
        var airborne = rec.Frames.First(f => !f.OnGround && f.AglFt > 500).T;
        var cut = rec.Cut(null, airborne);
        Assert.Contains("air", EndCheck.Problem(cut.Frames, allowAirEnd: false));
        Assert.Null(EndCheck.Problem(cut.Frames, allowAirEnd: true));
        Assert.Throws<ArgumentException>(() => new PlaybackSession(rec, new PlaybackSettings { ToS = airborne }));
        _ = new PlaybackSession(rec, new PlaybackSettings { ToS = airborne, AllowAirEnd = true });
        var moving = rec.Cut(null, rec.Frames.First(f => f.OnGround && f.GroundSpeedKt > 10).T);
        Assert.Contains("moving", EndCheck.Problem(moving.Frames, allowAirEnd: false));
    }
}

public class EndToEndTests
{
    // Limits set from the first offline run of the synthetic circuit (pos ≈ 0.00 m, jitter p99 ≈ 0.45 m/s²),
    // with headroom; not tuned to whatever passes.
    [Theory]
    [InlineData(30)]
    [InlineData(20)]
    public void Default_network_follows_the_circuit_smoothly_in_every_phase(double rateHz)
    {
        var s = OfflineRun.Run(Make.Circuit, new PlaybackSettings { RateHz = rateHz });
        Assert.True(s.Finished);
        var sum = s.Metrics.Summarize();
        Assert.Equal(4, sum.Phases.Count);
        foreach (var p in sum.Phases)
        {
            Assert.True(p.PosErrM.Max < 0.05, $"{p.Phase}: pos max {p.PosErrM.Max}");
            Assert.True(p.AttErrDeg.Max < 0.05, $"{p.Phase}: att max {p.AttErrDeg.Max}");
            Assert.True(p.AccelResid.P99 < 1.0, $"{p.Phase}: jitter p99 {p.AccelResid.P99}");
            Assert.Equal(0, p.Spikes);
            Assert.Equal(0, p.Extrapolated + p.Holding);
            Assert.InRange(p.LagMeanMs, 120, 150); // fastest delay (25 ms) + buffer (100 ms) + frame
        }
        Assert.True(s.Last.State.OnGround);
        Assert.Equal(0, s.Schedule.Lost);
    }

    [Fact]
    public void Short_stalls_are_bridged_by_extrapolation()
    {
        var s = OfflineRun.Run(Make.Circuit, new PlaybackSettings { Network = new NetworkOptions { BurstPct = 1, BurstMs = 150 } });
        var sum = s.Metrics.Summarize();
        Assert.True(sum.LateStretches > 50);
        Assert.Equal(0, sum.All.Holding);
        Assert.True(sum.All.PosErrM.Max < 0.1, $"pos max {sum.All.PosErrM.Max}");
        Assert.True(sum.All.AccelResid.Max < 3, $"jitter max {sum.All.AccelResid.Max}");
    }

    [Fact]
    public void Stalls_longer_than_the_extrapolation_limit_hold_and_jump()
    {
        var network = new NetworkOptions { BurstPct = 1, BurstMs = 400 };
        var held = OfflineRun.Run(Make.Circuit, new PlaybackSettings { Network = network }).Metrics.Summarize();
        Assert.True(held.All.Holding > 0);
        Assert.True(held.All.Spikes > 100);
        // A longer limit covers them.
        var covered = OfflineRun.Run(Make.Circuit, new PlaybackSettings { Network = network, Playout = new PlayoutOptions { MaxExtrapolationMs = 600 } }).Metrics.Summarize();
        Assert.Equal(0, covered.All.Holding);
        Assert.True(covered.All.Spikes < held.All.Spikes / 10);
    }

    [Fact]
    public void Report_round_trips_and_compares()
    {
        var dir = Path.Combine(Path.GetTempPath(), "follower-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rec = Make.Circuit.Cut(null, 120); // parked and taxiing: ends moving
            var s = OfflineRun.Run(rec, new PlaybackSettings { AllowAirEnd = true, Method = Method.B });
            var info = Report.Build(s, "offline", "synthetic", new DateTime(2026, 11, 4, 1, 2, 3, DateTimeKind.Utc), "normal end", false);
            var run = Report.Write(dir, info, s.Metrics.Rows);
            Assert.True(File.Exists(Path.Combine(run, "report.md")));
            Assert.True(File.ReadAllLines(Path.Combine(run, "frames.csv")).Length == s.Metrics.Rows.Count + 1);
            var back = Report.Load(run);
            Assert.Equal(Method.B, back.Settings.Method);
            Assert.Equal(info.Summary.All.Frames, back.Summary.All.Frames);
            var table = Report.Compare([back, back]);
            Assert.Contains("offline B 30 Hz", table);
            Assert.Contains("| Taxi |", Report.Markdown(back));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
