using System.Text.Json.Nodes;
using Crosscheck.Bridge.Sync;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// The sidestick takeover pushbuttons (profile roles.takeover, CRO-29/30, protocol.md §8). A takeover press
/// is never written into the sim of the pilot who is flying: the pilot monitoring's press requests control
/// and is held back; once control is granted it is mirrored as a normal press of the new PF, otherwise not
/// at all. In the harness the PF is the host (captain) and the PM is the guest (fo).
/// </summary>
public class TakeoverTests
{
    private const string Capt = "S_FC_CAPT_INST_DISCONNECT";
    private const string Fo = "S_FC_FO_INST_DISCONNECT";

    private static void Granted(EngineHarness h, string pf) => h.Channel.Inject("control_changed", new JsonObject
    {
        ["epoch"] = 2, ["kind"] = "take", ["pf_client_id"] = pf, ["pf_seat"] = "guest", ["previous_pf_client_id"] = EngineHarness.OtherClient,
        ["confirmed"] = new JsonArray(), ["differences"] = new JsonObject { ["groups"] = new JsonArray(), ["vars"] = new JsonArray() },
        ["desyncs"] = new JsonArray(), ["last_frame"] = null,
    });

    [Fact]
    public async Task A_PM_press_requests_control_and_is_mirrored_only_after_the_grant()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        Assert.Equal("fo", h.Status.SeatSide);

        h.Sim.PilotSet(Fo, 1); // down
        await Wait.Until(() => h.Channel.SentOf("control_request").Count == 1, "control request");
        Assert.Contains(h.Hub.Recent, e => e.Type == "takeover_pressed");
        await Task.Delay(80);
        h.Sim.PilotSet(Fo, 2); // released while the request is open
        await Task.Delay(50);
        Assert.Empty(h.Commands); // held back: never into the flying pilot's sim

        Granted(h, h.Channel.ClientId!);
        await Wait.Until(() => h.Commands.Count == 2, "held press mirrored after the grant");
        Assert.Equal(["down", "up"], h.Commands.Select(c => c["edge"]!.GetValue<string>()));
        Assert.InRange(h.Commands[1]["gap_ms"]!.GetValue<int>(), 60, 1000); // the hold as it was
        Assert.Single(h.Channel.SentOf("control_request")); // the release is not a second request

        // Now PF: later presses go out at once.
        h.Sim.PilotSet(Fo, 3);
        await Wait.Until(() => h.Commands.Count == 3, "press as PF");
        Assert.Single(h.Channel.SentOf("control_request"));
    }

    [Fact]
    public async Task A_denied_request_mirrors_nothing()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        h.Sim.PilotSet(Fo, 1);
        await Wait.Until(() => h.Channel.SentOf("control_request").Count == 1, "control request");
        h.Channel.Inject("control_denied", new JsonObject { ["reason"] = "peer_lost", ["message"] = "The pilot flying is not connected.", ["desyncs"] = new JsonArray() });
        h.Sim.PilotSet(Fo, 2);
        await Wait.Until(() => h.Hub.Recent.Any(e => e.Type == "takeover_dropped"), "dropped notice");
        await Task.Delay(100);
        Assert.Empty(h.Commands);

        // A later grant (from another request) does not bring the old press back.
        Granted(h, h.Channel.ClientId!);
        await Task.Delay(100);
        Assert.Empty(h.Commands);
    }

    [Fact]
    public async Task No_answer_in_time_mirrors_nothing()
    {
        await using var h = new EngineHarness(new EngineOptions { TakeoverGrantTimeoutMs = 150 });
        await h.StartAsync(pf: false);
        h.Sim.PilotSet(Fo, 1);
        await Wait.Until(() => h.Hub.Recent.Any(e => e.Type == "takeover_dropped"), "dropped after the timeout");
        Granted(h, h.Channel.ClientId!);
        await Task.Delay(100);
        Assert.Empty(h.Commands);
    }

    [Fact]
    public async Task The_other_seats_button_pressed_as_PM_is_held_back_too()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false); // fo
        h.Sim.PilotSet(Capt, 1);
        await Wait.Until(() => h.Channel.SentOf("control_request").Count == 1, "control request");
        await Task.Delay(50);
        Assert.Empty(h.Commands);
    }

    [Fact]
    public async Task On_the_PF_side_a_press_is_mirrored_to_the_PM()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        Assert.Equal("captain", h.Status.SeatSide);

        await h.Sim.PilotPress(Capt, holdMs: 60);
        await Wait.Until(() => h.Commands.Count == 2, "down and up");
        await Task.Delay(50);
        Assert.Empty(h.Channel.SentOf("control_request"));
    }

    [Fact]
    public async Task A_PF_never_writes_a_takeover_press_into_its_own_sim()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        var ack = await h.WaitAck(h.Edge(Fo, "down"));
        Assert.False(ack["ok"]!.GetValue<bool>()); // refused: the server raises a red desync
        await Task.Delay(50);
        Assert.DoesNotContain(h.Sim.Writes, w => w.Name == Fo);
    }

    [Fact]
    public async Task The_new_PFs_press_is_written_into_the_PMs_sim_and_is_not_a_request()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        Assert.True((await h.WaitAck(h.Edge(Capt, "down")))["ok"]!.GetValue<bool>());
        Assert.True((await h.WaitAck(h.Edge(Capt, "up", gapMs: 100)))["ok"]!.GetValue<bool>());
        Assert.Equal([1.0, 2.0], h.Sim.Writes.Where(w => w.Name == Capt).Select(w => w.Value));
        await Task.Delay(50);
        Assert.Empty(h.Channel.SentOf("control_request"));
        Assert.Empty(h.Commands); // our own writes are echoes
    }

    [Fact]
    public async Task Give_control_sends_control_give()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        h.Engine.GiveControl();
        Assert.Single(h.Channel.SentOf("control_give"));
    }
}
