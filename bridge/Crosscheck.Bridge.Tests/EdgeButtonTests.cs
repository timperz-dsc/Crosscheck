using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Sync;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// CRO-29/30: Fenix push buttons count +1 on press and +1 on release (odd = down). Each edge is mirrored as
/// it happens; the receiver writes its own value + 1, never the sender's number (protocol.md section 5).
/// </summary>
public class EdgeRuleTests
{
    [Theory]
    [InlineData(0, 1, "down")]
    [InlineData(1, 2, "up")]
    [InlineData(0, 2, "down up")] // two edges inside one frame
    [InlineData(7, 9, "up down")]
    [InlineData(1, 0, "up")] // a binding that writes 0/1
    [InlineData(0, 1.0000001, "down")]
    [InlineData(2, 1, "down")]
    [InlineData(2, 0, "")] // a decrease without a parity change: not an edge
    [InlineData(0, 40, "")] // a jump of 40 is not a click (a reload): no edge at all from an even value
    [InlineData(0, 41, "down")] // ...and at most one edge from the new parity
    public void Edges_follow_the_parity(double prev, double value, string expected) =>
        Assert.Equal(expected, string.Join(" ", SyncEngine.Edges(prev, value)));
}

public sealed class EdgeSenderTests : IAsyncLifetime
{
    private readonly EngineHarness _h = new();

    public Task InitializeAsync() => _h.StartAsync(pf: true);
    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public void Every_push_button_is_sampled_every_frame()
    {
        // The plan is read with SIMCONNECT_PERIOD.SIM_FRAME (SimConnectAdapter): every frame, so no edge is lost.
        Assert.Contains("S_CDU1_KEY_FPLN", _h.Engine.Plan.Watched);
        Assert.Contains("S_OH_FIRE_ENG1_TEST", _h.Engine.Plan.Watched);
        Assert.Contains("I_FCU_EFIS1_FD", _h.Engine.Plan.Watched); // the toggle lamp
    }

