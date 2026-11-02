using System.Text.Json.Nodes;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// CRO-33: replays from gate test 2 (docs/gate-tests/CRO-22-gate-test-2.md), using the anonymised extract
/// server/test/fixtures/gate-test-2.json. Each test is one cause the pilots hit.
/// </summary>
public class GateTest2ReplayTests
{
    private static readonly JsonNode Data =
        JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.Root, "server", "test", "fixtures", "gate-test-2.json")))!;

    private static readonly Profile Real =
        ProfileLoader.LoadFile(Path.Combine(Repo.Root, "profiles", "fenix-a320", "profile.yaml")).Profile!;

    private static async Task<EngineHarness> Start(bool pf, EngineOptions? options = null, Profile? profile = null, FakeSimOptions? sim = null,
        Action<FakeSimAdapter>? before = null)
    {
        var h = new EngineHarness(options, profile: profile, simOptions: sim);
        before?.Invoke(h.Sim); // values the cockpit already has when the session starts (not pilot actions)
        await h.StartAsync(pf);
        return h;
    }

    /// <summary>Waits until <paramref name="ms"/> after <paramref name="start"/> (replays keep the logged timing).</summary>
    private static async Task At(DateTime start, int ms)
    {
        var wait = start.AddMilliseconds(ms) - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
    }

    // ---------------------------------------------------------------- D: fast presses on the receiver

    [Fact]
    public async Task Fd_burst_with_41_ms_gaps_loses_no_press()
    {
        var fd = Data["fd_burst"]!;
        var name = fd["var"]!.GetValue<string>();
        var lamp = fd["lamp"]!.GetValue<string>();
        // The receiver's own writes are read back as late as in gate test 2 (median), so the race can happen.
        var lag = fd["read_back_ms"]!["median"]!.GetValue<int>();
        await using var h = await Start(false, sim: new FakeSimOptions { FrameMs = 5, EffectLagMs = 20, McduLagMs = 30, ReadBackLagMs = lag });
        var edges = fd["edges"]!.AsArray().Select(e => (T: e![0]!.GetValue<int>(), Edge: e[1]!.GetValue<string>(), Gap: e[2]!.GetValue<int>())).ToList();
        Assert.Contains(edges.Zip(edges.Skip(1)), p => p.Second.T - p.First.T <= 45); // the 41 ms gaps of the log

        var ids = new List<(string Id, string Edge)>();
        var start = DateTime.UtcNow;
        foreach (var e in edges)
        {
            await At(start, e.T);
            ids.Add((h.Edge(name, e.Edge, e.Gap, settleMs: 150), e.Edge));
        }
        foreach (var (id, _) in ids)
        {
            var ack = await h.WaitAck(id, 5000);
            Assert.True(ack["ok"]!.GetValue<bool>(), $"edge {id} was not applied: {ack.ToJsonString()}");
        }
        await Wait.Until(() => h.Sim.Get(name) == edges.Count, "every edge landed (own value + 1 each)", 5000);
        Assert.Equal(Enumerable.Range(1, edges.Count).Select(i => (double)i), h.Sim.Writes.Where(w => w.Name == name).Select(w => w.Value));
        var downs = edges.Count(e => e.Edge == "down");
        await Wait.Until(() => h.Sim.Get(lamp) == downs % 2, "the lamp toggled once per press");

        // One lamp read per burst (last edge + 400 ms), for the burst's last down edge: no flicker per press.
        var lastDown = ids.Last(i => i.Edge == "down").Id;
        await Wait.Until(() => h.Channel.SentOf("effect_report").Any(r => r["id"]!.GetValue<string>() == lastDown), "report for the last press", 5000);
        Assert.InRange(h.Channel.SentOf("effect_report").Count, 1, downs / 2);
    }

    // ---------------------------------------------------------------- A: deny list (IDG)

    [Fact]
    public async Task Idg_clicks_are_never_mirrored_and_the_press_is_shown_once()
    {
        var idg = Data["idg"]!;
        var name = idg["var"]!.GetValue<string>();
        await using var h = await Start(true, profile: Real);
        var start = DateTime.UtcNow;
        foreach (var v in idg["values"]!.AsArray())
        {
            await At(start, v![0]!.GetValue<int>() / 4); // the logged clicks, 4x faster
            h.Sim.PilotSet(name, v[1]!.GetValue<double>());
        }
        await Wait.Until(() => h.Engine.GroupValues("overhead").Any(x => x.Var == name && Equals(x.Value, 22.0)), "last click seen");
        await Task.Delay(50);
        Assert.DoesNotContain(h.Commands, c => c["var"]!.GetValue<string>() == name); // never mirrored
        var denied = h.Channel.SentOf("denied_input");
        Assert.InRange(denied.Count, 1, 4); // one line per press burst (at most one per 2 s), never one per click
        Assert.Equal("overhead", denied[0]["group"]!.GetValue<string>());
        Assert.Contains(h.Hub.Recent, e => e.Type == "denied_input" && e.Message.Contains("not mirrored (irreversible)"));
        // Compared in snapshots as its raw value (a pilot who joins later still sees it), never corrected.
        Assert.Contains(h.Engine.GroupValues("overhead"), x => x.Var == name && x.Class == "denied");
    }

    [Fact]
    public async Task Idg_is_refused_on_every_write_path()
    {
        const string name = "S_OH_ELEC_IDG1";
        await using var h = await Start(false, new EngineOptions { StuckMs = 50 }, profile: Real);
        h.Sim.Logic(name, 1);
        Assert.False((await h.WaitAck(h.Edge(name, "down")))["ok"]!.GetValue<bool>()); // command
        Assert.False((await h.WaitAck(h.Remote(name, "verified", 1)))["ok"]!.GetValue<bool>());
        Assert.False((await h.WaitAck(h.Correction(name, "verified", 0, "pf_reference")))["ok"]!.GetValue<bool>()); // correction
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.Engine.PilotSetAsync(name, 0)); // "Set both"
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.Engine.PressHereOnlyAsync(name)); // "Press here only"
        h.Channel.Inject("desync", new JsonObject
        {
            ["var"] = name, ["group"] = "overhead", ["reason"] = "stuck_pressed", ["expected"] = 0, ["observed"] = 1, ["tier"] = "red",
            ["client_id"] = h.Channel.ClientId, ["message"] = "x",
        }, h.Channel.NextSeq());
        await Task.Delay(100);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.Engine.ReleaseItAsync(name)); // "Release it"
        Assert.False(h.Engine.CanReleaseHere(name, h.Status.DesyncOf(name)!));
        Assert.False(h.Engine.CanSetBoth(name));
        Assert.Throws<SafetyViolationException>(() => h.Sim.Guard.CheckWrite(name, 2)); // the guard itself (safety release path)
        Assert.DoesNotContain(h.Sim.Writes, w => w.Name == name);
    }

    // ---------------------------------------------------------------- B: BLUE ELEC PUMP is a latch

    [Fact]
    public async Task Blue_pump_toggles_as_a_latch_on_both_sides()
    {
        var blue = Data["blue_pump"]!;
        var name = blue["var"]!.GetValue<string>();
        var values = blue["values"]!.AsArray().Select(v => (T: v![0]!.GetValue<int>(), Value: v[1]!.GetValue<double>())).ToList();
        Assert.Equal([1.0, 0, 1, 0, 1], values.Select(v => v.Value));

        // Sender: the host's clicks become absolute values, not edges.
        await using (var host = await Start(true, profile: Real))
        {
            foreach (var (_, value) in values)
            {
                host.Sim.PilotSet(name, value);
                await Task.Delay(30);
            }
            await Wait.Until(() => host.Commands.Count(c => c["var"]!.GetValue<string>() == name) == values.Count, "one command per click");
            Assert.All(host.Commands.Where(c => c["var"]!.GetValue<string>() == name), c =>
            {
                Assert.Equal("verified", c["class"]!.GetValue<string>());
                Assert.Null(c["edge"]);
            });
        }

        // Receiver (was at 1 in gate test 2 and skipped every click): it follows every value.
        await using var guest = await Start(false, profile: Real, before: sim => sim.Logic(name, 1));
        foreach (var (_, value) in values.Skip(1))
        {
            var ack = await guest.WaitAck(guest.Remote(name, "verified", value, settleMs: 150));
            Assert.True(ack["ok"]!.GetValue<bool>());
            Assert.Equal(value, guest.Sim.Get(name));
        }
        Assert.Equal([0.0, 1, 0, 1], guest.Sim.Writes.Where(w => w.Name == name).Select(w => w.Value));
    }

    // ---------------------------------------------------------------- E, F: encoders

    [Fact]
    public async Task Alt_case_writes_nothing_and_the_display_is_not_compared_while_unpowered()
    {
        var alt = Data["alt"]!;
        var name = alt["var"]!.GetValue<string>();
        var display = alt["display"]!.GetValue<string>();
        await using var h = await Start(false, profile: Real);
        h.Sim.Logic(display, alt["unpowered"]!["pm"]!.GetValue<double>());
        // Every correction the server sent in gate test 2 is refused: an encoder is never corrected.
        var ack = await h.WaitAck(h.Correction(name, "delta", alt["unpowered"]!["pf"]!.GetValue<double>(), "pf_reference"));
        Assert.False(ack["ok"]!.GetValue<bool>());
        Assert.False((await h.WaitAck(h.Correction(name, "delta", alt["powered"]!["pf"]!.GetValue<double>(), "pf_reference")))["ok"]!.GetValue<bool>());
        Assert.Empty(h.Sim.Writes);

        // Unpowered FCU (B_FCU_POWER 0): no display to compare, context "unpowered".
        Assert.Null(h.Engine.GroupValues("fcu").Single(v => v.Var == name).Value);
        Assert.Equal("unpowered", h.Engine.DeltaContext(Real.Vars.Single(v => v.Name == name)));
        h.Channel.Inject("snapshot_request", new JsonObject { ["request_id"] = "r1", ["groups"] = new JsonArray("fcu"), ["reason"] = "mismatch" }, h.Channel.NextSeq());
        await Wait.Until(() => h.Channel.SentOf("snapshot_values").Count == 1, "snapshot values");
        var item = h.Channel.SentOf("snapshot_values")[0]["values"]!.AsArray().Single(v => v!["var"]!.GetValue<string>() == name)!;
        Assert.Equal("unpowered", item["context"]!.GetValue<string>());

        // Powered: the display is reported and compared (the server decides; never written here).
        h.Sim.Logic("B_FCU_POWER", 1);
        Assert.Equal(alt["unpowered"]!["pm"]!.GetValue<double>(), h.Engine.GroupValues("fcu").Single(v => v.Var == name).Value);
        Assert.Equal("", h.Engine.DeltaContext(Real.Vars.Single(v => v.Name == name)));
    }

    [Fact]
    public async Task An_in_between_read_back_of_our_own_clicks_is_never_sent_as_a_pilot_turn()
    {
        // Gate test 2, 19:35:33: four BARO clicks 20-44 ms apart. The guest had written -3 while the sim still
        // showed -2 (its own earlier write, already read back), and sent "+1" back to the host as a pilot turn.
        var p = Data["phantom_turn"]!;
        var name = p["var"]!.GetValue<string>();
        var deltas = p["deltas"]!.AsArray().Select(d => d![1]!.GetValue<double>()).ToList();
        Assert.Equal([-1.0, -1, -1, -1], deltas);
        // Our writes stay in flight; the test makes the sim report the first one back at the moment the log shows.
        await using var h = await Start(false, profile: Real, sim: new FakeSimOptions { FrameMs = 5, EffectLagMs = 20, McduLagMs = 30, ReadBackLagMs = 1500 });
        var ids = new List<string> { h.Remote(name, "delta", delta: deltas[0]), h.Remote(name, "delta", delta: deltas[1]) };
        await Wait.Until(() => h.Sim.WritesOf(name).Count == 2, "two clicks written (-1, -2)");
        h.Sim.Logic(name, -1); // the read-back of our first write arrives: fresh, but behind our last write (-2)
        await Task.Delay(50);
        ids.Add(h.Remote(name, "delta", delta: deltas[2]));
        ids.Add(h.Remote(name, "delta", delta: deltas[3]));
        await Wait.Until(() => h.Sim.WritesOf(name).Count == 4, "all four clicks written");
        await Task.Delay(100);
        Assert.Empty(h.Commands); // no phantom "+1" back to the other pilot
        Assert.Equal([-1.0, -2, -3, -4], h.Sim.WritesOf(name));
    }

    [Fact]
    public async Task An_encoder_click_passes_once_the_counter_reached_or_passed_its_target()
    {
        await using var h = await Start(false);
        var first = h.Remote("E_FCU_ALTITUDE", "delta", delta: 1);
        h.Sim.PilotTurn("E_FCU_ALTITUDE", 5); // the local pilot keeps turning: the counter moves past the target
        Assert.True((await h.WaitAck(first))["ok"]!.GetValue<bool>());
    }

    // ---------------------------------------------------------------- C: two pilots on one counter button

    [Fact]
    public async Task Apu_reset_collision_replayed_ends_released_with_the_local_click_mirrored_as_one_press()
    {
        var apu = Data["apu_reset"]!;
        var name = apu["var"]!.GetValue<string>();
        await using var h = await Start(false, profile: Real, before: sim => sim.Logic(name, apu["start_value"]!.GetValue<double>()));
        var start = DateTime.UtcNow;
        var remote = new List<string>();
        foreach (var e in apu["events"]!.AsArray())
        {
            await At(start, e![0]!.GetValue<int>());
            if (e[1]!.GetValue<string>() == "remote") remote.Add(h.Edge(name, e[2]!.GetValue<string>(), e[3]!.GetValue<int>()));
            else h.Sim.PilotSet(name, h.Sim.Get(name) + 1); // the guest pilot's own step (Fenix adds 1)
        }
        foreach (var id in remote) Assert.True((await h.WaitAck(id, 5000))["ok"]!.GetValue<bool>());
        // In gate test 2 the button rested odd (pressed) for 84 minutes. Now the other pilot's up waits for the
        // local click to end, then releases Crosscheck's own down: the button rests up.
        await Wait.Until(() => ((long)h.Sim.Get(name) & 1) == 0, "button up");
        Assert.Equal(1, apu["end_parity_in_gate_test"]!.GetValue<int>());
        var sent = h.Commands.Where(c => c["var"]!.GetValue<string>() == name).Select(c => c["edge"]!.GetValue<string>()).ToList();
        Assert.Equal(["down", "up"], sent); // the local click went out as one press, not "up, down"
    }

    [Fact]
    public async Task Release_it_writes_one_up_step_in_this_cockpit_only_when_stuck_and_resting()
    {
        const string name = "S_OH_APU_AUTOEXTING_RESET";
        // As gate test 2 left the guest's button: odd, resting.
        await using var h = await Start(false, new EngineOptions { StuckMs = 300 }, before: sim => sim.Logic(name, 17));

        // Not reported stuck in this cockpit: refused.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.Engine.ReleaseItAsync(name));
        Inject(h, name, "c_other0001");
        await Task.Delay(50);
        Assert.False(h.Engine.CanReleaseHere(name, h.Status.DesyncOf(name)!));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.Engine.ReleaseItAsync(name));

        // Stuck here, but it just moved: someone may hold it.
        Inject(h, name, h.Channel.ClientId);
        h.Sim.PilotSet(name, 19);
        await Task.Delay(50);
        Assert.True(h.Engine.CanReleaseHere(name, h.Status.DesyncOf(name)!));
        var moved = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.Engine.ReleaseItAsync(name));
        Assert.Contains("moved", moved.Message);

        // Resting for StuckMs: one up step (19 → 20), never a down, nothing sent to the other pilot.
        await Task.Delay(350);
        var before = h.Commands.Count;
        await h.Engine.ReleaseItAsync(name);
        Assert.Equal(20, h.Sim.Get(name));
        Assert.Equal([20.0], h.Sim.Writes.Where(w => w.Name == name).Select(w => w.Value));
        await Task.Delay(100);
        Assert.Equal(before, h.Commands.Count); // our own write: never mirrored
        Assert.Contains(h.Hub.Recent, e => e.Type == "release_it");
        // Up already: a second click writes nothing (never a down).
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => h.Engine.ReleaseItAsync(name));
        Assert.Single(h.Sim.Writes, w => w.Name == name);
    }

    private static void Inject(EngineHarness h, string name, string clientId) =>
        h.Channel.Inject("desync", new JsonObject
        {
            ["var"] = name, ["group"] = "overhead", ["reason"] = "stuck_pressed", ["expected"] = 0, ["observed"] = 1, ["tier"] = "red",
            ["client_id"] = clientId, ["values"] = new JsonObject { ["pf"] = 0, ["pm"] = 1 }, ["message"] = $"{name}: stuck pressed.",
        }, h.Channel.NextSeq());

    // ---------------------------------------------------------------- A: counter detector

    [Fact]
    public async Task A_switch_whose_values_keep_rising_is_a_suspected_counter_and_stops_syncing()
    {
        const string name = "S_OH_ELEC_APU_MASTER"; // [0, 1] in the fixture
        await using var h = await Start(true);
        for (var v = 1; v <= 5; v++)
        {
            h.Sim.PilotSet(name, v);
            await Task.Delay(30);
        }
        await Wait.Until(() => h.Channel.SentOf("counter_suspect").Count == 1, "counter suspected");
        Assert.Equal(2, h.Commands.Count(c => c["var"]!.GetValue<string>() == name)); // 1, 2 sent; from 3 on not synced
        Assert.False(h.Engine.CanSetBoth(name));
        Assert.True(h.Sim.Guard.IsBlocked(name));
        Assert.Throws<SafetyViolationException>(() => h.Sim.Guard.CheckWrite(name, 0));
        Assert.Contains(h.Hub.Recent, e => e.Type == "counter_suspect");
    }
}
