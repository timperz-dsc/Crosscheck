using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// CRO-32: the FCU knob push/pull, synced since the supervised write test 2026-11-03. Fenix counts it: pull +1,
/// push -1 from the current value. Crosscheck mirrors the change only (delta), from the receiver's own value.
/// </summary>
public class FcuPushPullTests
{
    private static readonly Profile Real = PanelReplay.Real;

    private static async Task<EngineHarness> Start()
    {
        var h = new EngineHarness(profile: Real);
        await h.StartAsync(pf: false);
        return h;
    }

    [Fact]
    public async Task A_pull_and_a_push_from_the_other_cockpit_are_written_as_plus_one_and_minus_one_from_the_own_value()
    {
        await using var h = await Start();
        h.Sim.Logic("S_FCU_SPEED", 0);
        Assert.True((await h.WaitAck(h.Remote("S_FCU_SPEED", "delta", delta: 1)))["ok"]!.GetValue<bool>());
        Assert.True((await h.WaitAck(h.Remote("S_FCU_SPEED", "delta", delta: -1)))["ok"]!.GetValue<bool>());
        Assert.Equal([1.0, 0], h.Sim.WritesOf("S_FCU_SPEED"));
        Assert.Empty(h.Commands); // our own writes are never sent back as a pilot input
    }

    [Fact]
    public async Task Alt_rests_at_an_offset_and_is_mirrored_from_its_own_value()
    {
        await using var h = await Start();
        h.Sim.Logic("S_FCU_ALTITUDE", -3); // the resting value moved between the positions runs (-2, -3)
        Assert.True((await h.WaitAck(h.Remote("S_FCU_ALTITUDE", "delta", delta: 1)))["ok"]!.GetValue<bool>());
        Assert.True((await h.WaitAck(h.Remote("S_FCU_ALTITUDE", "delta", delta: -1)))["ok"]!.GetValue<bool>());
        Assert.Equal([-2.0, -3], h.Sim.WritesOf("S_FCU_ALTITUDE"));
    }

    [Fact]
    public async Task A_local_pull_and_push_are_sent_as_the_change_only()
    {
        await using var h = await Start();
        h.Sim.Logic("S_FCU_HEADING", 0);
        await Task.Delay(100);
        h.Sim.PilotSet("S_FCU_HEADING", 1);
        await Wait.Until(() => h.Commands.Count == 1, "pull sent");
        h.Sim.PilotSet("S_FCU_HEADING", 0);
        await Wait.Until(() => h.Commands.Count == 2, "push sent");
        Assert.Equal([1.0, -1], h.Commands.Select(c => c["delta"]!.GetValue<double>()));
        Assert.All(h.Commands, c => Assert.Null(c["value"]));
    }
}
