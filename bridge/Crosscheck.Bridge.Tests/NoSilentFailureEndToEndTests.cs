using System.Net.Http;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Sim;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// CRO-26 against the real server: two bridges with fake sims. A failed input stays red and
/// visible, controls that are not synced are one amber line for both, key effects are compared.
/// </summary>
[Collection("server")]
public sealed class NoSilentFailureEndToEndTests(ServerFixture server) : IAsyncLifetime
{
    private readonly List<BridgeRuntime> _bridges = [];
    private readonly string _dir = Repo.TempDir("crosscheck-cro26");
    private readonly HttpClient _http = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var b in _bridges) await b.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FakeSimAdapter Sim(BridgeRuntime b) => (FakeSimAdapter)b.Sim;

    private async Task<BridgeRuntime> Bridge(SessionMode mode, string? code, string name)
    {
        var b = new BridgeRuntime(new BridgeOptions
        {
            Server = server.Url, ProfilePath = Repo.FixtureProfile, Mode = mode, JoinCode = code, Name = name, ApiPort = 0,
            LogDir = Path.Combine(_dir, name), VersionTimeout = TimeSpan.Zero, StrictValidation = true, UiDir = Path.Combine(_dir, "no-ui"),
        }, p => BridgeBackends.Fake(p));
        _bridges.Add(b);
        await b.StartAsync(CancellationToken.None);
        return b;
    }

    private async Task<(BridgeRuntime Host, BridgeRuntime Guest)> Pair()
    {
        var host = await Bridge(SessionMode.Create, null, "host");
        var guest = await Bridge(SessionMode.Join, host.JoinCode, "guest");
        await Task.Delay(400);
        return (host, guest);
    }

    private async Task<JsonObject> Status(BridgeRuntime b) =>
        JsonNode.Parse(await _http.GetStringAsync($"http://127.0.0.1:{b.Api!.Port}/api/status"))!.AsObject();

    [Fact]
    public async Task Replay_gate_test_1_counter_values_against_0_1_the_red_desync_stays_visible()
    {
        // The ELEC PUMP clicks from gate test 1 (anonymised extract), on the fixture's [0, 1] switch.
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.Root, "server", "test", "fixtures", "gate-test-1.json")))!;
        var commands = data["elec_pump"]!["commands"]!.AsArray()
            .Select(c => (T: c!["t_ms"]!.GetValue<int>(), Value: c["value"]!.GetValue<double>()))
            .Select(c => (T: c.T > 60_000 ? c.T - 800_000 : c.T, c.Value)).ToList(); // two series, gap shortened
        Assert.Equal(Enumerable.Range(1, 28).Select(i => (double)i), commands.Select(c => c.Value));

        var (host, guest) = await Pair();
        const string name = "S_OH_ELEC_APU_MASTER";
        var start = DateTime.UtcNow;
        foreach (var c in commands)
        {
            var wait = start.AddMilliseconds(c.T * 0.25) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            Sim(host).PilotSet(name, c.Value);
        }
        await Wait.Until(() => guest.Status.OpenDesyncs.Contains(name), "red desync on the guest", 10000);
        await Task.Delay(5000); // more snapshot rounds: nothing may wipe it

        foreach (var b in new[] { host, guest })
        {
            var s = await Status(b);
            var red = s["desyncs"]!.AsArray().Single(d => d!["var"]!.GetValue<string>() == name)!;
            Assert.Equal("red", red["tier"]!.GetValue<string>());
            Assert.DoesNotContain(s["indicators"]!.AsArray(), d => d!["var"]!.GetValue<string>() == name);
        }
        Assert.Equal(1, Sim(guest).Get(name)); // the guard never wrote a value outside [0, 1]
        Assert.Equal(28, Sim(host).Get(name));
    }

    [Fact]
    public async Task Replay_gate_test_1_elec_pump_as_a_press_counter_goes_through_with_no_desync()
    {
        // CRO-27: the same ELEC PUMP clicks, now profiled as in the real profile (pulse, press: counter,
        // effect lamp). Real click timing; only the 800 s gap between the two series is shortened.
        var data = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.Root, "server", "test", "fixtures", "gate-test-1.json")))!;
        const string name = "S_OH_HYD_YELLOW_ELEC_PUMP", lamp = "I_OH_HYD_YELLOW_ELEC_PUMP_L";
        Assert.Equal(name, data["elec_pump"]!["var"]!.GetValue<string>());
        var commands = data["elec_pump"]!["commands"]!.AsArray()
            .Select(c => (T: c!["t_ms"]!.GetValue<int>(), Value: c["value"]!.GetValue<double>()))
            .Select(c => (T: c.T > 60_000 ? c.T - 808_000 : c.T, c.Value)).ToList();
        Assert.Equal(Enumerable.Range(1, 28).Select(i => (double)i), commands.Select(c => c.Value));

        var (host, guest) = await Pair();
        var start = DateTime.UtcNow;
        foreach (var c in commands)
        {
            var wait = start.AddMilliseconds(c.T) - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            Sim(host).PilotSet(name, c.Value);
        }
        // 14 presses: the guest wrote press and release for each, counting up from its own 0 to 28.
        await Wait.Until(() => Sim(guest).Get(name) == 28, "guest counter at 28", 10000);
        await Task.Delay(3000); // effect checks and more snapshot rounds

        var writes = Sim(guest).Writes.Where(w => w.Name == name).Select(w => w.Value).ToList();
        Assert.Equal(Enumerable.Range(1, 28).Select(i => (double)i), writes);
        Assert.Equal(Sim(host).Get(lamp), Sim(guest).Get(lamp));
        foreach (var b in new[] { host, guest })
        {
            var s = await Status(b);
            Assert.DoesNotContain(s["desyncs"]!.AsArray(), d => d!["var"]!.GetValue<string>() is name or lamp);
            Assert.DoesNotContain(s["indicators"]!.AsArray(), d => d!["var"]!.GetValue<string>() == lamp);
            Assert.DoesNotContain(name, b.Status.OpenDesyncs);
        }
    }

    [Fact]
    public async Task An_analog_knob_outside_0_1_inside_its_range_is_confirmed_by_read_back()
    {
        // CRO-27: LDG ELEV went -3 … 7 in gate test 1 and was refused against [0, 1]; the GraphQL check
        // (x 1024) failed even inside it. Now: range [-3, 7], no GraphQL check, confirmed by read-back.
        var (host, guest) = await Pair();
        const string name = "A_OH_PNEUMATIC_LDG_ELEV";
        foreach (var v in new[] { -3, -2.75, 0.25, 6.5, 7 })
        {
            Sim(host).PilotSet(name, v);
            await Wait.Until(() => Sim(guest).Get(name) == v, $"guest at {v}", 5000);
        }
        await Task.Delay(1500);
        foreach (var b in new[] { host, guest })
            Assert.DoesNotContain(name, b.Status.OpenDesyncs);
        Assert.DoesNotContain(Sim(guest).Writes, w => w.Name == name && (w.Value < -3 || w.Value > 7));
    }

    [Fact]
    public async Task A_control_that_is_not_synced_is_one_amber_line_for_both_pilots()
    {
        var (host, guest) = await Pair();
        Sim(host).PilotSet("S_FCU_AP1", 1);
        await Wait.Until(() => guest.Status.Advisories.Count == 1 && host.Status.Advisories.Count == 1, "advisory on both sides");

        var g = (await Status(guest))["advisories"]!.AsArray().Single()!;
        Assert.Equal("not_synced", g["kind"]!.GetValue<string>());
        Assert.Equal("AP1", g["label"]!.GetValue<string>());
        Assert.False(g["mine"]!.GetValue<bool>());
        Assert.Equal("captain", g["who"]!.GetValue<string>());
        var h = (await Status(host))["advisories"]!.AsArray().Single()!;
        Assert.True(h["mine"]!.GetValue<bool>());
        Assert.Empty((await Status(guest))["desyncs"]!.AsArray());
        Assert.DoesNotContain(Sim(guest).Writes, w => w.Name == "S_FCU_AP1");
    }

    [Fact]
    public async Task A_key_whose_lamp_differs_after_the_press_is_red_on_both_sides()
    {
        var (host, guest) = await Pair();
        Sim(guest).Unpower("S_FCU_EFIS1_FD");
        await Sim(host).PilotPress("S_FCU_EFIS1_FD", holdMs: 20);
        await Wait.Until(() => guest.Status.OpenDesyncs.Contains("S_FCU_EFIS1_FD") && host.Status.OpenDesyncs.Contains("S_FCU_EFIS1_FD"),
            "effect desync on both sides", 5000);
        var red = (await Status(guest))["desyncs"]!.AsArray().Single(d => d!["var"]!.GetValue<string>() == "S_FCU_EFIS1_FD")!;
        Assert.Equal("effect_mismatch", red["reason"]!.GetValue<string>());
        Assert.Equal("red", red["tier"]!.GetValue<string>());
        Assert.Equal(0, red["you"]!["value"]!.GetValue<double>());
        Assert.Equal(1, red["other"]!["value"]!.GetValue<double>());
        Assert.False(red["writable"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_press_with_no_effect_on_either_side_is_not_a_failure()
    {
        var (host, guest) = await Pair();
        Sim(guest).Unpower("S_FCU_EFIS1_FD");
        Sim(host).Unpower("S_FCU_EFIS1_FD");
        await Sim(host).PilotPress("S_FCU_EFIS1_FD", holdMs: 20);
        await Wait.Until(() => host.Engine!.AcksOk == 1, "acked");
        await Task.Delay(1500);
        Assert.Empty(host.Status.OpenDesyncs);
        Assert.Empty(guest.Status.OpenDesyncs);
    }

    [Fact]
    public async Task Red_inputs_carry_lamps_of_their_group_as_likely_effects_and_other_lamps_are_quiet()
    {
        var (_, guest) = await Pair();
        // As the server sends them: a refused switch (red), its lamp and a gear light (quiet), and a
        // refused input wrongly marked quiet (must still be red).
        JsonObject D(string var, string group, string reason, string tier) => new()
        {
            ["var"] = var, ["group"] = group, ["reason"] = reason, ["tier"] = tier, ["expected"] = 1, ["observed"] = 0,
            ["values"] = new JsonObject { ["pf"] = 1, ["pm"] = 0 }, ["message"] = var,
        };
        guest.Status.SetDesync("S_OH_ELEC_APU_MASTER", D("S_OH_ELEC_APU_MASTER", "overhead", "refused", "red"));
        guest.Status.SetDesync("I_OH_ELEC_APU_MASTER_L", D("I_OH_ELEC_APU_MASTER_L", "overhead", "observe_mismatch", "quiet"));
        guest.Status.SetDesync("I_MIP_GEAR_1_L", D("I_MIP_GEAR_1_L", "mip", "observe_mismatch", "quiet"));
        guest.Status.SetDesync("A_OH_PNEUMATIC_COCKPIT_TEMP", D("A_OH_PNEUMATIC_COCKPIT_TEMP", "overhead", "refused", "quiet"));

        var s = await Status(guest);
        var red = s["desyncs"]!.AsArray().Select(d => d!["var"]!.GetValue<string>()).ToList();
        Assert.Equal(["S_OH_ELEC_APU_MASTER", "A_OH_PNEUMATIC_COCKPIT_TEMP"], red);
        var effects = s["desyncs"]![0]!["effects"]!.AsArray().Select(e => e!["var"]!.GetValue<string>()).ToList();
        Assert.Equal(["I_OH_ELEC_APU_MASTER_L"], effects);
        Assert.Empty(s["desyncs"]![1]!["effects"]!.AsArray()); // listed once, under the first red item of the group
        Assert.Equal(["I_MIP_GEAR_1_L"], s["indicators"]!.AsArray().Select(d => d!["var"]!.GetValue<string>()).ToList());
        var overhead = s["groups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "overhead")!;
        Assert.Equal(2, overhead["differs"]!.GetValue<int>()); // red only
        var mip = s["groups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "mip")!;
        Assert.Equal(0, mip["differs"]!.GetValue<int>());
    }
    [Fact]
    public async Task A_toggle_light_that_differs_is_red_never_pressed_automatically_and_press_here_only_fixes_it()
    {
        var (host, guest) = await Pair();
        const string Fd = "S_FCU_EFIS1_FD", Lamp = "I_FCU_EFIS1_FD";
        // As if the guest joined with FD in the other state (or missed a press while reconnecting).
        Sim(guest).Logic(Lamp, Sim(host).Get(Lamp) == 0 ? 1 : 0);

        JsonObject? Toggle(JsonObject s) => s["desyncs"]!.AsArray().OfType<JsonObject>()
            .FirstOrDefault(d => d["var"]!.GetValue<string>() == Fd && d["reason"]!.GetValue<string>() == "toggle_mismatch");
        JsonObject? item = null;
        await Wait.Until(() => (item = Toggle(Status(guest).GetAwaiter().GetResult())) is not null, "red toggle_mismatch", 20_000);
        Assert.Equal("red", item!["tier"]!.GetValue<string>());
        Assert.True(item["press_here"]!.GetValue<bool>());
        Assert.Equal(Sim(guest).Get(Lamp) == 1 ? ("on", "off") : ("off", "on"), (item["you"]!["text"]!.GetValue<string>(), item["other"]!["text"]!.GetValue<string>()));
        // Never pressed automatically, on either side.
        Assert.DoesNotContain(Sim(host).Writes, w => w.Name == Fd);
        Assert.DoesNotContain(Sim(guest).Writes, w => w.Name == Fd);

        // The guest uses "Press FD here only": one click in the guest's cockpit, not mirrored.
        var sent = guest.Engine!.CommandsSent;
        var r = await _http.PostAsync($"http://127.0.0.1:{guest.Api!.Port}/api/press-here",
            new StringContent("""{"var":"S_FCU_EFIS1_FD"}""", System.Text.Encoding.UTF8, "application/json"));
        Assert.True(r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync());
        await Wait.Until(() => Sim(guest).Get(Lamp) == Sim(host).Get(Lamp), "lights match");
        await Wait.Until(() => !guest.Status.OpenDesyncs.Contains(Fd) && !host.Status.OpenDesyncs.Contains(Fd), "cleared on both", 20_000);
        Assert.Equal(sent, guest.Engine!.CommandsSent); // not mirrored
        Assert.DoesNotContain(Sim(host).Writes, w => w.Name == Fd);
    }

    [Fact]
    public async Task A_toggle_light_is_listed_after_the_other_red_items_of_its_group()
    {
        var (_, guest) = await Pair();
        JsonObject D(string var, string reason) => new()
        {
            ["var"] = var, ["group"] = "efis", ["reason"] = reason, ["tier"] = "red", ["expected"] = 1, ["observed"] = 0,
            ["values"] = new JsonObject { ["pf"] = 1, ["pm"] = 0 }, ["message"] = var,
        };
        // FD went dark first (as with an unaligned IRS); the upstream input that differs was found later.
        guest.Status.SetDesync("S_FCU_EFIS1_FD", D("S_FCU_EFIS1_FD", "toggle_mismatch"));
        await Task.Delay(20);
        guest.Status.SetDesync("S_FCU_EFIS1_ND_MODE", D("S_FCU_EFIS1_ND_MODE", "refused"));
        var s = await Status(guest);
        var red = s["desyncs"]!.AsArray().Select(d => d!["var"]!.GetValue<string>()).ToList();
        Assert.Equal(["S_FCU_EFIS1_ND_MODE", "S_FCU_EFIS1_FD"], red);
    }
}