    [Fact]
    public async Task Two_edges_inside_one_frame_are_a_down_and_an_up()
    {
        _h.Sim.PilotSet("S_CDU1_KEY_FPLN", 2);
        await Wait.Until(() => _h.Commands.Count == 2, "two commands");
        Assert.Equal(["down", "up"], _h.Commands.Select(c => c["edge"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_binding_that_writes_0_and_1_works_the_same_way()
    {
        _h.Sim.PilotSet("S_OH_FIRE_ENG1_TEST", 1);
        await Wait.Until(() => _h.Commands.Count == 1, "down");
        _h.Sim.PilotSet("S_OH_FIRE_ENG1_TEST", 0);
        await Wait.Until(() => _h.Commands.Count == 2, "up");
        Assert.Equal(["down", "up"], _h.Commands.Select(c => c["edge"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Snapshots_compare_parity_and_the_toggle_lamp_only()
    {
        _h.Sim.Logic("S_FCU_EFIS1_FD", 85); // a counter that differs between the sims by design
        _h.Sim.Logic("I_FCU_EFIS1_FD", 1);
        await Wait.Until(() => _h.Sim.Get("I_FCU_EFIS1_FD") == 1, "lamp");
        var efis = _h.Engine.GroupValues("efis");
        Assert.Contains(("S_FCU_EFIS1_FD", "pulse", (object?)1.0), efis); // held down: odd
        Assert.Contains(("I_FCU_EFIS1_FD", "toggle", (object?)1.0), efis);
        Assert.DoesNotContain(efis, v => v.Var == "I_FCU_EFIS1_FD" && v.Class == "observe"); // not twice

        _h.Channel.Inject("snapshot_request", new JsonObject { ["request_id"] = "r1", ["groups"] = new JsonArray("efis"), ["reason"] = "mismatch" });
        await Wait.Until(() => _h.Channel.SentOf("snapshot_values").Count == 1, "values");
        var values = _h.Channel.SentOf("snapshot_values")[0]["values"]!.AsArray();
        var lamp = values.Single(v => v!["var"]!.GetValue<string>() == "I_FCU_EFIS1_FD")!;
        Assert.Equal("S_FCU_EFIS1_FD", lamp["button"]!.GetValue<string>());
    }
}

public sealed class EdgeReceiverTests
{
    private const string Fpln = "S_CDU1_KEY_FPLN";
    private const string Test = "S_OH_FIRE_ENG1_TEST";

    private static async Task<EngineHarness> Pm(EngineOptions? options = null)
    {
        var h = new EngineHarness(options);
        await h.StartAsync(pf: false);
        return h;
    }

    private static double AtOf(EngineHarness h, string name, double value) => h.Sim.TimedWrites.First(w => w.Name == name && w.Value == value).AtMs;

    [Fact]
    public async Task An_up_waits_for_the_down_and_at_least_50_ms()
    {
        await using var h = await Pm();
        h.Edge(Fpln, "down");
        h.Edge(Fpln, "up", gapMs: 0); // both arrive at once
        await Wait.Until(() => h.Sim.Get(Fpln) == 2, "released");
        Assert.True(AtOf(h, Fpln, 2) - AtOf(h, Fpln, 1) >= 45, "the up came too soon after the down");
    }

    [Fact]
    public async Task A_hold_is_mirrored_for_as_long_as_it_lasted()
    {
        await using var h = await Pm();
        h.Edge(Test, "down");
        h.Edge(Test, "up", gapMs: 600); // after a resume both edges can arrive together
        await Wait.Until(() => h.Sim.Get(Test) == 2, "released");
        Assert.InRange(AtOf(h, Test, 2) - AtOf(h, Test, 1), 570, 800);
    }

    [Fact]
    public async Task A_button_the_local_pilot_holds_is_never_pressed_or_released_by_crosscheck()
    {
        // The other pilot's press waits while the local pilot holds the button; held longer than the cap, it is
        // a lost press (red), never written into a button the local pilot holds (gate test 2 causes C and D).
        await using var h = await Pm(new EngineOptions { SafetyReleaseMs = 200, LocalHoldMaxMs = 300 });
        h.Sim.PilotSet(Test, 1); // the local pilot holds it down
        await Wait.Until(() => h.Commands.Count == 1 && h.Sim.Get(Test) == 1, "held");
        var down = h.Edge(Test, "down");
        await Task.Delay(150);
        Assert.Null(h.AckFor(down)); // held back, not judged yet
        var ack = await h.WaitAck(down);
        Assert.False(ack["ok"]!.GetValue<bool>());
        Assert.Equal("press_lost", ack["reason"]!.GetValue<string>());
        Assert.True((await h.WaitAck(h.Edge(Test, "up", gapMs: 100)))["ok"]!.GetValue<bool>());
        await Task.Delay(400); // longer than the safety cap
        Assert.DoesNotContain(h.Sim.Writes, w => w.Name == Test);
        Assert.Equal(1, h.Sim.Get(Test));
        Assert.Empty(h.Channel.SentOf("safety_release"));
    }

    [Fact]
    public async Task A_press_during_a_local_press_waits_for_the_local_release_then_lands()
    {
        await using var h = await Pm();
        h.Sim.PilotSet(Test, 1); // local press
        await Wait.Until(() => h.Commands.Count == 1, "local down sent");
        var down = h.Edge(Test, "down");
        var up = h.Edge(Test, "up", gapMs: 80);
        await Task.Delay(150);
        Assert.Null(h.AckFor(down));
        h.Sim.PilotSet(Test, 2); // local release
        Assert.True((await h.WaitAck(down))["ok"]!.GetValue<bool>());
        Assert.True((await h.WaitAck(up))["ok"]!.GetValue<bool>());
        await Wait.Until(() => h.Sim.Get(Test) == 4, "the other pilot's click landed after the local one");
        Assert.Equal([3.0, 4.0], h.Sim.Writes.Where(w => w.Name == Test).Select(w => w.Value));
    }

    [Fact]
    public async Task The_safety_cap_releases_only_with_an_up_and_a_red_desync()
    {
        await using var h = await Pm(new EngineOptions { SafetyReleaseMs = 200 });
        Assert.True((await h.WaitAck(h.Edge(Test, "down")))["ok"]!.GetValue<bool>());
        await Wait.Until(() => h.Channel.SentOf("safety_release").Count == 1, "safety release");
        var msg = h.Channel.SentOf("safety_release")[0];
        Assert.Equal((Test, "cap", "overhead", 2.0),
            (msg["var"]!.GetValue<string>(), msg["cause"]!.GetValue<string>(), msg["group"]!.GetValue<string>(), msg["observed"]!.GetValue<double>()));
        Assert.Equal([1.0, 2.0], h.Sim.Writes.Where(w => w.Name == Test).Select(w => w.Value)); // never a down
        Assert.DoesNotContain(h.Sim.Writes, w => w.Name != Test);
        Assert.Contains(Test, h.Status.OpenDesyncs);
        Assert.Contains(h.Hub.Recent, e => e.Type == "safety_release");

        // The late up finds nothing to release.
        Assert.True((await h.WaitAck(h.Edge(Test, "up", gapMs: 5000)))["ok"]!.GetValue<bool>());
        Assert.Equal(2, h.Sim.Writes.Count(w => w.Name == Test));
        Assert.Empty(h.Commands); // the release is not sent back as a pilot press
    }

    [Fact]
    public async Task Link_loss_while_a_mirrored_button_is_down_releases_it_at_once()
    {
        await using var h = await Pm();
        Assert.True((await h.WaitAck(h.Edge(Test, "down")))["ok"]!.GetValue<bool>());
        var lost = Stopwatch.StartNew();
        h.Channel.SetState(Crosscheck.Bridge.Protocol.ServerState.Reconnecting);
        await Wait.Until(() => h.Sim.Get(Test) == 2, "released");
        Assert.True(lost.ElapsedMilliseconds < 1000);
        Assert.Empty(h.Channel.SentOf("safety_release")); // the link is down: sent after resume

        h.Channel.SetState(Crosscheck.Bridge.Protocol.ServerState.Connected);
        h.Channel.RaiseResumed(false);
        await Wait.Until(() => h.Channel.SentOf("safety_release").Count == 1, "sent after resume");
        Assert.Equal("link_lost", h.Channel.SentOf("safety_release")[0]["cause"]!.GetValue<string>());
    }

    [Fact]
    public async Task Losing_the_other_pilot_releases_what_crosscheck_holds_for_them()
    {
        await using var h = await Pm();
        Assert.True((await h.WaitAck(h.Edge(Test, "down")))["ok"]!.GetValue<bool>());
        h.Channel.Inject("peer_status", new JsonObject { ["state"] = "lost", ["seat"] = "host", ["client_id"] = EngineHarness.OtherClient });
        await Wait.Until(() => h.Channel.SentOf("safety_release").Count == 1, "released");
        Assert.Equal("peer_lost", h.Channel.SentOf("safety_release")[0]["cause"]!.GetValue<string>());
        Assert.Equal(2, h.Sim.Get(Test));
    }

    [Fact]
    public async Task A_pilot_touching_a_mirrored_button_takes_it_over()
    {
        await using var h = await Pm(new EngineOptions { SafetyReleaseMs = 200 });
        Assert.True((await h.WaitAck(h.Edge(Test, "down")))["ok"]!.GetValue<bool>());
        h.Sim.PilotSet(Test, 2); // the local pilot let go of it
        await Task.Delay(400);
        Assert.Empty(h.Channel.SentOf("safety_release"));
        Assert.Equal([1.0], h.Sim.Writes.Where(w => w.Name == Test).Select(w => w.Value));
    }
}

/// <summary>
/// Replays the pilot clicks recorded in profiles/fenix-a320/observe-results.csv (CRO-29 part 1) through a
/// sender engine, a relay and a receiver engine, with the recorded timing.
/// </summary>
public sealed class ObserveReplayTests : IAsyncLifetime
{
    private readonly EngineHarness _pf = new();
    private readonly EngineHarness _pm = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _relay;

    public async Task InitializeAsync()
    {
        await _pf.StartAsync(pf: true);
        await _pm.StartAsync(pf: false);
        foreach (var h in new[] { _pf, _pm }) h.Sim.HoldEffect("S_OH_FIRE_ENG1_TEST", "I_OH_FIRE_ENG1_BUTTON", 1000);
        _relay = Task.Run(Relay);
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        if (_relay is not null) await _relay;
        await _pf.DisposeAsync();
        await _pm.DisposeAsync();
    }

    /// <summary>The server's part: every command the PF sends arrives at the PM, in order.</summary>
    private async Task Relay()
    {
        var sent = 0;
        while (!_stop.IsCancellationRequested)
        {
            var commands = _pf.Commands;
            for (; sent < commands.Count; sent++)
            {
                var c = commands[sent];
                _pm.Remote(c["var"]!.GetValue<string>(), "pulse", c["value"]!.GetValue<double>(), settleMs: c["settle_ms"]!.GetValue<int>(),
                    edge: c["edge"]?.GetValue<string>(), gapMs: c["gap_ms"]?.GetValue<int>() ?? 0);
            }
            await Task.Delay(2);
        }
    }

    /// <summary>The recorded value changes of one button in one step: (ms from the first change, steps above the start).</summary>
    private static List<(double Ms, int Steps)> Recorded(string step, string var)
    {
        var path = Path.Combine(Repo.Root, "profiles", "fenix-a320", "observe-results.csv");
        var rows = File.ReadLines(path).Skip(1).Select(l => l.Split(','))
            .Where(c => c[2] == step && c[4] == var).Select(c => (T: double.Parse(c[0], CultureInfo.InvariantCulture), Old: double.Parse(c[5], CultureInfo.InvariantCulture), New: double.Parse(c[6], CultureInfo.InvariantCulture)))
            .ToList();
        Assert.NotEmpty(rows);
        return rows.Select(r => (r.T - rows[0].T, (int)(r.New - rows[0].Old))).ToList();
    }

    /// <summary>The pilot repeats the recorded clicks on <paramref name="button"/> in the PF's cockpit.</summary>
    private async Task Replay(List<(double Ms, int Steps)> clicks, string button)
    {
        var start = _pf.Sim.Get(button);
        var clock = Stopwatch.StartNew();
        foreach (var (ms, steps) in clicks)
        {
            var wait = ms - clock.Elapsed.TotalMilliseconds;
            if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
            _pf.Sim.PilotSet(button, start + steps);
        }
    }

    private List<double> PmWrites(string name) => _pm.Sim.Writes.Where(w => w.Name == name).Select(w => w.Value).ToList();

    [Theory]
    [InlineData("MCDU1_S/once", 2)]
    [InlineData("MCDU1_S/double", 4)]
    [InlineData("MCDU1_S/hold", 2)]
    public async Task Recorded_MCDU_clicks_arrive_as_the_same_edges(string step, int edges)
    {
        var clicks = Recorded(step, "S_CDU1_KEY_S");
        Assert.Equal(edges, clicks.Count);
        await Replay(clicks, "S_CDU1_KEY_FPLN");
        await Wait.Until(() => PmWrites("S_CDU1_KEY_FPLN").Count == edges, () => $"{edges} edges mirrored, got {PmWrites("S_CDU1_KEY_FPLN").Count}");
        Assert.Equal(Enumerable.Range(1, edges).Select(i => (double)i), PmWrites("S_CDU1_KEY_FPLN")); // own value + 1, in order
        Assert.Equal(string.Concat(Enumerable.Repeat("FPLN", edges / 2)), _pm.Sim.McduText(1)); // one key per press
        Assert.Equal(_pf.Sim.McduText(1), _pm.Sim.McduText(1));
    }

    [Fact]
    public async Task A_recorded_2_s_hold_is_mirrored_as_a_2_s_hold()
    {
        var clicks = Recorded("MCDU1_S/hold", "S_CDU1_KEY_S");
        var held = clicks[1].Ms - clicks[0].Ms; // 1849 ms in the recording
        await Replay(clicks, "S_CDU1_KEY_FPLN");
        await Wait.Until(() => PmWrites("S_CDU1_KEY_FPLN").Count == 2, "mirrored");
        var times = _pm.Sim.TimedWrites.Where(w => w.Name == "S_CDU1_KEY_FPLN").Select(w => w.AtMs).ToList();
        Assert.InRange(times[1] - times[0], held - 150, held + 150);
    }

    [Fact]
    public async Task The_recorded_FIRE_TEST_hold_runs_the_test_on_both_sides_a_click_on_neither()
    {
        var click = Recorded("ENG1_FIRE_TEST/once", "S_OH_FIRE_ENG1_TEST");
        await Replay(click, "S_OH_FIRE_ENG1_TEST");
        await Wait.Until(() => PmWrites("S_OH_FIRE_ENG1_TEST").Count == 2, "click mirrored");
        await Task.Delay(1200);
        Assert.Equal(0, _pf.Sim.Get("I_OH_FIRE_ENG1_BUTTON")); // a 158 ms click never runs the test
        Assert.Equal(0, _pm.Sim.Get("I_OH_FIRE_ENG1_BUTTON"));

        var hold = Recorded("ENG1_FIRE_TEST/hold", "S_OH_FIRE_ENG1_TEST");
        var replay = Replay(hold, "S_OH_FIRE_ENG1_TEST");
        await Wait.Until(() => _pm.Sim.Get("I_OH_FIRE_ENG1_BUTTON") == 1, "the test runs in the PM's cockpit too", 4000);
        Assert.Equal(1, _pf.Sim.Get("I_OH_FIRE_ENG1_BUTTON"));
        await replay;
        await Wait.Until(() => _pm.Sim.Get("I_OH_FIRE_ENG1_BUTTON") == 0, "and ends on release");
        Assert.Equal([1.0, 2.0, 3.0, 4.0], PmWrites("S_OH_FIRE_ENG1_TEST"));
        Assert.Empty(_pm.Channel.SentOf("safety_release"));
    }
}
