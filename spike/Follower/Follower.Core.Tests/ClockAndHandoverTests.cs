using System.Globalization;

namespace Crosscheck.Follower.Tests;

/// <summary>Sim-time clock, re-timing and the player clock (research section 6.9, changes 1–3).</summary>
public class ClockTests
{
    /// <summary>
    /// A straight taxi at 8 m/s, 40 fps sim frames, timed like the real recorder: sim time is
    /// exact, the stopwatch puts every 20th frame 0.5 ms after the one before (handled in one go)
    /// and every 37th frame is delivered twice.
    /// </summary>
    private static List<Frame> NoisyTaxi(double seconds = 30)
    {
        var frames = new List<Frame>();
        var sw = 0.0;
        var n = (int)(seconds * 40);
        for (var i = 0; i <= n; i++)
        {
            var simT = i / 40.0;
            var f = Make.Moving(simT, 8, 0) with { OnGround = true, SimTimeS = 1000 + simT, AbsTimeS = Synthetic.AbsTimeBase + simT };
            // The stopwatch lags or catches up: a frame handled late, then the next one at once.
            sw = i % 20 == 1 ? sw + 0.0005 : i == 0 ? 0 : simT + 0.004 * ((i * 7919) % 5) / 4;
            if (frames.Count > 0 && sw <= frames[^1].T) sw = frames[^1].T + 0.0005;
            frames.Add(f with { T = sw });
            if (i % 37 == 5) frames.Add(f with { T = sw + 0.002 }); // same sim frame again
        }
        return frames;
    }

    [Fact]
    public void Sim_time_is_chosen_over_a_noisy_stopwatch()
    {
        var frames = NoisyTaxi();
        var sw = TimeBase.Quality(frames, ClockSource.Stopwatch);
        var sim = TimeBase.Quality(frames, ClockSource.Sim);
        Assert.True(sim.Usable);
        Assert.True(sim.SpeedErrP95 < 0.01, $"sim p95 {sim.SpeedErrP95}");
        Assert.True(sw.SpeedErrP95 > 0.2, $"stopwatch p95 {sw.SpeedErrP95}");
        Assert.True(sim.Duplicates > 20);
        Assert.Equal(0, sim.NotAdvancing);
        Assert.Equal(ClockSource.Sim, TimeBase.Choose(frames).Source);
    }

    [Fact]
    public void A_recording_without_sim_time_keeps_the_stopwatch()
    {
        var frames = NoisyTaxi().Select(f => f with { SimTimeS = 0, AbsTimeS = 0 }).ToList();
        Assert.False(TimeBase.Quality(frames, ClockSource.Sim).Available);
        var (source, why) = TimeBase.Choose(frames);
        Assert.Equal(ClockSource.Stopwatch, source);
        Assert.Contains("no usable sim clock", why);
        Assert.Throws<ArgumentException>(() => new PlaybackSession(new Recording(new RecordingHeader(default, "", ""), StopAtEnd(frames), false), new PlaybackSettings { Clock = ClockSource.Sim }));
    }

    [Fact]
    public void Apply_drops_repeated_frames_and_retimes_on_the_sim_clock()
    {
        var frames = NoisyTaxi();
        var (timed, info) = TimeBase.Apply(frames, ClockSource.Sim);
        Assert.Equal(ClockSource.Sim, info.Source);
        Assert.Equal(frames.Count - timed.Count, info.DroppedDuplicates);
        Assert.Equal(0, info.DroppedNotAdvancing);
        Assert.Equal(frames[0].T, timed[0].T);
        // Every step is exactly one sim frame now.
        Assert.All(timed.Zip(timed.Skip(1)), p => Assert.Equal(0.025, p.Second.T - p.First.T, 9));
        // The stopwatch leaves times alone and drops nothing (it always moves on).
        var (sw, swInfo) = TimeBase.Apply(frames, ClockSource.Stopwatch);
        Assert.Equal(frames.Count, sw.Count);
        Assert.Equal(0, swInfo.DroppedDuplicates + swInfo.DroppedNotAdvancing);
        Assert.Throws<ArgumentException>(() => TimeBase.Apply(frames, ClockSource.Auto));
    }

