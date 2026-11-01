using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Sim;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// Starts the real session server from server/ (Node) on a free port, with its normal timings.
/// Needs Node 22+ and `npm install` in server/.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private Process? _process;
    public Uri Url { get; private set; } = null!;
    public string LogDir { get; } = Repo.TempDir("crosscheck-server-logs");

    public async Task InitializeAsync()
    {
        var serverDir = Path.Combine(Repo.Root, "server");
        var tsx = Path.Combine(serverDir, "node_modules", "tsx", "dist", "cli.mjs");
        if (!File.Exists(tsx))
            throw new InvalidOperationException("End-to-end tests need the session server: run `npm install` in server/ first.");
        var port = FreePort();
        var info = new ProcessStartInfo("node", $"\"{tsx}\" src/index.ts")
        {
            WorkingDirectory = serverDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["CROSSCHECK_PORT"] = port.ToString();
        info.Environment["CROSSCHECK_HOST"] = "127.0.0.1";
        info.Environment["CROSSCHECK_LOG_DIR"] = LogDir;
        _process = Process.Start(info)!;
        var ready = new TaskCompletionSource();
        _process.OutputDataReceived += (_, e) => { if (e.Data?.Contains("Crosscheck session server") == true) ready.TrySetResult(); };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Url = new Uri($"ws://127.0.0.1:{port}/ws");
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public Task DisposeAsync()
    {
        try { _process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _process?.WaitForExit(5000);
        try { Directory.Delete(LogDir, recursive: true); } catch (IOException) { }
        return Task.CompletedTask;
    }
}

[CollectionDefinition("server")]
public sealed class ServerCollection : ICollectionFixture<ServerFixture>;

/// <summary>Two bridges with fake sims against the real server: the whole chain without MSFS.</summary>
[Collection("server")]
public sealed class EndToEndTests(ServerFixture server) : IAsyncLifetime
{
    private readonly List<BridgeRuntime> _bridges = [];
    private readonly string _logs = Repo.TempDir("crosscheck-bridge-logs");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var b in _bridges) await b.DisposeAsync();
        // CROSSCHECK_KEEP_TEST_LOGS=1 keeps the bridge logs for debugging.
        if (Environment.GetEnvironmentVariable("CROSSCHECK_KEEP_TEST_LOGS") == "1") return;
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    private static FakeSimAdapter Sim(BridgeRuntime b) => (FakeSimAdapter)b.Sim;

    private async Task<BridgeRuntime> Bridge(SessionMode mode, string? code = null, string? profile = null, Action<FakeFenixGraphQl>? tweak = null, string name = "pilot")
    {
        var options = new BridgeOptions
        {
            Server = server.Url, ProfilePath = profile ?? Repo.FenixProfile, Mode = mode, JoinCode = code, Name = name,
            ApiPort = 0, LogDir = Path.Combine(_logs, name), VersionTimeout = TimeSpan.Zero, StrictValidation = true,
        };
        var b = new BridgeRuntime(options, p =>
        {
            var backends = BridgeBackends.Fake(p);
            tweak?.Invoke((FakeFenixGraphQl)backends.GraphQl);
            return backends;
        });
        _bridges.Add(b);
        await b.StartAsync(CancellationToken.None);
        return b;
    }

    /// <summary>Host (PF) and guest (PM) in one session, after the join resync settled.</summary>
    private async Task<(BridgeRuntime Host, BridgeRuntime Guest)> Pair(string? guestProfile = null)
    {
        var host = await Bridge(SessionMode.Create, name: "host");
        var guest = await Bridge(SessionMode.Join, host.JoinCode, guestProfile, name: "guest");
        await Task.Delay(400);
        return (host, guest);
    }

    [Fact]
    public async Task Switch_flip_syncs_and_is_acked()
    {
        var (host, guest) = await Pair();
        Assert.Equal("pf", host.Status.Role);
        Assert.Equal("pm", guest.Status.Role);

        Sim(host).PilotSet("S_OH_ELEC_APU_MASTER", 1);
        await Wait.Until(() => Sim(guest).Get("S_OH_ELEC_APU_MASTER") == 1, "guest switch");
        await Wait.Until(() => host.Engine.AcksOk == 1, "ack back at the host");
        await Wait.Until(() => Sim(guest).Get("I_OH_ELEC_APU_MASTER_L") == 1, "effect on the guest");
        Assert.Equal(0, guest.Engine.CommandsSent);
    }

    [Fact]
    public async Task Encoder_deltas_from_both_sides_sum_on_both_sims()
    {
        var (host, guest) = await Pair();
        var turns = new List<Task>();
        for (var i = 0; i < 5; i++)
        {
            Sim(host).PilotTurn("E_FCU_ALTITUDE", 1);
            if (i < 2) Sim(guest).PilotTurn("E_FCU_ALTITUDE", -1);
            await Task.Delay(40);
        }
        await Wait.Until(() => Sim(host).Get("N_FCU_ALTITUDE") == 3 && Sim(guest).Get("N_FCU_ALTITUDE") == 3,
            () => $"both displays at +3 (host display {Sim(host).Get("N_FCU_ALTITUDE")} counter {Sim(host).Get("E_FCU_ALTITUDE")} sent {host.Engine.CommandsSent}; " +
                  $"guest display {Sim(guest).Get("N_FCU_ALTITUDE")} counter {Sim(guest).Get("E_FCU_ALTITUDE")} sent {guest.Engine.CommandsSent}; " +
                  $"host writes [{string.Join(",", Sim(host).Writes.Select(w => w.Value))}] guest writes [{string.Join(",", Sim(guest).Writes.Select(w => w.Value))}])", 8000);
        await Task.Delay(500);
        Assert.True(Sim(host).Get("N_FCU_ALTITUDE") == 3 && Sim(guest).Get("N_FCU_ALTITUDE") == 3,
            $"after settling: host {Sim(host).Get("N_FCU_ALTITUDE")}, guest {Sim(guest).Get("N_FCU_ALTITUDE")}, logs {_logs}");
    }

    [Fact]
    public async Task A_key_pulse_arrives_exactly_once()
    {
        var (host, guest) = await Pair();
        await Sim(host).PilotPress("S_CDU1_KEY_FPLN");
        await Wait.Until(() => Sim(guest).McduText(1) == "FPLN", "guest MCDU");
        await Wait.Until(() => host.Engine.AcksOk == 1, "ack");
        await Task.Delay(500);
        Assert.Equal("FPLN", Sim(guest).McduText(1));
        Assert.Equal("FPLN", Sim(host).McduText(1));
        Assert.Equal(0, Sim(guest).Get("S_CDU1_KEY_FPLN"));
        Assert.Equal(0, guest.Engine.CommandsSent);
        Assert.Equal(1, host.Engine.CommandsSent);
    }

    [Fact]
    public async Task A_lost_command_is_retried_and_then_acked()
    {
        var (host, guest) = await Pair();
        guest.Engine.DropIncomingCommands(1);
        Sim(host).PilotSet("S_FCU_EFIS1_ND_MODE", 3);
        await Wait.Until(() => Sim(guest).Get("S_FCU_EFIS1_ND_MODE") == 3, "applied on the re-send", 5000);
        await Wait.Until(() => host.Engine.AcksOk == 1, "ack after retry");
        Assert.Empty(host.Status.OpenDesyncs);
    }

    [Fact]
    public async Task A_value_that_refuses_to_change_ends_in_a_desync_on_both_sides()
    {
        var (host, guest) = await Pair();
        var events = await ListenEvents(guest);
        Sim(guest).Refuse("S_OH_ELEC_APU_MASTER");

        Sim(host).PilotSet("S_OH_ELEC_APU_MASTER", 1);
        await Wait.Until(() => host.Status.OpenDesyncs.Contains("S_OH_ELEC_APU_MASTER") && guest.Status.OpenDesyncs.Contains("S_OH_ELEC_APU_MASTER"), "desync on both", 10000);
        await Wait.Until(() => { lock (events) return events.Any(e => e["type"]!.GetValue<string>() == "desync"); }, "desync on the local API event stream");
        var status = JsonNode.Parse(await new HttpClient().GetStringAsync($"http://127.0.0.1:{guest.Api!.Port}/status"))!;
        Assert.Contains(status["open_desyncs"]!.AsArray(), d => d!["reason"]!.GetValue<string>() == "refused");
    }

    [Fact]
    public async Task Network_drop_and_resume_applies_the_missed_commands()
    {
        var (host, guest) = await Pair();
        guest.Client.ForceDrop();
        await Wait.Until(() => host.Status.PeerState == "lost" || host.Status.PeerState == "resumed", "host sees the drop");
        Sim(host).PilotSet("S_OH_ELEC_APU_MASTER", 1);
        Sim(host).PilotSet("S_FCU_EFIS1_ND_MODE", 4);
        Sim(host).PilotTurn("E_FCU_ALTITUDE", 2);
        await Wait.Until(() => guest.Status.Server == "connected", "guest resumed", 8000);
        await Wait.Until(() =>
            Sim(guest).Get("S_OH_ELEC_APU_MASTER") == 1 && Sim(guest).Get("S_FCU_EFIS1_ND_MODE") == 4 && Sim(guest).Get("N_FCU_ALTITUDE") == 2,
            "missed commands applied", 8000);
        Assert.Equal(0, guest.Engine.CommandsSent);
    }

    [Fact]
    public async Task Hundred_rapid_changes_cause_no_echo_loop()
    {
        var (host, guest) = await Pair();
        for (var i = 0; i < 100; i++)
        {
            Sim(host).PilotSet("S_FCU_EFIS1_ND_MODE", i % 5);
            Sim(host).PilotTurn("E_FCU_ALTITUDE", 1);
            await Task.Delay(i % 3 == 0 ? 20 : 4); // some changes share a frame, some do not
        }
        await Wait.Until(() => Sim(guest).Get("N_FCU_ALTITUDE") == 100 && Sim(guest).Get("S_FCU_EFIS1_ND_MODE") == 4, "guest caught up", 20000);
        var sentBefore = host.Engine.CommandsSent;
        await Task.Delay(1500);
        Assert.Equal(0, guest.Engine.CommandsSent); // the receiver never sent anything back
        Assert.Equal(sentBefore, host.Engine.CommandsSent); // and nothing keeps bouncing
        Assert.Equal(100, Sim(host).Get("N_FCU_ALTITUDE"));
        Assert.Empty(host.Status.OpenDesyncs);
    }

    [Fact]
    public async Task Status_endpoint_shows_session_and_roles()
    {
        var (host, guest) = await Pair();
        using var http = new HttpClient();
        var hs = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{host.Api!.Port}/status"))!;
        var gs = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{guest.Api!.Port}/status"))!;
        Assert.Equal("pf", hs["role"]!.GetValue<string>());
        Assert.Equal("pm", gs["role"]!.GetValue<string>());
        Assert.Equal(hs["session_id"]!.GetValue<string>(), gs["session_id"]!.GetValue<string>());
        Assert.Equal("connected", hs["server"]!.GetValue<string>());
        Assert.Equal("connected", gs["sim"]!.GetValue<string>());
        Assert.Equal(FakeFenixGraphQl.FakeSoftwareVersion, gs["aircraft"]!["app_version"]!.GetValue<string>());

        using var foreign = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{host.Api.Port}/status");
        foreign.Headers.Add("Origin", "https://example.com");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);
    }

    [Fact]
    public async Task Control_is_granted_immediately_while_snapshots_differ_and_the_differences_are_listed()
    {
        var (host, guest) = await Pair();
        Sim(guest).Logic("I_MIP_GEAR_1_L", 1); // an observe var differs: a desync on both sides
        await Wait.Until(() => guest.Status.OpenDesyncs.Contains("I_MIP_GEAR_1_L"), "desync open", 10000);

        guest.Engine.RequestControl();
        await Wait.Until(() => guest.Status.Role == "pf" && host.Status.Role == "pm", "roles swapped", 3000);
        Assert.True(guest.Sim.Stream.IsPf); // the new PF's stream writer refuses frames
        Assert.False(host.Sim.Stream.IsPf); // the new PM follows

        var changed = guest.Events.Recent.Last(e => e.Type == "control_changed");
        Assert.Equal("take", changed.Data!["kind"]!.GetValue<string>());
        Assert.Contains("mip", changed.Data["differences"]!["groups"]!.AsArray().Select(g => g!.GetValue<string>()));
        Assert.Contains("I_MIP_GEAR_1_L", changed.Data["differences"]!["vars"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("Open differences", changed.Message);
        Assert.DoesNotContain(guest.Events.Recent, e => e.Type == "control_denied");
    }

    [Fact]
    public async Task The_PF_can_give_control_to_the_PM()
    {
        var (host, guest) = await Pair();
        host.Engine.GiveControl();
        await Wait.Until(() => guest.Status.Role == "pf" && host.Status.Role == "pm", "roles swapped", 3000);
        var changed = host.Events.Recent.Last(e => e.Type == "control_changed");
        Assert.Equal("give", changed.Data!["kind"]!.GetValue<string>());
        Assert.StartsWith("You gave control", changed.Message);
    }

    [Fact]
    public async Task The_PMs_takeover_pushbutton_takes_control_and_is_synced()
    {
        var (host, guest) = await Pair(); // guest sits right (fo) by default
        Sim(guest).PilotSet("S_FC_FO_INST_DISCONNECT", 1);
        await Wait.Until(() => guest.Status.Role == "pf" && host.Status.Role == "pm", "roles swapped", 3000);
        await Wait.Until(() => Sim(host).Get("S_FC_FO_INST_DISCONNECT") == 1, "press synced to the other sim");

        Sim(guest).PilotSet("S_FC_FO_INST_DISCONNECT", 0);
        await Wait.Until(() => Sim(host).Get("S_FC_FO_INST_DISCONNECT") == 0, "release synced");
        await Task.Delay(300);
        Assert.Equal("pf", guest.Status.Role); // the release is not a second request, and the host did not take it back
        Assert.Equal(0, host.Engine.CommandsSent);
    }

    [Fact]
    public async Task Preflight_rejects_a_different_aircraft_build()
    {
        var host = await Bridge(SessionMode.Create, name: "host");
        var e = await Assert.ThrowsAsync<JoinRejectedException>(() =>
            Bridge(SessionMode.Join, host.JoinCode, tweak: g => g.SoftwareVersion = "3.0.1.999", name: "guest"));
        Assert.Contains("aircraft.app_version", e.Message);
    }

    [Fact]
    public async Task A_bridge_runs_from_a_folder_with_åäö()
    {
        var dir = Path.Combine(_logs, "Prøfil åäö");
        Directory.CreateDirectory(dir);
        var profile = Path.Combine(dir, "profile.yaml");
        File.Copy(Repo.FenixProfile, profile);
        var (host, guest) = await Pair(guestProfile: profile);
        Sim(host).PilotSet("S_OH_ELEC_APU_MASTER", 1);
        await Wait.Until(() => Sim(guest).Get("S_OH_ELEC_APU_MASTER") == 1, "synced");
        Assert.True(File.Exists(guest.Log.Path));
    }

    private static async Task<List<JsonNode>> ListenEvents(BridgeRuntime b)
    {
        var list = new List<JsonNode>();
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{b.Api!.Port}/events"), CancellationToken.None);
        _ = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                lock (list) list.Add(JsonNode.Parse(Encoding.UTF8.GetString(buffer, 0, r.Count))!);
            }
        });
        return list;
    }
}
