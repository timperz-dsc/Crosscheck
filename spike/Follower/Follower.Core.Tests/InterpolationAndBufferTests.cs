namespace Crosscheck.Follower.Tests;

public class InterpolatorTests
{
    [Fact]
    public void Exact_at_the_sample_points()
    {
        var a = Make.Moving(0, 50, 20);
        var b = Make.Moving(0.1, 50, 20) with { HeadingDeg = 95, BankDeg = -10 };
        var atA = Interpolator.Between(a, b, 0);
        var atB = Interpolator.Between(a, b, 0.1);
        Assert.True(Geo.Offset(a, atA).Length < 1e-6);
        Assert.True(Geo.Offset(b, atB).Length < 1e-6);
        Assert.True(Geo.AttitudeError(b, atB) < 1e-6);
    }

    [Fact]
    public void Constant_velocity_is_reproduced()
    {
        var a = Make.Moving(0, 80, -30, 5);
        var b = Make.Moving(0.2, 80, -30, 5);
        for (var t = 0.0; t <= 0.2; t += 0.01)
            Assert.True(Geo.Offset(Make.Moving(t, 80, -30, 5), Interpolator.Between(a, b, t)).Length < 1e-4);
    }

    [Fact]
    public void Constant_acceleration_is_reproduced()
    {
        // x = v0 t + a t²/2 along east, with matching velocities at both ends.
        static Frame At(double t)
        {
            const double v0 = 20, acc = 2;
            var f = Make.Moving(0, 0, 0);
            var (lat, lon, alt) = Geo.Move(f, new Enu(v0 * t + acc * t * t / 2, 0, 0));
            return f with { T = t, Lat = lat, Lon = lon, AltFt = alt, VelWorldX = (v0 + acc * t) / Geo.FtToM };
        }
        for (var t = 0.0; t <= 0.5; t += 0.05)
            Assert.True(Geo.Offset(At(t), Interpolator.Between(At(0), At(0.5), t)).Length < 1e-4);
    }

    [Fact]
    public void Finite_difference_tangents_need_no_velocities()
    {
        var frames = Make.Track(1, 10, 30, 0).Select(f => f with { VelWorldX = 0, VelWorldZ = 0 }).ToArray();
        var mid = Interpolator.Between(frames[4], frames[5], 0.43, Tangents.FiniteDifference, frames[3], frames[6]);
        Assert.True(Geo.Offset(Make.Moving(0.43, 30, 0), mid).Length < 1e-4);
        // With velocity tangents the zero velocities would put it in the wrong place along the segment.
        var wrong = Interpolator.Between(frames[4], frames[5], 0.43, Tangents.Velocity);
        Assert.True(Geo.Offset(Make.Moving(0.43, 30, 0), wrong).Length > 0.1);
    }

    [Fact]
    public void Heading_interpolates_across_north()
    {
        var a = Make.Moving(0, 0, 10) with { HeadingDeg = 358 };
        var b = Make.Moving(0.1, 0, 10) with { HeadingDeg = 4 };
        Assert.Equal(1, Interpolator.Between(a, b, 0.05).HeadingDeg, 6);
    }

    [Fact]
    public void Extrapolation_follows_velocity_and_turn_rate()
    {
        var prev = Make.Moving(0, 50, 0) with { HeadingDeg = 90 };
        var last = Make.Moving(0.1, 50, 0) with { HeadingDeg = 91 };
        var x = Interpolator.Extrapolate(last, prev, 0.3);
        Assert.True(Geo.Offset(Make.Moving(0.3, 50, 0), x).Length < 1e-3);
        Assert.Equal(93, x.HeadingDeg, 4); // 10 °/s kept turning
        Assert.Equal(91, Interpolator.Extrapolate(last, null, 0.3).HeadingDeg, 6);
    }

    [Fact]
    public void Extrapolation_keeps_a_turn_turning()
    {
        // Two frames of a 50 m/s turn; dead reckoning bends with the acceleration seen between them.
        var r = 1000.0;
        var w = 50 / r;
        Frame On(double t)
        {
            var f = Make.Moving(0, 0, 0);
            var (lat, lon, alt) = Geo.Move(f, new Enu(r * Math.Sin(w * t), r - r * Math.Cos(w * t), 0));
            return f with { T = t, Lat = lat, Lon = lon, AltFt = alt, VelWorldX = 50 * Math.Cos(w * t) / Geo.FtToM, VelWorldZ = 50 * Math.Sin(w * t) / Geo.FtToM };
        }
        var straight = Geo.Offset(On(0.35), Interpolator.Extrapolate(On(0.1), null, 0.35)).Length;
        var curved = Geo.Offset(On(0.35), Interpolator.Extrapolate(On(0.1), On(0.0667), 0.35)).Length;
        Assert.True(curved < straight / 5, $"curved {curved}, straight {straight}");
    }

