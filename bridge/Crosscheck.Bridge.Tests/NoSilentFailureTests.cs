using System.Text.Json;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Tests;

/// <summary>CRO-26: every failure is visible. Bridge side of effect checks, watch list, sim state, tiers.</summary>
public sealed class PulseEffectTests : IAsyncLifetime
{
    private readonly EngineHarness _h = new();

    public Task InitializeAsync() => _h.StartAsync(pf: true);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task A_key_with_a_lamp_sends_an_effect_check_and_reports_its_own_lamp_after_settle()
    {
        await _h.Sim.PilotPress("S_FCU_EFIS1_FD", holdMs: 20);
        await Wait.Until(() => _h.Commands.Count == 2, "down and up");
        var c = _h.Commands[0];
        Assert.Null(_h.Commands[1]["effect"]); // the lamp is compared after the down edge only
        Assert.Equal("I_FCU_EFIS1_FD", c["effect"]!["var"]!.GetValue<string>());
        Assert.Equal("same", c["effect"]!["expect"]!.GetValue<string>());

        await Wait.Until(() => _h.Channel.SentOf("effect_report").Count == 1, "effect report");
        var report = _h.Channel.SentOf("effect_report")[0];
        Assert.Equal(c["id"]!.GetValue<string>(), report["id"]!.GetValue<string>());
        Assert.Equal(1, report["value"]!.GetValue<double>()); // the lamp toggled 0 → 1 in this sim
    }

    [Fact]
    public async Task A_key_without_a_lamp_has_no_effect_check()
    {
        await _h.Sim.PilotPress("S_CDU1_KEY_FPLN", holdMs: 20);
        await Wait.Until(() => _h.Commands.Count == 2, "down and up");
        Assert.Null(_h.Commands[0]["effect"]);
    }

    [Fact]
    public async Task The_receiver_acks_with_its_lamp_and_does_not_judge_it()
    {
        var id = _h.Edge("S_FCU_EFIS1_FD", "down", settleMs: 100);
        var ack = await _h.WaitAck(id);
        Assert.True(ack["ok"]!.GetValue<bool>());
        Assert.Equal(1, ack["effect"]!.GetValue<double>());
        var up = await _h.WaitAck(_h.Edge("S_FCU_EFIS1_FD", "up", gapMs: 60, settleMs: 100));
        Assert.Null(up["effect"]); // no lamp check after an up edge

        // Unpowered: the press is stored, the lamp does not change. Still ok: the server compares both sims.
        _h.Sim.Unpower("S_FCU_EFIS1_FD");
        var id2 = _h.Edge("S_FCU_EFIS1_FD", "down", settleMs: 100);
        var ack2 = await _h.WaitAck(id2);
        Assert.True(ack2["ok"]!.GetValue<bool>());
        Assert.Equal(1, ack2["effect"]!.GetValue<double>());
    }
}

public sealed class DuplicateEventTests : IAsyncLifetime
{
    private readonly EngineHarness _h = new(new EngineOptions { AckCacheSize = 2 });

    public Task InitializeAsync() => _h.StartAsync(pf: true);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task A_key_press_is_never_applied_twice_even_after_its_ack_left_the_cache()
    {
        var id = _h.Remote("S_CDU1_KEY_FPLN", "pulse", 1, id: "c_other0001-press");
        await _h.WaitAck(id);
        var presses = _h.Sim.Writes.Count(w => w.Name == "S_CDU1_KEY_FPLN");
        for (var i = 0; i < 4; i++) await _h.WaitAck(_h.Remote("S_OH_ELEC_APU_MASTER", "verified", i % 2));

        _h.Remote("S_CDU1_KEY_FPLN", "pulse", 1, id: "c_other0001-press"); // the same id again
        await Task.Delay(300);
        Assert.Equal(presses, _h.Sim.Writes.Count(w => w.Name == "S_CDU1_KEY_FPLN"));
        Assert.Equal(2, _h.Channel.SentOf("ack").Count(a => a["id"]!.GetValue<string>() == id));
    }
}

public sealed class UnsyncedWatchTests : IAsyncLifetime
{
    private readonly EngineHarness _h = new(withUnsynced: true);

    public Task InitializeAsync() => _h.StartAsync(pf: true);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task Moving_a_control_that_is_not_synced_tells_both_pilots_and_is_never_written()
    {
        Assert.Contains("S_FCU_AP1", _h.Engine.Plan.Watched);
        _h.Sim.PilotSet("S_FCU_AP1", 1);
        await Wait.Until(() => _h.Channel.SentOf("unsynced_input").Count == 1, "unsynced_input");
        var msg = _h.Channel.SentOf("unsynced_input")[0];
        Assert.Equal("S_FCU_AP1", msg["var"]!.GetValue<string>());
        Assert.Equal("AP1", msg["label"]!.GetValue<string>());
        Assert.Empty(_h.Commands);

        _h.Sim.PilotSet("S_FCU_AP1", 0); // again within 2 s: no second message
        await Task.Delay(100);
        Assert.Single(_h.Channel.SentOf("unsynced_input"));

        await Assert.ThrowsAsync<SafetyViolationException>(() => _h.Sim.WriteAsync("S_FCU_AP1", 1));
        Assert.DoesNotContain(_h.Sim.Writes, w => w.Name == "S_FCU_AP1");
    }
}

public sealed class SimStateTests : IAsyncLifetime
{
    private readonly EngineHarness _h = new();

