using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Follow;
using Crosscheck.Bridge.Logging;

namespace Crosscheck.Bridge.Tests;

/// <summary>The playout buffer, interpolation and clock, ported from the CRO-17 spike.</summary>
public class PlayoutTests
{
    private static readonly IReadOnlyList<PhysicsFrame> Circuit = Synthetic.Circuit();

    [Fact]
    public void Steady_frames_are_interpolated_exactly()
    {
        var taxi = Synthetic.From(Circuit, 30);
        var buffer = new PlayoutBuffer(new PlayoutOptions());
        var errors = new List<double>();
        // 30 Hz frames arriving 35 ms after they were made; sampled at 60 Hz.
        var next = 0.0;
        for (var now = 0.0; now < 5; now += 1 / 60.0)
        {
            while (next <= now - 0.035)
            {
                buffer.Push(Interpolator.Sample(taxi, next), next + 0.035);
                next += 1 / 30.0;
            }
            var r = buffer.Sample(now);
            if (r.Status != PlayoutStatus.Interpolated) continue;
            errors.Add(Geo.Distance(r.State, Interpolator.Sample(taxi, r.RenderTime)));
        }
        Assert.True(errors.Count > 200);
        Assert.True(errors.Max() < 0.05, $"max error {errors.Max():0.000} m");
    }

    [Fact]
    public void Late_frames_extrapolate_for_600_ms_then_hold()
    {
        var air = Synthetic.From(Circuit, Synthetic.LiftoffT(Circuit) + 20);
        var buffer = new PlayoutBuffer(new PlayoutOptions());
        for (var t = 0.0; t <= 1; t += 1 / 30.0) buffer.Push(Interpolator.Sample(air, t), t + 0.03);
        Assert.Equal(PlayoutStatus.Interpolated, buffer.Sample(1.0).Status);
        var late = buffer.Sample(1.0 + 0.03 + 0.1 + 0.5); // render 0.5 s past the newest frame
        Assert.Equal(PlayoutStatus.Extrapolated, late.Status);
        Assert.True(Geo.Distance(late.State, Interpolator.Sample(air, late.RenderTime)) < 5);
        var held = buffer.Sample(1.0 + 0.03 + 0.1 + 1.0);
        Assert.Equal(PlayoutStatus.Holding, held.Status);
        var stillHeld = buffer.Sample(1.0 + 0.03 + 0.1 + 1.5);
        Assert.True(Geo.Distance(held.State, stillHeld.State) < 1e-6); // capped at 600 ms: no further dead reckoning
    }

    [Fact]
    public void An_older_frame_is_dropped()
    {
        var buffer = new PlayoutBuffer(new PlayoutOptions());
        Assert.True(buffer.Push(Circuit[10], 1));
        Assert.False(buffer.Push(Circuit[5], 1.01));
        Assert.Equal(1, buffer.DroppedOld);
    }

    [Fact]
    public void Frame_clock_skips_repeats_and_follows_sim_time()
    {
        var clock = new FrameClock();
        Assert.True(clock.Next(0, 100));
        Assert.True(clock.Next(0.004, 100.016)); // two frames handled in one go: sim time still steps 16 ms
        Assert.Equal(0.016, clock.Now, 6);
        Assert.False(clock.Next(0.02, 100.016)); // the same sim frame again
        Assert.True(clock.Next(0.03, 100.033));
        Assert.Equal(0.033, clock.Now, 6);
        Assert.Equal(1, clock.Repeats);
    }

    [Fact]
    public void Wire_format_round_trips_and_needs_the_core_fields()
    {
        var f = Circuit[5000];
        var wire = StreamFields.ToWire(f, new Dictionary<string, double> { ["A_FC_THROTTLE_LEFT_INPUT"] = 2.5 });
        Assert.True(StreamFields.TryFromWire(wire, out var back, out var levers));
        Assert.Equal(f.Lat, back.Lat);
        Assert.Equal(f.HeadingDeg, back.HeadingDeg);
        Assert.Equal(f.OnGround, back.OnGround);
        Assert.Equal(2.5, levers["a_fc_throttle_left_input"]);
        Assert.All(wire, p => Assert.Matches("^[a-z0-9_]{1,40}$", p.Key)); // the stream schema's names
        wire.Remove("lat");
        Assert.False(StreamFields.TryFromWire(wire, out _, out _));
    }
}

/// <summary>The follower's decisions (CRO-13 owner rules), with the in-memory physics link.</summary>
public sealed class PhysicsSyncTests : IAsyncLifetime
{
    private static readonly IReadOnlyList<PhysicsFrame> Circuit = Synthetic.Circuit();
    private readonly FakePhysicsLink _link = new();
    private readonly EventHub _hub = new();
    private readonly string _logs = Repo.TempDir("crosscheck-physics-logs");
    private PhysicsSync _sync = null!;
    private CancellationTokenSource _feed = new();