    [Fact]
    public void Velocity_mismatch_detects_wrong_axes()
    {
        Assert.True(Interpolator.VelocityMismatch(Make.Circuit.Frames) < 0.01);
        var swapped = Make.Circuit.Frames.Select(f => f with { VelWorldX = f.VelWorldZ, VelWorldZ = f.VelWorldX }).ToArray();
        Assert.True(Interpolator.VelocityMismatch(swapped) > 0.3);
    }
}

public class PlayoutBufferTests
{
    private static PlayoutBuffer Buffer(double bufferMs = 100, double extrapMs = 250) =>
        new(new PlayoutOptions { BufferMs = bufferMs, MaxExtrapolationMs = extrapMs });

    [Fact]
    public void Empty_until_the_first_frame_then_waiting()
    {
        var b = Buffer();
        Assert.Equal(PlayoutStatus.Empty, b.Sample(0).Status);
        b.Push(Make.Moving(0, 10, 0), 0.03);
        Assert.Equal(PlayoutStatus.Waiting, b.Sample(0.05).Status);
    }

    [Fact]
    public void Without_jitter_the_output_is_the_truth_delayed_by_the_buffer()
    {
        var b = Buffer();
        const double latency = 0.035;
        var frames = Make.Track(5, 30, 60, 10);
        var next = 0;
        for (var now = 0.0; now < 5; now += 1 / 60.0)
        {
            while (next < frames.Count && frames[next].T + latency <= now) b.Push(frames[next], frames[next++].T + latency);
            var r = b.Sample(now);
            if (now < 0.5) continue;
            Assert.Equal(PlayoutStatus.Interpolated, r.Status);
            Assert.Equal(now - latency - 0.1, r.RenderTime, 9);
            Assert.True(Geo.Offset(Make.Moving(r.RenderTime, 60, 10), r.State).Length < 1e-3);
        }
    }

    [Fact]
    public void Late_frames_extrapolate_then_hold()
    {
        var b = Buffer(bufferMs: 100, extrapMs: 250);
        foreach (var f in Make.Track(1, 30, 40, 0)) b.Push(f, f.T + 0.03);
        // Newest frame T=1.0; render = now - 0.03 - 0.1.
        var on = b.Sample(1.1);
        Assert.Equal(PlayoutStatus.Interpolated, on.Status);
        var late = b.Sample(1.3);
        Assert.Equal(PlayoutStatus.Extrapolated, late.Status);
        Assert.Equal(0.17, late.LateBy, 6);
        Assert.True(Geo.Offset(Make.Moving(late.RenderTime, 40, 0), late.State).Length < 1e-3);
        var held = b.Sample(1.6);
        Assert.Equal(PlayoutStatus.Holding, held.Status);
        Assert.True(Geo.Offset(Make.Moving(1.25, 40, 0), held.State).Length < 1e-3); // stopped at the 250 ms limit
    }

    [Fact]
    public void Older_frames_are_dropped()
    {
        var b = Buffer();
        Assert.True(b.Push(Make.Moving(0.2, 1, 0), 0.25));
        Assert.False(b.Push(Make.Moving(0.1, 1, 0), 0.26));
        Assert.False(b.Push(Make.Moving(0.2, 1, 0), 0.27));
        Assert.Equal(2, b.DroppedOld);
        Assert.Equal(3, b.Received);
    }

    [Fact]
    public void Clock_offset_holds_still_under_jitter()
    {
        var b = Buffer();
        var rng = new Random(3);
        double? first = null;
        var minOffset = double.MaxValue;
        foreach (var f in Make.Track(10, 30, 20, 0))
        {
            var arrival = f.T + 0.035 + (rng.NextDouble() * 2 - 1) * 0.010;
            b.Push(f, arrival);
            b.Sample(arrival);
            if (f.T > 3)
            {
                first ??= b.ClockOffset;
                minOffset = Math.Min(minOffset, b.ClockOffset!.Value);
                Assert.True(Math.Abs(b.ClockOffset!.Value - first!.Value) < 0.003, $"offset moved to {b.ClockOffset}");
            }
        }
        Assert.InRange(first!.Value, 0.024, 0.030); // the fastest arrival, not the average
    }

