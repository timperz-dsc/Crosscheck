using System.Text.Json.Nodes;
using Crosscheck.Bridge.Sync;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// Spring-loaded switches (verified with rest, gate test 2 prep): every change is mirrored, so a hold lasts as
/// long on both sides. If the release of a position Crosscheck wrote never arrives, Crosscheck writes the rest
/// value back, with a red desync (owner decision, protocol.md section 5). Nothing else is ever written.
/// </summary>
public sealed class SpringReleaseTests
{
    private const string Trim = "S_FC_RUDDER_TRIM";

    private static async Task<EngineHarness> Pm(EngineOptions? options = null)
    {
        var h = new EngineHarness(options);
        h.Sim.Logic(Trim, 1); // at rest before the session starts
        await h.StartAsync(pf: false);
        await Task.Delay(50); // a few frames: the engine has seen the rest value
        return h;
    }

    [Fact]
    public async Task A_hold_and_its_release_are_mirrored_and_nothing_else_is_written()
    {
        await using var h = await Pm(new EngineOptions { SafetyReleaseMs = 300 });
        Assert.True((await h.WaitAck(h.Remote(Trim, "verified", 0)))["ok"]!.GetValue<bool>());
        Assert.True((await h.WaitAck(h.Remote(Trim, "verified", 1)))["ok"]!.GetValue<bool>());
        await Task.Delay(500); // longer than the cap: the release came, so nothing to do
        Assert.Equal([0.0, 1.0], h.Sim.Writes.Where(w => w.Name == Trim).Select(w => w.Value));
        Assert.Empty(h.Channel.SentOf("safety_release"));
    }

    [Fact]
    public async Task The_cap_returns_it_to_rest_with_a_red_desync()
    {
        await using var h = await Pm(new EngineOptions { SafetyReleaseMs = 200 });
        Assert.True((await h.WaitAck(h.Remote(Trim, "verified", 2)))["ok"]!.GetValue<bool>());
        await Wait.Until(() => h.Channel.SentOf("safety_release").Count == 1, "safety release");
        var msg = h.Channel.SentOf("safety_release")[0];
        Assert.Equal((Trim, "cap", "flightcontrols", 1.0),
            (msg["var"]!.GetValue<string>(), msg["cause"]!.GetValue<string>(), msg["group"]!.GetValue<string>(), msg["observed"]!.GetValue<double>()));
        Assert.Equal([2.0, 1.0], h.Sim.Writes.Where(w => w.Name == Trim).Select(w => w.Value));
        Assert.DoesNotContain(h.Sim.Writes, w => w.Name != Trim);
        Assert.Contains(Trim, h.Status.OpenDesyncs);
        Assert.Contains(h.Hub.Recent, e => e.Type == "safety_release" && e.Message.Contains("returned RUD TRIM to neutral"));
        Assert.Empty(h.Commands); // the rest write is not sent back as a pilot input
    }

    [Fact]
    public async Task Link_loss_while_held_returns_it_to_rest_at_once()
    {
        await using var h = await Pm();
        Assert.True((await h.WaitAck(h.Remote(Trim, "verified", 0)))["ok"]!.GetValue<bool>());
        h.Channel.SetState(Crosscheck.Bridge.Protocol.ServerState.Reconnecting);
        await Wait.Until(() => h.Sim.Get(Trim) == 1, "returned to rest");
        h.Channel.SetState(Crosscheck.Bridge.Protocol.ServerState.Connected);
        h.Channel.RaiseResumed(false);
        await Wait.Until(() => h.Channel.SentOf("safety_release").Count == 1, "sent after resume");
        Assert.Equal("link_lost", h.Channel.SentOf("safety_release")[0]["cause"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_switch_the_local_pilot_moved_is_never_returned()
    {
        await using var h = await Pm(new EngineOptions { SafetyReleaseMs = 200 });
        Assert.True((await h.WaitAck(h.Remote(Trim, "verified", 0)))["ok"]!.GetValue<bool>());
        h.Sim.PilotSet(Trim, 2); // the local pilot turned it the other way
        await Task.Delay(400);
        Assert.Empty(h.Channel.SentOf("safety_release"));
        Assert.Equal([0.0], h.Sim.Writes.Where(w => w.Name == Trim).Select(w => w.Value));
    }

    [Fact]
    public async Task Without_rest_a_switch_is_never_written_back()
    {
        await using var h = new EngineHarness(new EngineOptions { SafetyReleaseMs = 200 });
        await h.StartAsync(pf: false);
        Assert.True((await h.WaitAck(h.Remote("S_FCU_EFIS1_ND_MODE", "verified", 3)))["ok"]!.GetValue<bool>());
        h.Channel.SetState(Crosscheck.Bridge.Protocol.ServerState.Reconnecting);
        await Task.Delay(400);
        Assert.Equal([3.0], h.Sim.Writes.Where(w => w.Name == "S_FCU_EFIS1_ND_MODE").Select(w => w.Value));
        Assert.Empty(h.Channel.SentOf("safety_release"));
    }
}

/// <summary>Indicators with an alarm (FCU managed lamps, gate test 2 prep): compared as inputs, never quiet, never written.</summary>
public sealed class ModeSnapshotTests : IAsyncLifetime
{
    private readonly EngineHarness _h = new();

    public Task InitializeAsync() => _h.StartAsync(pf: true);
    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Alarm_indicators_are_mode_items_with_the_text_and_the_control_they_belong_to()
    {
        _h.Sim.Logic("I_FCU_SPEED_MANAGED", 1);
        await Wait.Until(() => _h.Sim.Get("I_FCU_SPEED_MANAGED") == 1, "lamp");
        var fcu = _h.Engine.GroupValues("fcu");
        Assert.Contains(("I_FCU_SPEED_MANAGED", "mode", (object?)1.0), fcu);
        Assert.Contains(fcu, v => v.Var == "B_FCU_SPEED_DASHED" && v.Class == "mode");
        Assert.DoesNotContain(fcu, v => v.Var == "I_FCU_SPEED_MANAGED" && v.Class == "observe");

        _h.Channel.Inject("snapshot_request", new JsonObject { ["request_id"] = "r1", ["groups"] = new JsonArray("fcu"), ["reason"] = "mismatch" });
        await Wait.Until(() => _h.Channel.SentOf("snapshot_values").Count == 1, "values");
        var dashed = _h.Channel.SentOf("snapshot_values")[0]["values"]!.AsArray().Single(v => v!["var"]!.GetValue<string>() == "B_FCU_SPEED_DASHED")!;
        Assert.Equal("I_FCU_SPEED_MANAGED", dashed["report_as"]!.GetValue<string>());
        Assert.StartsWith("SPD managed differs between the cockpits", dashed["message"]!.GetValue<string>());
    }

    [Fact]
    public void A_mode_mismatch_is_red_even_if_marked_quiet() =>
        Assert.Equal("red", Crosscheck.Bridge.Api.UiStatus.TierOf(Crosscheck.Profiles.Model.SyncClass.Observe, false, "mode_mismatch", "quiet"));
}