    public Task InitializeAsync()
    {
        _sync = new PhysicsSync(_link, new BridgeLog(_logs), _hub, new PhysicsOptions { StatsEveryS = 0 },
            new Dictionary<string, double> { ["A_FC_THROTTLE_LEFT_INPUT"] = 0.2 });
        _link.Start(["A_FC_THROTTLE_LEFT_INPUT"]);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _feed.Cancel();
        await _sync.DisposeAsync();
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    /// <summary>Plays the PF's side: the script from <paramref name="fromT"/> on, 30 frames per second, until stopped.</summary>
    private void Feed(IReadOnlyList<PhysicsFrame> script, Dictionary<string, double>? levers = null)
    {
        _feed.Cancel();
        _feed = new CancellationTokenSource();
        var ct = _feed.Token;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long n = 0;
            while (!ct.IsCancellationRequested)
            {
                var t = sw.Elapsed.TotalSeconds;
                _sync.OnStream(++n, StreamFields.ToWire(Interpolator.Sample(script, t) with { T = t }, levers));
                await Task.Delay(33);
            }
        }, ct);
    }

    private void StopFeed() => _feed.Cancel();

    private async Task Following(IReadOnlyList<PhysicsFrame> script)
    {
        _sync.StartSession((_, _) => { });
        _sync.SetRole(isPf: false);
        Feed(script);
        await Wait.Until(() => _sync.Mode == FollowMode.Following && _link.Flags.AllOn, "following and frozen");
    }

    [Fact]
    public async Task The_PF_streams_and_is_never_frozen_or_placed()
    {
        var sent = new List<JsonObject>();
        _sync.StartSession((_, data) => { lock (sent) sent.Add(data); });
        _sync.SetRole(isPf: true);
        _link.Fly(Circuit, 30);
        Feed(Circuit); // frames from someone else must change nothing on the PF
        await Task.Delay(700);
        lock (sent)
        {
            Assert.InRange(sent.Count, 15, 26); // about 30 per second, never more
            Assert.All(sent, d => Assert.True(StreamFields.TryFromWire(d, out _, out _)));
        }
        Assert.Equal(FollowMode.Streaming, _sync.Mode);
        Assert.Empty(_link.PlacedCopy());
        Assert.Empty(_link.FreezeEventsCopy());
        Assert.Equal(0, _link.GateRefused);
        Assert.False(_link.IsFollower);
    }

    [Fact]
    public async Task The_follower_places_every_sim_frame_and_freezes_after_the_first_placement()
    {
        await Following(Synthetic.From(Circuit, 30));
        var placed = _link.PlacedCopy();
        var events = _link.FreezeEventsCopy();
        Assert.Equal(FollowerSafety.Events, events.Select(e => e.Event));
        Assert.All(events, e => Assert.True(e.On));
        Assert.NotEmpty(placed);
        await Task.Delay(500);
        Assert.InRange(_link.PlacedCopy().Count - placed.Count, 20, 40); // one placement per sim frame (about 60 per second)
    }

    [Fact]
    public async Task Becoming_PF_releases_the_freezes_in_the_same_call()
    {
        await Following(Synthetic.From(Circuit, 30));
        _sync.SetRole(isPf: true);
        Assert.False(_link.Flags.AnyOn); // before SetRole returned
        Assert.False(_link.IsFollower);
        var placed = _link.PlacedCopy().Count;
        await Task.Delay(300);
        Assert.Equal(placed, _link.PlacedCopy().Count);
        Assert.Equal(FollowMode.Streaming, _sync.Mode);
    }

    [Fact]
    public async Task The_watchdog_sends_a_freeze_again_that_reads_off()
    {
        await Following(Synthetic.From(Circuit, 30));
        await Task.Delay(1100); // the grace time after the freezes went on
        _link.DropFreeze(FollowerSafety.FreezeAltitude);
        Assert.False(_link.Flags.AllOn);
        await Wait.Until(() => _link.Flags.AllOn, "freeze sent again");
        Assert.Contains((FollowerSafety.FreezeAltitude, true), _link.FreezeEventsCopy().Skip(3));
    }

    [Fact]
    public async Task Server_lost_in_the_air_holds_and_only_the_pilots_yes_releases()
    {
        var air = Synthetic.From(Circuit, Synthetic.LiftoffT(Circuit) + 30);
        await Following(air);
        StopFeed();
        _sync.OnServerConnected(false);
        Assert.Equal(FollowMode.PfLost, _sync.Mode);
        Assert.True(_sync.InAir);
        var placed = _link.PlacedCopy().Count;
        var at = _link.State;
        await Task.Delay(1500);
        Assert.True(_link.Flags.AllOn); // never released by itself
        Assert.Equal(placed, _link.PlacedCopy().Count); // holding: nothing more written
        Assert.Equal(0, Geo.Distance(at, _link.State), 6);
        Assert.Equal("pf_lost", _sync.StatusJson()["mode"]!.GetValue<string>());
        Assert.Equal("server", _sync.StatusJson()["pf_lost_cause"]!.GetValue<string>());

        Assert.True(_sync.TakeAircraftByPilot());
        Assert.False(_link.Flags.AnyOn);
        Assert.Equal(FollowMode.Released, _sync.Mode);
        // The pilot flies now: frames that arrive later are never placed, and a new role from the server is needed.
        Feed(air);
        await Task.Delay(300);
        Assert.Equal(placed, _link.PlacedCopy().Count);
        _sync.SetRole(isPf: false);
        Assert.Equal(FollowMode.Released, _sync.Mode);
    }

    [Fact]
    public async Task The_PF_coming_back_resumes_following_without_a_release()
    {
        await Following(Synthetic.From(Circuit, Synthetic.LiftoffT(Circuit) + 30));
        StopFeed();
        _sync.OnPeerState("lost");
        Assert.Equal(FollowMode.PfLost, _sync.Mode);
        var events = _link.FreezeEventsCopy().Count;
        Feed(Synthetic.From(Circuit, Synthetic.LiftoffT(Circuit) + 40));
        await Wait.Until(() => _sync.Mode == FollowMode.Following, "following again");
        Assert.True(_link.Flags.AllOn);
        Assert.Equal(events, _link.FreezeEventsCopy().Count); // never switched off on the way
    }

    [Fact]
    public async Task Session_end_releases()
    {
        await Following(Synthetic.From(Circuit, 30));
        _sync.EndSession("left");
        Assert.False(_link.Flags.AnyOn);
        Assert.Equal(FollowMode.Off, _sync.Mode);
    }

    [Fact]
    public async Task Release_aircraft_switches_off_freezes_left_by_a_crashed_bridge()
    {
        _link.SetFlagsDirectly(true);
        await Wait.Until(() => _sync.FrozenOutside, "frozen outside seen");
        Assert.True(_sync.StatusJson()["frozen_outside"]!.GetValue<bool>());
        Assert.True(_sync.ReleaseAircraft().Ok);
        Assert.False(_link.Flags.AnyOn);
        await Wait.Until(() => !_sync.FrozenOutside, "released");
    }

    [Fact]
    public async Task Release_aircraft_is_refused_while_following_a_connected_PF()
    {
        await Following(Synthetic.From(Circuit, 30));
        Assert.False(_sync.ReleaseAircraft().Ok);
        Assert.True(_link.Flags.AllOn);
    }

    [Fact]
    public async Task Thrust_levers_that_differ_are_reported_but_never_written_without_a_range()
    {
        _sync.StartSession((_, _) => { });
        _sync.SetRole(isPf: false);
        Feed(Synthetic.From(Circuit, 30), new Dictionary<string, double> { ["A_FC_THROTTLE_LEFT_INPUT"] = 3.5 });
        _link.SetLvar("A_FC_THROTTLE_LEFT_INPUT", 2.0);
        await Wait.Until(() => _sync.LeverDifferences().Count == 1, "lever difference");
        var d = _sync.LeverDifferences()[0];
        Assert.Equal((3.5, 2.0), (d.Pf, d.Mine));
        _link.SetLvar("A_FC_THROTTLE_LEFT_INPUT", 3.4); // within the tolerance
        await Wait.Until(() => _sync.LeverDifferences().Count == 0, "levers match");
    }

    [Fact]
    public async Task Stream_stats_are_logged_for_CRO_18()
    {
        await Following(Synthetic.From(Circuit, 30));
        await Task.Delay(500);
        var stats = _sync.LogStats()!;
        Assert.Equal("following", stats["mode"]!.GetValue<string>());
        Assert.True(stats["received"]!.GetValue<long>() > 5);
        Assert.True(stats["placed"]!.GetValue<long>() > 10);
        Assert.NotNull(stats["late_p95_ms"]);
        Assert.NotNull(stats["max_gap_ms"]);
    }

    [Fact]
    public async Task A_frame_without_the_core_fields_is_ignored_and_counted()
    {
        _sync.StartSession((_, _) => { });
        _sync.SetRole(isPf: false);
        await Task.Delay(100);
        _sync.OnStream(1, new JsonObject { ["lat"] = 1.0 });
        await Task.Delay(100);
        Assert.Empty(_link.PlacedCopy());
        Assert.Equal(1, _sync.LogStats()!["bad"]!.GetValue<long>());
    }
}
