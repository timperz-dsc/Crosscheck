using System.Text.Json.Nodes;
using Crosscheck.Bridge.Follow;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// The physics stream (CRO-13) end to end: two bridges with fake sims and fake physics links over the real
/// session server, the PF flying the synthetic circuit. Transfers on the ground and in the air, the PF lost
/// in the air (frozen, red line, released only on the pilot's yes), leaving in the air.
/// </summary>
[Collection("server")]
public sealed class PhysicsStreamEndToEndTests(ServerFixture server) : IAsyncLifetime
{
    private static readonly IReadOnlyList<PhysicsFrame> Circuit = Synthetic.Circuit();
    private static readonly double Taxi = 30; // taxiing at 15 kt
    private static readonly double Air = Synthetic.LiftoffT(Circuit) + 40; // climbing, about 170 kt
    private readonly List<BridgeRuntime> _bridges = [];
    private readonly string _logs = Repo.TempDir("crosscheck-physics-e2e");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var b in _bridges) await b.DisposeAsync();
        if (Environment.GetEnvironmentVariable("CROSSCHECK_KEEP_TEST_LOGS") == "1") return;
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    private static FakePhysicsLink Link(BridgeRuntime b) => (FakePhysicsLink)b.Physics.Link;

    private async Task<BridgeRuntime> Bridge(SessionMode mode, string? code, string name)
    {
        var options = new BridgeOptions
        {
            Server = server.Url, ProfilePath = Repo.FixtureProfile, Mode = mode, JoinCode = code, Name = name,
            ApiPort = 0, LogDir = Path.Combine(_logs, name), VersionTimeout = TimeSpan.Zero, StrictValidation = true,
        };
        var b = new BridgeRuntime(options, p => BridgeBackends.Fake(p));
        _bridges.Add(b);
        await b.StartAsync(CancellationToken.None);
        return b;
    }

    /// <summary>Host (PF) flying the circuit from <paramref name="fromT"/>, guest following it, frozen.</summary>
    private async Task<(BridgeRuntime Host, BridgeRuntime Guest)> Following(double fromT)
    {
        var host = await Bridge(SessionMode.Create, null, "host");
        Link(host).Fly(Circuit, fromT);
        var guest = await Bridge(SessionMode.Join, host.JoinCode, "guest");
        await Wait.Until(() => guest.Physics.Mode == FollowMode.Following && Link(guest).Flags.AllOn, "guest follows, frozen", 8000);
        return (host, guest);
    }

    /// <summary>How far the follower's placements were from where the PF really was at that time (p95, metres).</summary>
    private static double TrackingErrorP95(BridgeRuntime pf, BridgeRuntime follower, double sinceSimTime)
    {
        var truth = Link(pf).History();
        var errors = Link(follower).PlacedCopy()
            .Where(p => p.At >= sinceSimTime && p.Frame.T > truth[0].T && p.Frame.T < truth[^1].T)
            .Select(p => Geo.Distance(p.Frame, Interpolator.Sample(truth, p.Frame.T)))
            .Order().ToList();
        Assert.True(errors.Count > 30, $"only {errors.Count} placements to compare");
        return errors[(int)(0.95 * (errors.Count - 1))];
    }

    private static void AssertNeverFrozenOrPlaced(BridgeRuntime pf, int placedBefore = 0)
    {
        Assert.Equal(placedBefore, Link(pf).PlacedCopy().Count);
        Assert.False(Link(pf).Flags.AnyOn);
        Assert.Equal(0, Link(pf).GateRefused);
    }

    [Fact]
    public async Task The_follower_tracks_the_PF_on_the_ground_and_in_the_air()
    {
        var (host, guest) = await Following(Taxi);
        var t0 = Link(guest).SimTime;
        await Task.Delay(2500);
        Assert.True(TrackingErrorP95(host, guest, t0 + 0.5) < 0.5, "taxi tracking");
        Assert.False(guest.Physics.InAir);

        Link(host).Fly(Circuit, Air);
        var t1 = Link(guest).SimTime;
        await Wait.Until(() => guest.Physics.InAir, "guest placed in the air");
        await Task.Delay(2500);
        // A jump of the PF (the script moved on) is a late stretch; afterwards the follower tracks again.
        Assert.True(TrackingErrorP95(host, guest, t1 + 1.5) < 1.0, "air tracking");

        var counters = guest.StatusJson()["counters"]!;
        Assert.True(counters["stream_applied"]!.GetValue<long>() > 100, "about 30 frames per second arrive");
        Assert.True(host.Physics.FramesSent > 100);
        AssertNeverFrozenOrPlaced(host);
        var physics = guest.StatusJson()["physics"]!;
        Assert.Equal("following", physics["mode"]!.GetValue<string>());
        Assert.Equal("ok", physics["health"]!.GetValue<string>());
        Assert.True(physics["in_air"]!.GetValue<bool>());
    }

    private async Task Transfer(double fromT)
    {
        var (host, guest) = await Following(fromT);
        await Task.Delay(1000);

        Assert.True(guest.TakeControl(false).Ok);
        await Wait.Until(() => guest.Engine!.IsPf && !host.Engine!.IsPf, "control changed");
        // The new PF released where it stands: no freeze, no more placement, and it carries on from there
        // with the velocity it was placed with (no jump: only the distance flown since the last placement).
        await Wait.Until(() => !Link(guest).Flags.AnyOn, "new PF released");
        var (lastAt, lastPlaced) = Link(guest).PlacedCopy()[^1];
        var placedAtRelease = Link(guest).PlacedCopy().Count;
        var released = Link(guest).State;
        var speed = Geo.Velocity(lastPlaced).Length;
        var flown = speed * (Link(guest).SimTime - lastAt);
        Assert.True(Geo.Distance(lastPlaced, released) < flown + 1, $"jump at release {Geo.Distance(lastPlaced, released):0.0} m, flown {flown:0.0} m at {speed:0} m/s");
        await Task.Delay(300);
        Assert.True(Geo.Distance(released, Link(guest).State) > speed * 0.15, "the released aircraft keeps moving");
        Assert.Equal(placedAtRelease, Link(guest).PlacedCopy().Count);
        Assert.Equal(0, Link(guest).GateRefused);
        Assert.Equal(FollowMode.Streaming, guest.Physics.Mode);

        // The old PF freezes and follows the new one.
        await Wait.Until(() => host.Physics.Mode == FollowMode.Following && Link(host).Flags.AllOn, "old PF follows, frozen", 8000);
        var t0 = Link(host).SimTime;
        await Task.Delay(2000);
        Assert.True(TrackingErrorP95(guest, host, t0 + 0.5) < 1.0, "the old PF tracks the new PF");
    }

    [Fact]
    public Task Control_transfer_on_the_ground() => Transfer(Taxi);

    [Fact]
    public Task Control_transfer_in_the_air() => Transfer(Air);

    [Fact]
    public async Task PF_lost_in_the_air_holds_with_a_red_line_until_the_pilots_yes()
    {
        var (host, guest) = await Following(Air);
        var pfLost = new List<string>();
        guest.Events.Published += e => { if (e.Type == "pf_lost") lock (pfLost) pfLost.Add(e.Message); };

        // The PF's bridge dies (no leave).
        _bridges.Remove(host);
        await host.Client!.DisposeAsync();
        await host.DisposeAsync();

        await Wait.Until(() => guest.Physics.Mode == FollowMode.PfLost, "guest holds", 10000);
        await Wait.Until(() => { lock (pfLost) return pfLost.Count == 1; }, "red line");
        var physics = guest.StatusJson()["physics"]!;
        Assert.Equal("pf_lost", physics["mode"]!.GetValue<string>());
        Assert.Equal("peer", physics["pf_lost_cause"]!.GetValue<string>());
        Assert.True(physics["in_air"]!.GetValue<bool>());

        // Never released by itself: frozen, nothing more written, not moving.
        var placed = Link(guest).PlacedCopy().Count;
        var at = Link(guest).State;
        await Task.Delay(2500);
        Assert.True(Link(guest).Flags.AllOn);
        Assert.Equal(placed, Link(guest).PlacedCopy().Count);
        Assert.True(Geo.Distance(at, Link(guest).State) < 1e-6);
        // A normal "I have control" is not enough while the PF is lost; leaving needs a confirmation in the air.
        Assert.True(guest.TakeControl(false).Ok);
        await Task.Delay(500);
        Assert.True(Link(guest).Flags.AllOn);

        // The pilot's yes: control, and the freezes come off.
        Assert.True(guest.TakeControl(confirmPeerLost: true).Ok);
        await Wait.Until(() => guest.Engine!.IsPf, "guest is PF");
        await Wait.Until(() => !Link(guest).Flags.AnyOn, "released");
        Assert.Equal(FollowMode.Streaming, guest.Physics.Mode);
    }

    [Fact]
    public async Task Leaving_in_the_air_asks_first_and_leaving_on_the_ground_releases()
    {
        var (host, guest) = await Following(Air);
        var answer = await guest.LeaveAsync(releaseInAir: false);
        Assert.False(answer.Ok);
        Assert.Equal("release_in_air", answer.Confirm);
        Assert.NotNull(guest.Engine); // still in the session
        Assert.True(Link(guest).Flags.AllOn);

        Assert.True((await guest.LeaveAsync(releaseInAir: true)).Ok);
        Assert.False(Link(guest).Flags.AnyOn);
        Assert.Null(guest.Engine);

        // On the ground: leaving releases without a question.
        Link(host).Fly(Circuit, Taxi);
        var second = await Bridge(SessionMode.Join, host.JoinCode, "second");
        await Wait.Until(() => second.Physics.Mode == FollowMode.Following && Link(second).Flags.AllOn, "second follows", 8000);
        Assert.True((await second.LeaveAsync()).Ok);
        Assert.False(Link(second).Flags.AnyOn);
        AssertNeverFrozenOrPlaced(host);
    }

    [Fact]
    public async Task Status_shows_the_stream_for_the_status_bar_and_the_API_releases_a_frozen_aircraft()
    {
        var (host, guest) = await Following(Taxi);
        await Task.Delay(500);
        var pf = host.StatusJson()["physics"]!;
        Assert.Equal("streaming", pf["mode"]!.GetValue<string>());
        var pm = guest.StatusJson()["physics"]!;
        Assert.Equal(100, pm["buffer_ms"]!.GetValue<double>());
        Assert.True(pm.AsObject().ContainsKey("latency_ms")); // known after the first ping
        Assert.True(pm["frozen"]!.GetValue<bool>());

        // A crashed bridge left this sim frozen: the start screen offers "Release aircraft".
        await guest.LeaveAsync();
        Link(guest).SetFlagsDirectly(true);
        await Wait.Until(() => guest.StatusJson()["physics"]!["frozen_outside"]!.GetValue<bool>(), "frozen outside a session");
        using var http = new HttpClient();
        var res = await http.PostAsync($"http://127.0.0.1:{guest.Api!.Port}/api/aircraft/release", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.False(Link(guest).Flags.AnyOn);
    }
}
