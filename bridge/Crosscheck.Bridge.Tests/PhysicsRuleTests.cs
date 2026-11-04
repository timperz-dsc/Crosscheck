using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Follow;
using Crosscheck.Bridge.Logging;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// 0.6.1: the follower leaves elevator, aileron and rudder alone while the PF is on the ground below 40 kt (so the PM
/// can do the flight control check), starts writing them at 40 kt or in the air, and keeps them on for a hold time.
/// Position, attitude and the freezes are not affected.
/// </summary>
public sealed class FollowerSurfaceTests : IAsyncLifetime
{
    private static readonly PhysicsFrame Base = Synthetic.Circuit()[0];
    private readonly FakePhysicsLink _link = new();
    private readonly string _logs = Repo.TempDir("crosscheck-surface-logs");
    private readonly BridgeLog _log;
    private readonly PhysicsSync _sync;
    private readonly CancellationTokenSource _feed = new();
    private volatile bool _pfOnGround = true;
    private double _pfGs = 10;

    public FollowerSurfaceTests()
    {
        _log = new BridgeLog(_logs);
        _sync = new PhysicsSync(_link, _log, new EventHub(), new PhysicsOptions { StatsEveryS = 0, SurfaceHoldS = 0.6 });
    }

    public Task InitializeAsync()
    {
        _link.Start([]);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _feed.Cancel();
        await _sync.DisposeAsync();
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    private void SetPf(bool onGround, double gs)
    {
        Volatile.Write(ref _pfGs, gs);
        _pfOnGround = onGround;
    }

    /// <summary>The PF's side: 30 frames per second of its own clock, with the ground state the test sets.</summary>
    private async Task Follow()
    {
        _sync.StartSession((_, _) => { });
        _sync.SetRole(isPf: false);
        var ct = _feed.Token;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long n = 0;
            while (!ct.IsCancellationRequested)
            {
                var t = sw.Elapsed.TotalSeconds;
                var f = Base with { T = t, OnGround = _pfOnGround, GroundSpeedKt = Volatile.Read(ref _pfGs), Elevator = 0.4, Aileron = -0.3, Rudder = 0.2 };
                _sync.OnStream(++n, StreamFields.ToWire(f, null));
                await Task.Delay(33);
            }
        }, ct);
        await Wait.Until(() => _sync.Mode == FollowMode.Following && _link.Flags.AllOn, "following and frozen");
    }

    private bool? LastSurfaces()
    {
        lock (_link) return _link.SurfacesWritten.Count == 0 ? null : _link.SurfacesWritten[^1];
    }

    /// <summary>follow_surfaces events from the log file, in order: (on, cause).</summary>
    private List<(bool On, string Cause)> SurfaceEvents()
    {
        var list = new List<(bool, string)>();
        if (_log.Path is null || !File.Exists(_log.Path)) return list;
        using var fs = new FileStream(_log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        while (r.ReadLine() is { } line)
        {
            if (!line.Contains("\"follow_surfaces\"")) continue;
            var o = JsonNode.Parse(line)!.AsObject();
            list.Add((o["on"]!.GetValue<bool>(), o["cause"]!.GetValue<string>()));
        }
        return list;
    }

    [Fact]
    public async Task On_the_ground_below_40_kt_the_surfaces_are_not_written_but_position_and_freezes_are()
    {
        SetPf(onGround: true, gs: 10);
        await Follow();
        await Wait.Until(() => _link.PlacedCopy().Count > 10, "placements");
        Assert.True(_link.Flags.AllOn); // the freezes are unchanged
        lock (_link) Assert.All(_link.SurfacesWritten, w => Assert.False(w));
        Assert.All(_link.PlacedCopy(), p => Assert.Equal(Base.Lat, p.Frame.Lat, 6)); // still placed where the PF is
        var events = SurfaceEvents();
        Assert.Equal([(false, "engage")], events);
    }

    [Fact]
    public async Task At_40_kt_on_the_ground_the_surfaces_start()
    {
        SetPf(onGround: true, gs: 10);
        await Follow();
        await Wait.Until(() => LastSurfaces() == false, "off");
        SetPf(onGround: true, gs: 40);
        await Wait.Until(() => LastSurfaces() == true, "on");
        Assert.Equal([(false, "engage"), (true, "speed")], SurfaceEvents());
    }

    [Fact]
    public async Task In_the_air_the_surfaces_are_written_from_the_first_placement()
    {
        SetPf(onGround: false, gs: 5); // airborne: GS does not matter
        await Follow();
        await Wait.Until(() => _link.PlacedCopy().Count > 5, "placements");
        lock (_link) Assert.All(_link.SurfacesWritten, w => Assert.True(w));
        Assert.Equal([(true, "engage")], SurfaceEvents());
    }

    [Fact]
    public async Task Lifting_off_slowly_starts_the_surfaces_even_below_40_kt()
    {
        SetPf(onGround: true, gs: 10);
        await Follow();
        await Wait.Until(() => LastSurfaces() == false, "off");
        SetPf(onGround: false, gs: 10);
        await Wait.Until(() => LastSurfaces() == true, "on");
        Assert.Equal((true, "airborne"), SurfaceEvents()[^1]);
    }

    [Fact]
    public async Task Once_on_the_surfaces_stay_on_for_the_hold_time_then_stop_on_the_ground_below_40_kt()
    {
        SetPf(onGround: true, gs: 60);
        await Follow();
        await Wait.Until(() => LastSurfaces() == true, "on");
        SetPf(onGround: true, gs: 20); // slow again at once (e.g. an aborted takeoff)
        await Task.Delay(150);
        Assert.True(LastSurfaces()); // inside the 0.6 s hold: no flicker
        await Wait.Until(() => LastSurfaces() == false, "off after the hold");
        Assert.Equal((false, "ground_slow"), SurfaceEvents()[^1]);
        // And on again at 40 kt, at once.
        SetPf(onGround: true, gs: 45);
        await Wait.Until(() => LastSurfaces() == true, "on again");
        Assert.Equal((true, "speed"), SurfaceEvents()[^1]);
    }

    [Fact]
    public async Task A_new_follow_decides_again_and_a_missing_ground_speed_counts_as_zero()
    {
        // A frame without gs_kt reads as 0 kt: on the ground that keeps the surfaces off.
        SetPf(onGround: true, gs: 0);
        await Follow();
        await Wait.Until(() => _link.PlacedCopy().Count > 5, "placements");
        lock (_link) Assert.All(_link.SurfacesWritten, w => Assert.False(w));
        // Becoming PF and then follower again starts with a fresh decision (event "engage").
        _sync.SetRole(isPf: true);
        _sync.SetRole(isPf: false);
        await Wait.Until(() => SurfaceEvents().Count(e => e.Cause == "engage") == 2, "second engage");
    }
}

