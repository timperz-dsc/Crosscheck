using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Sim;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// UI mode: bridges start idle and the local API drives create, preflight check, join, leave,
/// control and "Set both", against the real session server.
/// </summary>
[Collection("server")]
public sealed class UiApiTests(ServerFixture server) : IAsyncLifetime
{
    private readonly List<BridgeRuntime> _bridges = [];
    private readonly string _dir = Repo.TempDir("crosscheck-ui-api");
    private readonly HttpClient _http = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var b in _bridges) await b.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<BridgeRuntime> Idle(string name, Action<FakeFenixGraphQl>? tweak = null, string? uiDir = null)
    {
        var b = new BridgeRuntime(new BridgeOptions
        {
            Server = server.Url, ProfilePath = Repo.FenixProfile, Name = name, ApiPort = 0, LogDir = Path.Combine(_dir, name),
            VersionTimeout = TimeSpan.Zero, StrictValidation = true, UiDir = uiDir ?? Path.Combine(_dir, "no-ui"),
        }, p =>
        {
            var backends = BridgeBackends.Fake(p);
            tweak?.Invoke((FakeFenixGraphQl)backends.GraphQl);
            return backends;
        });
        _bridges.Add(b);
        await b.StartAsync(CancellationToken.None);
        return b;
    }

    private static FakeSimAdapter Sim(BridgeRuntime b) => (FakeSimAdapter)b.Sim;

    private string Url(BridgeRuntime b, string path) => $"http://127.0.0.1:{b.Api!.Port}{path}";

    private async Task<JsonObject> Status(BridgeRuntime b) => JsonNode.Parse(await _http.GetStringAsync(Url(b, "/api/status")))!.AsObject();

    private async Task<(HttpStatusCode Code, JsonObject Body)> Post(BridgeRuntime b, string path, object? body = null)
    {
        using var content = new StringContent(body is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var r = await _http.PostAsync(Url(b, path), content);
        return (r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject());
    }

    private async Task<(BridgeRuntime Host, BridgeRuntime Guest)> SessionViaApi()
    {
        var host = await Idle("host");
        var guest = await Idle("guest");
        Assert.Equal(HttpStatusCode.OK, (await Post(host, "/api/session/create")).Code);
        Assert.Equal(HttpStatusCode.OK, (await Post(guest, "/api/session/check", new { code = host.JoinCode })).Code);
        Assert.Equal(HttpStatusCode.OK, (await Post(guest, "/api/session/join", new { code = host.JoinCode })).Code);
        await Wait.Until(() => host.Status.PeerState == "joined", "host sees the guest");
        await Task.Delay(400); // join resync
        return (host, guest);
    }

    [Fact]
    public async Task Starts_idle_and_shows_this_computer()
    {
        var b = await Idle("solo");
        var s = await Status(b);
        Assert.Equal("idle", s["phase"]!.GetValue<string>());
        Assert.Null(s["start_blocked"]);
        var pc = s["this_computer"]!;
        Assert.True(pc["sim"]!["connected"]!.GetValue<bool>());
        Assert.Equal("Fenix A320", pc["aircraft"]!["name"]!.GetValue<string>());
        Assert.Equal("fenix-a320", pc["profile"]!["id"]!.GetValue<string>());
        Assert.Equal(server.Url.ToString(), pc["server"]!.GetValue<string>());
        Assert.Null(b.Engine);
    }

    [Fact]
    public async Task Sim_not_connected_blocks_start()
    {
        var b = await Idle("nosim");
        Sim(b).SetConnected(false);
        await Wait.Until(() => b.Status.Sim == "disconnected", "sim down");
        var s = await Status(b);
        Assert.Equal("sim_not_connected", s["start_blocked"]!.GetValue<string>());
        Assert.False(s["this_computer"]!["sim"]!["connected"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Create_waits_for_the_first_officer_then_check_and_join_from_the_ui()
    {
        var host = await Idle("host");
        var guest = await Idle("guest");
        Assert.Equal(HttpStatusCode.OK, (await Post(host, "/api/session/create")).Code);
        var hs = await Status(host);
        Assert.Equal("waiting", hs["phase"]!.GetValue<string>());
        Assert.Equal(host.JoinCode, hs["join_code"]!.GetValue<string>());

        var (code, _) = await Post(guest, "/api/session/check", new { code = host.JoinCode!.ToLowerInvariant() });
        Assert.Equal(HttpStatusCode.OK, code);
        var gs = await Status(guest);
        Assert.Equal("preflight", gs["phase"]!.GetValue<string>());
        var table = gs["preflight"]!;
        Assert.True(table["joinable"]!.GetValue<bool>());
        var rows = table["rows"]!.AsArray().ToDictionary(r => r!["field"]!.GetValue<string>(), r => r!["result"]!.GetValue<string>());
        Assert.Equal("same", rows["bridge_version"]);
        Assert.Equal("same", rows["profile"]);
        Assert.Equal("same", rows["aircraft.app_version"]);
        Assert.Equal("warn", rows["airac"]); // not given on either side: cannot be compared
        Assert.Null(host.Status.PeerState); // a check takes no slot

        Assert.Equal(HttpStatusCode.OK, (await Post(guest, "/api/session/join", new { code = host.JoinCode })).Code);
        await Wait.Until(() => host.Status.PeerState == "joined", "host sees the guest");
        hs = await Status(host);
        gs = await Status(guest);
        Assert.Equal("session", hs["phase"]!.GetValue<string>());
        Assert.Equal("session", gs["phase"]!.GetValue<string>());
        var crew = gs["crew"]!.AsArray();
        Assert.Contains(crew, c => c!["you"]!.GetValue<bool>() && c["side"]!.GetValue<string>() == "fo" && c["role"]!.GetValue<string>() == "pm");
        Assert.Contains(crew, c => !c!["you"]!.GetValue<bool>() && c["side"]!.GetValue<string>() == "captain" && c["role"]!.GetValue<string>() == "pf");
        Assert.Contains(gs["groups"]!.AsArray(), g => g!["label"]!.GetValue<string>() == "Main panel" && g["differs"]!.GetValue<int>() == 0);
        Assert.Contains(gs["owned"]!.AsArray(), o => o!["label"]!.GetValue<string>() == "Left tank fuel" && o["unit"]!.GetValue<string>() == "kg");
    }

    [Fact]
    public async Task Preflight_check_shows_a_blocking_mismatch_and_unknown_codes_are_explained()
    {
        var host = await Idle("host");
        var guest = await Idle("guest", g => g.SoftwareVersion = "3.0.1.999");
        await Post(host, "/api/session/create");

        var (badCode, bad) = await Post(guest, "/api/session/check", new { code = "ZZZZZZ" });
        Assert.Equal(HttpStatusCode.Conflict, badCode);
        Assert.Contains("No session with the code ZZZZZZ", bad["error"]!.GetValue<string>());
        Assert.Equal("idle", (await Status(guest))["phase"]!.GetValue<string>());

        await Post(guest, "/api/session/check", new { code = host.JoinCode });
        var table = (await Status(guest))["preflight"]!;
        Assert.False(table["joinable"]!.GetValue<bool>());
        var app = table["rows"]!.AsArray().Single(r => r!["field"]!.GetValue<string>() == "aircraft.app_version")!;
        Assert.Equal("reject", app["result"]!.GetValue<string>());
        Assert.Equal("3.0.1.999", app["you"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.OK, (await Post(guest, "/api/session/cancel")).Code);
        Assert.Equal("idle", (await Status(guest))["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task Unknown_aircraft_version_is_a_warning_and_the_join_goes_through()
    {
        var host = await Idle("host");
        var guest = await Idle("guest", g => g.Available = false); // version unreadable → "unknown"
        await Post(host, "/api/session/create");
        await Post(guest, "/api/session/check", new { code = host.JoinCode });
        var table = (await Status(guest))["preflight"]!;
        Assert.True(table["joinable"]!.GetValue<bool>());
        Assert.Equal("warn", table["rows"]!.AsArray().Single(r => r!["field"]!.GetValue<string>() == "aircraft.app_version")!["result"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.OK, (await Post(guest, "/api/session/join", new { code = host.JoinCode })).Code);
    }

    [Fact]
    public async Task Set_both_sends_a_pilot_command_from_this_seat_and_clears_the_desync()
    {
        var (host, guest) = await SessionViaApi();
        Sim(guest).Refuse("S_OH_ELEC_APU_MASTER");
        Sim(host).PilotSet("S_OH_ELEC_APU_MASTER", 1);
        await Wait.Until(() => host.Status.OpenDesyncs.Contains("S_OH_ELEC_APU_MASTER"), "desync", 10000);

        var d = (await Status(host))["desyncs"]!.AsArray().Single()!;
        Assert.Equal("APU MASTER", d["label"]!.GetValue<string>());
        Assert.Equal("Overhead", d["group_label"]!.GetValue<string>());
        Assert.Equal("ON", d["you"]!["text"]!.GetValue<string>());
        Assert.Equal("OFF", d["other"]!["text"]!.GetValue<string>());
        Assert.True(d["writable"]!.GetValue<bool>());
        Assert.Equal(["ON", "OFF"], d["options"]!.AsArray().Select(o => o!["text"]!.GetValue<string>()));
        Assert.Equal(1, (await Status(host))["groups"]!.AsArray().Single(g => g!["id"]!.GetValue<string>() == "overhead")!["differs"]!.GetValue<int>());

        Sim(guest).Unstick("S_OH_ELEC_APU_MASTER");
        var sentBefore = host.Engine!.CommandsSent;
        Assert.Equal(HttpStatusCode.OK, (await Post(host, "/api/set", new { var = "S_OH_ELEC_APU_MASTER", value = 0 })).Code);
        await Wait.Until(() => Sim(guest).Get("S_OH_ELEC_APU_MASTER") == 0 && Sim(host).Get("S_OH_ELEC_APU_MASTER") == 0, "both off");
        await Wait.Until(() => host.Status.OpenDesyncs.Count == 0 && guest.Status.OpenDesyncs.Count == 0, "desync cleared", 10000);
        await Task.Delay(100);
        Assert.Equal(sentBefore + 1, host.Engine.CommandsSent); // one pilot command, the own write was not echoed

        // Never outside the profile, never a non-switch.
        Assert.Equal(HttpStatusCode.Conflict, (await Post(host, "/api/set", new { var = "S_OH_ELEC_APU_MASTER", value = 7 })).Code);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(host, "/api/set", new { var = "I_MIP_GEAR_1_L", value = 1 })).Code);
    }

    [Fact]
    public async Task Control_buttons_drive_the_handover_and_the_new_pf_gets_the_notice()
    {
        var (host, guest) = await SessionViaApi();
        Sim(guest).Logic("I_MIP_GEAR_1_L", 1);
        await Wait.Until(() => guest.Status.OpenDesyncs.Contains("I_MIP_GEAR_1_L"), "desync", 10000);

        Assert.Equal(HttpStatusCode.OK, (await Post(guest, "/api/control/take")).Code);
        await Wait.Until(() => guest.Status.Role == "pf", "guest is PF", 3000);
        var notice = (await Status(guest))["control_notice"]!;
        Assert.Contains("Main panel", notice["groups"]!.AsArray().Select(g => g!.GetValue<string>()));
        Assert.Contains("Gear indicator", notice["vars"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Null((await Status(host))["control_notice"]);

        Assert.Equal(HttpStatusCode.OK, (await Post(guest, "/api/control/give")).Code);
        await Wait.Until(() => host.Status.Role == "pf", "host is PF again", 3000);
    }

    [Fact]
    public async Task Recent_lists_actions_in_words()
    {
        var (host, guest) = await SessionViaApi();
        Sim(host).PilotSet("S_OH_ELEC_APU_MASTER", 1);
        await Wait.Until(() => host.Engine!.AcksOk == 1, "ack");
        await Wait.Until(() => guest.Events.Recent.Any(e => e.Type == "action_applied"), "applied on the guest");
        var hostRecent = JsonNode.Parse(await _http.GetStringAsync(Url(host, "/api/recent")))!.AsArray();
        var guestRecent = JsonNode.Parse(await _http.GetStringAsync(Url(guest, "/api/recent")))!.AsArray();
        Assert.Contains(hostRecent, e => e!["message"]!.GetValue<string>() == "APU MASTER on by you. Confirmed.");
        Assert.Contains(guestRecent, e => e!["message"]!.GetValue<string>() == "APU MASTER on by captain. Confirmed.");
    }

    [Fact]
    public async Task Fuel_drift_is_shown_in_kg_and_never_corrected()
    {
        var host = await Idle("host");
        FakeFenixGraphQl? guestGql = null;
        var guest = await Idle("guest", g => guestGql = g);
        await Post(host, "/api/session/create");
        await Post(guest, "/api/session/join", new { code = host.JoinCode });
        await Task.Delay(400);
        guestGql!.Datarefs["aircraft.fuel.left.amount.kg"] = 1488.0;
        await Wait.Until(() => host.Engine!.Drift.ContainsKey("aircraft.fuel.left.amount.kg"), "drift reported", 15000);
        var fuel = (await Status(host))["owned"]!.AsArray().Single()!;
        Assert.Equal(-12, fuel["delta"]!.GetValue<double>());
        Assert.Empty(host.Status.OpenDesyncs);
        Assert.Empty(guest.Status.OpenDesyncs);
    }

    [Fact]
    public async Task Fuel_that_differs_at_join_is_a_warning_until_both_load_the_same()
    {
        var host = await Idle("host");
        FakeFenixGraphQl? guestGql = null;
        var guest = await Idle("guest", g =>
        {
            guestGql = g;
            g.Datarefs["aircraft.fuel.left.amount.kg"] = 1100.0; // loaded differently in the EFB
        });
        await Post(host, "/api/session/create");
        await Post(guest, "/api/session/join", new { code = host.JoinCode });
        await Wait.Until(() => guest.Engine!.DriftWarnings.Count == 1, "warning at the start", 10000);

        var fuel = (await Status(guest))["owned"]!.AsArray().Single()!;
        Assert.True(fuel["warn"]!.GetValue<bool>());
        Assert.Equal(400, Math.Abs(fuel["delta"]!.GetValue<double>()));
        Assert.Empty(guest.Status.OpenDesyncs); // no red, no desync
        Assert.Equal(1100.0, guestGql!.Datarefs["aircraft.fuel.left.amount.kg"]); // never written

        guestGql.Datarefs["aircraft.fuel.left.amount.kg"] = 1450.0; // now within 100 kg
        await Wait.Until(() => guest.Engine!.DriftWarnings.Count == 0, "warning resolved", 15000);
        Assert.False((await Status(guest))["owned"]!.AsArray().Single()!["warn"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Leave_returns_to_the_start_and_a_new_session_can_be_created()
    {
        var (host, _) = await SessionViaApi();
        Assert.Equal(HttpStatusCode.OK, (await Post(host, "/api/session/leave")).Code);
        Assert.Equal("idle", (await Status(host))["phase"]!.GetValue<string>());
        Assert.Null(host.Engine);
        Assert.Equal(HttpStatusCode.OK, (await Post(host, "/api/session/create")).Code);
        Assert.Equal("waiting", (await Status(host))["phase"]!.GetValue<string>());
    }

    [Fact]
    public async Task Serves_the_ui_and_refuses_foreign_hosts_and_non_json_posts()
    {
        var ui = Path.Combine(_dir, "ui");
        Directory.CreateDirectory(ui);
        await File.WriteAllTextAsync(Path.Combine(ui, "index.html"), "<!doctype html><title>Crosscheck</title><div id=root></div>");
        var b = await Idle("web", uiDir: ui);

        Assert.Contains("id=root", await _http.GetStringAsync(Url(b, "/")));
        Assert.Contains("id=root", await _http.GetStringAsync(Url(b, "/some/deep/link"))); // SPA fallback

        using var rebinding = new HttpRequestMessage(HttpMethod.Get, Url(b, "/api/status"));
        rebinding.Headers.Host = "evil.example.com";
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(rebinding)).StatusCode);

        using var form = new StringContent("code=ABCDEF", Encoding.UTF8, "application/x-www-form-urlencoded");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await _http.PostAsync(Url(b, "/api/session/create"), form)).StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Post, Url(b, "/api/session/create")) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        foreign.Headers.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await _http.SendAsync(foreign)).StatusCode);
        Assert.Null(b.Engine);
    }
}
