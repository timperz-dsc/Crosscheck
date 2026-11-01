using Crosscheck.Bridge.Sync;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// The sidestick takeover pushbutton (profile roles.takeover): on the PM's own seat a press is
/// also "I have control"; everywhere else it is just synced. In the harness the PF is the host
/// (captain by default) and the PM is the guest (fo by default).
/// </summary>
public class TakeoverTests
{
    private const string Capt = "S_FC_CAPT_INST_DISCONNECT";
    private const string Fo = "S_FC_FO_INST_DISCONNECT";

    [Fact]
    public async Task A_press_on_the_PMs_own_seat_is_synced_and_requests_control()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        Assert.Equal("fo", h.Status.SeatSide);

        h.Sim.PilotSet(Fo, 1);
        await Wait.Until(() => h.Channel.SentOf("control_request").Count == 1, "control request");
        var c = Assert.Single(h.Commands);
        Assert.Equal(Fo, c["var"]!.GetValue<string>());
        Assert.Equal(1, c["value"]!.GetValue<double>());
        Assert.Contains(h.Hub.Recent, e => e.Type == "takeover_pressed");

        // Releasing the button is synced too, but is not a second request.
        h.Sim.PilotSet(Fo, 0);
        await Wait.Until(() => h.Commands.Count == 2, "release synced");
        await Task.Delay(50);
        Assert.Single(h.Channel.SentOf("control_request"));
    }

    [Fact]
    public async Task On_the_PF_side_a_press_is_just_synced()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        Assert.Equal("captain", h.Status.SeatSide);

        h.Sim.PilotSet(Capt, 1);
        await Wait.Until(() => h.Commands.Count == 1, "press synced");
        await Task.Delay(50);
        Assert.Empty(h.Channel.SentOf("control_request"));
    }

    [Fact]
    public async Task The_other_seats_button_does_not_request_control()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false); // fo
        h.Sim.PilotSet(Capt, 1);
        await Wait.Until(() => h.Commands.Count == 1, "press synced");
        await Task.Delay(50);
        Assert.Empty(h.Channel.SentOf("control_request"));
    }

    [Fact]
    public async Task The_other_pilots_press_written_into_this_sim_is_not_a_request()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        var id = h.Remote(Fo, "verified", value: 1);
        Assert.True((await h.WaitAck(id))["ok"]!.GetValue<bool>());
        await Task.Delay(50);
        Assert.Empty(h.Channel.SentOf("control_request"));
        Assert.Empty(h.Commands); // our own write is an echo
    }

    [Fact]
    public async Task The_seat_side_option_picks_the_button()
    {
        await using var h = new EngineHarness(new EngineOptions { SeatSide = SeatSide.Captain });
        await h.StartAsync(pf: false); // guest, but sits on the left
        Assert.Equal("captain", h.Status.SeatSide);

        h.Sim.PilotSet(Fo, 1);
        await Wait.Until(() => h.Commands.Count == 1, "fo press synced");
        await Task.Delay(30);
        Assert.Empty(h.Channel.SentOf("control_request"));

        h.Sim.PilotSet(Capt, 1);
        await Wait.Until(() => h.Channel.SentOf("control_request").Count == 1, "control request from the captain's button");
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