    [Fact]
    public void A_sim_clock_that_repeats_while_the_aircraft_moves_is_not_a_duplicate()
    {
        var frames = Make.Track(2, 40, 8, 0).Select(f => f with { SimTimeS = f.T }).ToList();
        frames[10] = frames[10] with { SimTimeS = frames[9].SimTimeS }; // clock stuck for one moving frame
        var (timed, info) = TimeBase.Apply(frames, ClockSource.Sim);
        Assert.Equal(1, info.DroppedNotAdvancing);
        Assert.Equal(0, info.DroppedDuplicates);
        Assert.Equal(frames.Count - 1, timed.Count);
    }

    [Fact]
    public void Velocity_check_over_half_a_second_ignores_stopwatch_noise()
    {
        var frames = NoisyTaxi();
        // Frame to frame the stopwatch looks like 20 %+ speed error; over 0.5 s the axes check out.
        Assert.True(Interpolator.VelocityMismatch(frames) < 0.03, $"{Interpolator.VelocityMismatch(frames)}");
        var swapped = frames.Select(f => f with { VelWorldX = f.VelWorldZ, VelWorldZ = f.VelWorldX }).ToList();
        Assert.True(Interpolator.VelocityMismatch(swapped) > 0.3);
    }

    [Fact]
    public void Frame_clock_follows_the_sim_and_skips_repeats()
    {
        var c = new FrameClock(ClockSource.Sim);
        Assert.True(c.Next(10.000, 500.000));
        Assert.Equal(0, c.Now);
        Assert.True(c.Next(10.030, 500.025)); // the stopwatch says 30 ms, the sim 25 ms: the sim wins
        Assert.Equal(0.025, c.Now, 9);
        Assert.False(c.Next(10.031, 500.025)); // the same sim frame again
        Assert.Equal(1, c.Repeats);
        Assert.True(c.Next(10.032, 500.050)); // handled at once, but it is the next sim frame
        Assert.Equal(0.050, c.Now, 9);
        Assert.Equal(ClockSource.Sim, c.Source);
    }

    [Fact]
    public void Frame_clock_falls_back_to_the_stopwatch_when_the_sim_clock_stops()
    {
        var c = new FrameClock(ClockSource.Sim);
        c.Next(0, 100);
        c.Next(0.025, 100.025);
        Assert.False(c.Next(0.050, 100.025));
        Assert.False(c.Next(0.075, 100.025));
        Assert.True(c.Next(0.100, 100.025)); // third repeat in a row: stopped
        Assert.Equal(ClockSource.Stopwatch, c.Source);
        Assert.NotNull(c.FallbackReason);
        Assert.Equal(0.100, c.Now, 9);
        Assert.True(c.Next(0.125, 100.025));
        Assert.Equal(0.125, c.Now, 9);
    }

    [Fact]
    public void Frame_clock_bridges_a_jump_with_the_stopwatch()
    {
        var c = new FrameClock(ClockSource.Abs);
        c.Next(0, 6.39e10);
        c.Next(0.025, 6.39e10 + 0.025);
        Assert.True(c.Next(0.050, 6.39e10 + 3600)); // the sim clock jumped an hour
        Assert.Equal(1, c.Jumps);
        Assert.Equal(0.050, c.Now, 4); // ABSOLUTE TIME near 6.4e10 s resolves to ~8 µs as a double
        Assert.True(c.Next(0.075, 6.39e10 + 3600.025));
        Assert.Equal(0.075, c.Now, 4);
        Assert.Throws<ArgumentException>(() => new FrameClock(ClockSource.Auto));
    }