/// <summary>0.6.1: the PF streams 30 frames per second on average whatever the sim's frame rate (it was 22 Hz at 45 fps).</summary>
public sealed class StreamRateTests
{
    private static int Sent(double fps, double seconds, Action<PhysicsSync, FakePhysicsLink>? between = null)
    {
        var link = new FakePhysicsLink(new FakePhysicsOptions { Manual = true });
        var logs = Repo.TempDir("crosscheck-rate-logs");
        var sync = new PhysicsSync(link, new BridgeLog(logs), new EventHub(), new PhysicsOptions { StatsEveryS = 0 });
        try
        {
            link.Start([]);
            var sent = 0;
            sync.StartSession((_, _) => sent++);
            sync.SetRole(isPf: true);
            var frames = (int)Math.Round(fps * seconds);
            for (var k = 0; k < frames; k++) link.PushFrame(Synthetic.Circuit()[0] with { T = k / fps });
            return sent;
        }
        finally
        {
            sync.DisposeAsync().AsTask().Wait();
            try { Directory.Delete(logs, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(45)]
    [InlineData(50)]
    [InlineData(60)]
    [InlineData(75)]
    [InlineData(90)]
    [InlineData(120)]
    public void A_sim_at_30_fps_or_more_gets_30_frames_per_second_whatever_its_frame_rate(double fps)
    {
        Assert.InRange(Sent(fps, 3), 89, 91);
    }

    [Fact]
    public void A_sim_slower_than_30_fps_sends_every_frame_and_never_more()
    {
        Assert.Equal(75, Sent(25, 3));
        Assert.Equal(60, Sent(20, 3));
    }

    [Fact]
    public void A_stalled_clock_starts_a_new_grid_and_never_sends_a_burst()
    {
        var link = new FakePhysicsLink(new FakePhysicsOptions { Manual = true });
        var logs = Repo.TempDir("crosscheck-rate-logs");
        var sync = new PhysicsSync(link, new BridgeLog(logs), new EventHub(), new PhysicsOptions { StatsEveryS = 0 });
        try
        {
            link.Start([]);
            var sent = 0;
            sync.StartSession((_, _) => sent++);
            sync.SetRole(isPf: true);
            var f = Synthetic.Circuit()[0];
            for (var k = 0; k < 60; k++) link.PushFrame(f with { T = k / 60.0 });
            var before = sent;
            // The sim hung for 5 s: the next two frames must send once, not catch up on 150 frames.
            link.PushFrame(f with { T = 6.0 });
            link.PushFrame(f with { T = 6.0 + 1 / 60.0 });
            Assert.Equal(before + 1, sent);
            for (var k = 2; k < 62; k++) link.PushFrame(f with { T = 6.0 + k / 60.0 });
            Assert.InRange(sent - before, 30, 32);
        }
        finally
        {
            sync.DisposeAsync().AsTask().Wait();
            try { Directory.Delete(logs, recursive: true); } catch (IOException) { }
        }
    }
}