    [Fact]
    public void Coming_back_from_a_late_stretch_is_smooth()
    {
        // 20 m/s, frames at 30 Hz, a 200 ms gap, then frames again: no jump on the way back.
        var b = new PlayoutBuffer(new PlayoutOptions { BufferMs = 50, MaxExtrapolationMs = 400, BlendMs = 200 });
        var frames = Make.Track(4, 30, 20, 0);
        Frame? last = null;
        double lastNow = 0;
        var maxStep = 0.0;
        for (var now = 0.0; now < 4; now += 1 / 60.0)
        {
            foreach (var f in frames.Where(f => f.T + 0.03 <= now && f.T + 0.03 > now - 1 / 60.0))
                if (f.T < 2 || f.T > 2.2) b.Push(f, f.T + 0.03);
            var r = b.Sample(now);
            if (r.Status == PlayoutStatus.Empty) continue;
            if (last is { } l && now > 0.5) maxStep = Math.Max(maxStep, Math.Abs(Geo.Offset(l, r.State).Length - 20 * (now - lastNow)));
            last = r.State;
            lastNow = now;
        }
        Assert.True(maxStep < 0.01, $"largest step off the steady speed: {maxStep} m");
    }
}

public class NetworkTests
{
    private static readonly IReadOnlyList<Packet> Packets = PfSource.Sends(Make.Track(20, 60, 30, 0), 30);

    [Fact]
    public void Sends_at_the_stream_rate_without_repeats()
    {
        var p30 = PfSource.Sends(Make.Track(10, 60, 1, 0), 30);
        var p20 = PfSource.Sends(Make.Track(10, 60, 1, 0), 20);
        Assert.Equal(301, p30.Count);
        Assert.Equal(201, p20.Count);
        Assert.Equal(p30.Count, p30.Select(p => p.Frame.T).Distinct().Count());
        Assert.All(p30, p => Assert.True(p.Frame.T <= p.SendTime + 1e-9));
        // A slow sim (20 fps) streamed at 30 Hz: one packet per sim frame at most.
        var slow = PfSource.Sends(Make.Track(10, 20, 1, 0), 30);
        Assert.Equal(201, slow.Count);
    }

    [Fact]
    public void Same_seed_same_schedule()
    {
        var o = new NetworkOptions { Seed = 7 };
        var a = NetworkSimulator.Schedule(Packets, o).Arrivals.Select(x => x.ArrivalTime);
        var b = NetworkSimulator.Schedule(Packets, o).Arrivals.Select(x => x.ArrivalTime);
        var c = NetworkSimulator.Schedule(Packets, o with { Seed = 8 }).Arrivals.Select(x => x.ArrivalTime);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Delays_stay_within_latency_and_jitter()
    {
        var s = NetworkSimulator.Schedule(Packets, new NetworkOptions { LatencyMs = 35, JitterMs = 10, AllowReorder = true });
        var delays = s.Arrivals.Select(a => (a.ArrivalTime - a.Packet.SendTime) * 1000).ToArray();
        Assert.All(delays, d => Assert.InRange(d, 25 - 1e-9, 45 + 1e-9));
        Assert.InRange(delays.Average(), 34, 36);
        Assert.True(delays.Min() < 26 && delays.Max() > 44);
    }

    [Fact]
    public void In_order_by_default_like_the_websocket()
    {
        var s = NetworkSimulator.Schedule(Packets, new NetworkOptions { JitterMs = 40 });
        Assert.Equal(0, s.Reordered);
        Assert.Equal(s.Arrivals.Select(a => a.Packet.Seq).Order(), s.Arrivals.Select(a => a.Packet.Seq));
        Assert.True(NetworkSimulator.Schedule(Packets, new NetworkOptions { JitterMs = 40, AllowReorder = true }).Reordered > 0);
    }

    [Fact]
    public void Loss_drops_about_that_share()
    {
        var s = NetworkSimulator.Schedule(Packets, new NetworkOptions { LossPct = 10 });
        Assert.Equal(Packets.Count, s.Sent);
        Assert.InRange(s.Lost, Packets.Count * 0.07, Packets.Count * 0.13);
        Assert.Equal(Packets.Count - s.Lost, s.Arrivals.Count);
    }

    [Fact]
    public void A_stall_holds_everything_behind_it()
    {
        var s = NetworkSimulator.Schedule(Packets, new NetworkOptions { JitterMs = 0, BurstPct = 2, BurstMs = 200 });
        Assert.True(s.Bursts > 0);
        var delays = s.Arrivals.Select(a => (a.ArrivalTime - a.Packet.SendTime) * 1000).ToArray();
        Assert.Contains(delays, d => d > 200);
        // During a stall several packets arrive at the same moment.
        Assert.Contains(s.Arrivals.GroupBy(a => Math.Round(a.ArrivalTime, 6)), g => g.Count() >= 4);
    }

    [Fact]
    public void Normal_jitter_is_centred()
    {
        var s = NetworkSimulator.Schedule(Packets, new NetworkOptions { Shape = JitterShape.Normal, JitterMs = 10, AllowReorder = true });
        var delays = s.Arrivals.Select(a => (a.ArrivalTime - a.Packet.SendTime) * 1000).ToArray();
        Assert.InRange(delays.Average(), 34, 36);
        Assert.True(delays.Max() > 45); // tails beyond ±1σ
    }
}