    [Fact]
    public void Synthetic_circuit_plays_on_sim_time_like_before()
    {
        var session = new PlaybackSession(Make.Circuit, new PlaybackSettings());
        Assert.Equal(ClockSource.Sim, session.Clock.Source);
        Assert.Equal(0, session.Clock.DroppedDuplicates + session.Clock.DroppedNotAdvancing);
        Assert.Equal(Make.Circuit.Frames.Count, session.Recording.Frames.Count);
        Assert.Equal(Make.Circuit.Frames[100].T, session.Recording.Frames[100].T, 9);
    }

    [Fact]
    public void New_fields_survive_a_file_round_trip()
    {
        var path = Path.GetTempFileName();
        try
        {
            var frames = Make.Circuit.Frames.Take(200).ToArray();
            RecordingFormat.Save(path, Make.Circuit.Header, frames);
            var back = RecordingFormat.Load(path);
            Assert.True(back.Has("sim_time") && back.Has("abs_time") && back.Has("g_force"));
            Assert.Equal(frames[150].SimTimeS, back.Frames[150].SimTimeS);
            Assert.Equal(frames[150].AbsTimeS, back.Frames[150].AbsTimeS);
            Assert.Equal((float)frames[150].GForce, (float)back.Frames[150].GForce);
            Assert.False((Make.Circuit with { FieldNames = ["t", "lat"] }).Has("g_force"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The taxi frames, slowed to a stop at the end so the player accepts them.</summary>
    private static List<Frame> StopAtEnd(List<Frame> frames)
    {
        var last = frames[^1];
        frames.Add(last with { T = last.T + 0.05, SimTimeS = last.SimTimeS + 0.05, AbsTimeS = last.AbsTimeS + 0.05, VelWorldX = 0, VelWorldZ = 0, GroundSpeedKt = 0 });
        return frames;
    }
}

/// <summary>Defaults and options (changes 3 and 5).</summary>
public class OptionTests
{
    private static PlaybackSettings Parse(params string[] argv)
    {
        var a = new Args(argv, PlaybackArgs.Flags);
        var s = PlaybackArgs.Parse(a);
        a.EnsureAllUsed();
        return s;
    }

    [Fact]
    public void Method_B_writes_the_mixed_set_by_default()
    {
        Assert.Equal(VelocityMode.Mixed, Parse("--method", "B").Velocities);
        Assert.Equal(VelocityMode.Body, Parse("--method", "A").Velocities);
        Assert.Equal(VelocityMode.Body, Parse().Velocities);
        Assert.Equal(VelocityMode.World, Parse("--method", "B", "--velocities", "world").Velocities);
        Assert.Equal(VelocityMode.Mixed, Parse("--method", "A", "--velocities", "mixed").Velocities);
    }

    [Fact]
    public void Velocity_tangents_and_auto_clock_are_the_defaults()
    {
        var s = Parse();
        Assert.Equal(Tangents.Velocity, s.Playout.Tangents);
        Assert.Equal(ClockSource.Auto, s.Clock);
        Assert.Null(s.HandoverTestS);
        Assert.Equal(ClockSource.Sim, Parse("--clock", "sim").Clock);
        Assert.Equal(ClockSource.Abs, Parse("--clock", "ABS").Clock);
        Assert.Equal(42.5, Parse("--method", "B", "--handover-test", "42.5").HandoverTestS);
        Assert.Throws<UsageException>(() => Parse("--clock", "gps"));
        Assert.Throws<UsageException>(() => Parse("--handover-test", "-1"));
    }

    [Fact]
    public void Help_no_longer_recommends_fd()
    {
        Assert.Contains("for comparison only", PlaybackArgs.Help);
        Assert.Contains("--handover-test", PlaybackArgs.Help);
        Assert.Contains("mixed", PlaybackArgs.Help);
    }
}

/// <summary>Ground speed in the metrics (change 4).</summary>
public class GroundSpeedMetricTests
{
    [Fact]
    public void Ground_speed_of_follower_and_recording_are_in_the_rows()
    {
        var session = OfflineRun.Run(Make.Circuit, new PlaybackSettings { Method = Method.B, FromS = 0, ToS = 200, AllowAirEnd = true });
        var taxi = session.Metrics.Summarize().Phases.Single(p => p.Phase == "Taxi");
        Assert.True(taxi.GsErrKt.P95 < 0.5, $"{taxi.GsErrKt.P95}");
        Assert.True(taxi.GsJumpKt.P95 < 0.2, $"{taxi.GsJumpKt.P95}");
        Assert.Contains(session.Metrics.Rows, r => r.GsKt > 10);
    }

    [Fact]
    public void A_follower_speed_that_jumps_shows_as_gs_jump()
    {
        var truth = Make.Track(10, 40, 8, 0).Select(f => f with { OnGround = true }).ToArray();
        var m = new MetricsCollector(truth, new MetricsOptions { WritesAdvance = false });
        for (var i = 1; i < truth.Length; i++)
        {
            var actual = truth[i - 1] with { GroundSpeedKt = i % 2 == 0 ? 10 : 20 }; // 10 kt jumps
            m.Add(truth[i].T, new PlayoutResult(truth[i], PlayoutStatus.Interpolated, truth[i].T, 0), actual, wallMs: 25);
        }
        var s = m.Summarize().All;
        Assert.True(s.GsJumpKt.P95 > 9, $"{s.GsJumpKt.P95}");
        Assert.Equal(25, s.WallMs.P95);
    }

    [Fact]
    public void Csv_and_report_carry_the_new_columns()
    {
        var session = OfflineRun.Run(Make.Circuit, new PlaybackSettings { FromS = 0, ToS = 120, AllowAirEnd = true });
        var info = Report.Build(session, "offline", "synth.cxrec", DateTime.UtcNow, "normal end", false);
        var w = new StringWriter();
        Report.WriteCsv(w, session.Metrics.Rows);
        var header = w.ToString().Split('\n')[0].Trim();
        Assert.EndsWith(",gs_kt,truth_gs_kt,gs_jump_kt,pc_ms", header);
        Assert.Equal(header.Split(',').Length, w.ToString().Split('\n')[1].Trim().Split(',').Length);
        var md = Report.Markdown(info);
        Assert.Contains("GS jump p95/max kt", md);
        Assert.Contains("Clock: Sim", md);
        Assert.DoesNotContain("--tangents fd", md);
        Assert.Contains("GS jump p95 kt", Report.Compare([info]));
    }
}

/// <summary>Handover test (change 6).</summary>
public class HandoverTests
{
    private static double TaxiPoint()
    {
        var c = HandoverTest.Candidates(Make.Circuit.Frames);
        Assert.NotEmpty(c);
        return (c[0].FromS + c[0].ToS) / 2;
    }

    [Fact]
    public void The_synthetic_circuit_has_taxi_stretches_and_only_they_are_accepted()
    {
        var frames = Make.Circuit.Frames;
        var at = TaxiPoint();
        Assert.Null(HandoverTest.Problem(frames, at, Method.B));
        Assert.Null(HandoverTest.Problem(frames, at, Method.C));
        Assert.Contains("method B or C", HandoverTest.Problem(frames, at, Method.A));
        Assert.Contains("taxiing", HandoverTest.Problem(frames, 5, Method.B)); // parked
        var air = frames.First(f => !f.OnGround && f.AglFt > 500).T - frames[0].T;
        Assert.Contains("in the air", HandoverTest.Problem(frames, air, Method.B));
        Assert.Contains("go on for", HandoverTest.Problem(frames, frames[^1].T - frames[0].T - 1, Method.B));
        Assert.All(HandoverTest.Candidates(frames), c => Assert.True(c.ToS - c.FromS >= 10 && c.MeanKt is >= 8 and <= 35));
    }

    [Fact]
    public void Session_refuses_a_bad_handover_point_and_signals_a_good_one()
    {
        var at = TaxiPoint();
        Assert.Throws<ArgumentException>(() => new PlaybackSession(Make.Circuit, new PlaybackSettings { Method = Method.B, HandoverTestS = 5 }));
        Assert.Throws<ArgumentException>(() => new PlaybackSession(Make.Circuit, new PlaybackSettings { Method = Method.A, HandoverTestS = at }));

        // A cut that ends while taxiing is fine with a handover test: the run ends after the release.
        var settings = new PlaybackSettings { Method = Method.B, HandoverTestS = at, ToS = at + 8, SettleS = 0 };
        var session = new PlaybackSession(Make.Circuit, settings);
        Assert.Throws<ArgumentException>(() => new PlaybackSession(Make.Circuit, settings with { HandoverTestS = null }));

        var due = double.NaN;
        var actual = session.Start;
        for (var t = 0.0; t < at + 8 && double.IsNaN(due); t += 1 / 60.0)
        {
            var target = session.OnSimFrame(t, actual);
            if (session.HandoverDue) due = target.RenderTime;
            actual = target.State;
        }
        Assert.False(double.IsNaN(due));
        Assert.InRange(due - session.HandoverAt!.Value, 0, 0.02);
    }

    [Fact]
    public void Log_measures_speed_heading_and_jumps_after_the_release()
    {
        var truth = Make.Track(20, 40, 0, 7.7).Select(f => f with { OnGround = true }).ToArray(); // 15 kt north
        var log = new HandoverLog(truth, 5, 5);
        var i = 0;
        for (var t = 0.0; !log.Done; t += 0.025, i++)
        {
            // The follower keeps rolling but slows down, and jumps 3 m sideways once.
            var slow = Math.Max(0, 7.7 - 1.0 * t);
            var f = Make.Moving(5 + t, 0, slow) with { OnGround = true };
            var (lat, lon, alt) = Geo.Move(truth[200], new Enu(i >= 80 ? 3 : 0, 7.7 * t - 0.5 * Math.Min(t, 7.7) * t, 0));
            log.Add(10 + t, f with { Lat = lat, Lon = lon, AltFt = alt, GroundSpeedKt = slow / Geo.KtToMs });
        }
        var s = log.Summarize()!;
        Assert.Equal(5, s.AtS);
        Assert.InRange(s.LoggedS, 5, 5.03);
        Assert.Equal(15, s.GsAtReleaseKt, 0);
        Assert.True(s.GsAtEndKt < s.GsAfter1sKt && s.GsAfter1sKt < s.GsAtReleaseKt);
        Assert.Equal(15, s.TruthGsAtEndKt, 0);
        Assert.InRange(s.MaxJumpM, 2.7, 3.1);
        Assert.False(s.LeftTheGround);
        var w = new StringWriter();
        log.WriteCsv(w);
        Assert.Equal(log.Rows.Count + 1, w.ToString().Trim().Split('\n').Length);
    }

    [Fact]
    public void Report_shows_the_handover_section()
    {
        var at = TaxiPoint();
        var session = new PlaybackSession(Make.Circuit, new PlaybackSettings { Method = Method.B, HandoverTestS = at });
        var summary = new HandoverSummary { AtS = at, Frames = 200, LoggedS = 5, GsAtReleaseKt = 15, GsAtEndKt = 14 };
        var info = Report.Build(session, "sim", "c.cxrec", DateTime.UtcNow, "handover test", false, handover: summary);
        var md = Report.Markdown(info);
        Assert.Contains($"## Handover test at {at.ToString("0.0", CultureInfo.InvariantCulture)} s", md);
        Assert.Contains("handover.csv", md);
        Assert.Contains($"handover {at.ToString("0.#", CultureInfo.InvariantCulture)} s", Report.RunLabel(info));
    }
}