    public Task InitializeAsync() => _h.StartAsync(pf: true);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task A_disconnected_sim_is_reported_and_snapshot_requests_are_not_answered()
    {
        _h.Sim.SetConnected(false);
        await Wait.Until(() => _h.Channel.SentOf("sim_state").Count == 1, "sim_state");
        Assert.False(_h.Channel.SentOf("sim_state")[0]["connected"]!.GetValue<bool>());

        _h.Channel.Inject("snapshot_request", new JsonObject { ["request_id"] = "r9", ["groups"] = null, ["reason"] = "mismatch" });
        await Task.Delay(100);
        Assert.Empty(_h.Channel.SentOf("snapshot_values"));

        _h.Sim.SetConnected(true);
        await Wait.Until(() => _h.Channel.SentOf("sim_state").Count == 2, "sim_state again");
        Assert.True(_h.Channel.SentOf("sim_state")[1]["connected"]!.GetValue<bool>());
        _h.Channel.Inject("snapshot_request", new JsonObject { ["request_id"] = "r10", ["groups"] = null, ["reason"] = "resync" });
        await Wait.Until(() => _h.Channel.SentOf("snapshot_values").Count > 0, "answered");
    }

    [Fact]
    public async Task Snapshots_carry_the_drift_step_and_report_an_encoder_display_only_under_the_encoder()
    {
        _h.Channel.Inject("snapshot_request", new JsonObject { ["request_id"] = "r1", ["groups"] = new JsonArray("fcu", "systems"), ["reason"] = "mismatch" });
        await Wait.Until(() => _h.Channel.SentOf("snapshot_values").Count == 2, "values");
        var all = _h.Channel.SentOf("snapshot_values").SelectMany(v => v["values"]!.AsArray()).OfType<JsonObject>().ToList();
        Assert.DoesNotContain(all, v => v["var"]!.GetValue<string>() == "N_FCU_ALTITUDE");
        Assert.Contains(all, v => v["var"]!.GetValue<string>() == "E_FCU_ALTITUDE" && v["class"]!.GetValue<string>() == "delta");
        var fuel = all.Single(v => v["var"]!.GetValue<string>() == "aircraft.fuel.left.amount.kg");
        Assert.Equal(50, fuel["step"]!.GetValue<double>()); // half of drift_warn 100
    }
}

public sealed class UnsyncedListTests
{
    [Fact]
    public void Loads_names_and_labels_and_skips_profile_vars_and_non_controls()
    {
        var list = UnsyncedList.LoadNextTo(Repo.FixtureProfile, Repo.LoadFixture());
        Assert.Equal("AP1", list.Labels["S_FCU_AP1"]);
        Assert.Equal("RUD TRIM, knob", list.Labels["A_PED_RUDDER_TRIM"]); // quoted CSV
        Assert.False(list.Labels.ContainsKey("S_OH_ELEC_APU_MASTER")); // in the profile: synced
        Assert.False(list.Labels.ContainsKey("I_NOT_A_CONTROL")); // lamps are not controls
    }

    [Fact]
    public void Missing_file_means_an_empty_list()
    {
        var dir = Repo.TempDir("crosscheck-unsynced");
        Assert.Empty(UnsyncedList.LoadNextTo(Path.Combine(dir, "profile.yaml"), Repo.LoadFixture()).Labels);
    }

    [Fact]
    public void The_shipped_list_never_contains_a_profile_var()
    {
        var path = Path.Combine(Repo.Root, "profiles", "fenix-a320", "profile.yaml");
        var profile = Crosscheck.Profiles.ProfileLoader.LoadFile(path).Profile!;
        var list = UnsyncedList.LoadNextTo(path, profile);
        Assert.True(list.Labels.Count > 200);
        Assert.Contains("S_OH_ELEC_APU_START", list.Labels.Keys);
        Assert.DoesNotContain("S_FCU_AP1", list.Labels.Keys); // observed in CRO-29, now an edge button
        var inProfile = profile.Vars.Select(v => v.Name).ToHashSet();
        Assert.DoesNotContain(list.Labels.Keys, inProfile.Contains);
    }
}

public sealed class TierTests
{
    private sealed record TierCase(string Name, string Class, string Reason, bool DeltaDisplay, string Tier);

    [Fact]
    public void Matches_the_shared_tier_vectors()
    {
        var path = Path.Combine(Repo.Root, "protocol", "test-vectors", "desync-tiers.json");
        var cases = JsonNode.Parse(File.ReadAllText(path))!["cases"]!.AsArray().Select(c => new TierCase(
            c!["name"]!.GetValue<string>(), c["class"]!.GetValue<string>(), c["reason"]!.GetValue<string>(),
            c["delta_display"]!.GetValue<bool>(), c["tier"]!.GetValue<string>())).ToList();
        Assert.True(cases.Count > 10);
        foreach (var c in cases)
        {
            var cls = Enum.Parse<SyncClass>(c.Class, ignoreCase: true);
            // What the server sends (only observe may be quiet) and the bridge's own check agree.
            var serverTier = c.Class == "observe" ? "quiet" : "red";
            Assert.True(UiStatus.TierOf(cls, c.DeltaDisplay, c.Reason, serverTier) == c.Tier, c.Name);
        }
    }

    [Theory]
    [InlineData("verified", "refused")]
    [InlineData("pulse", "effect_mismatch")]
    [InlineData("observe", "refused")]
    [InlineData("delta", "correction_failed")]
    public void A_failed_input_is_red_even_if_marked_quiet(string cls, string reason)
    {
        Assert.Equal("red", UiStatus.TierOf(Enum.Parse<SyncClass>(cls, ignoreCase: true), false, reason, "quiet"));
    }
}
