using System.Net.Http;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Follow;
using Crosscheck.Bridge.Logging;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles;

namespace Crosscheck.Bridge.Tests;

/// <summary>0.6.1 diagnosis log: read-only, log file only, only values that are known to exist.</summary>
public sealed class DiagnosisLogTests
{
    private static readonly Dictionary<string, double> Lvars = new()
    {
        ["A320_Sound:ENG_1_N1"] = 0.62, ["A320_Sound:ENG_2_N1"] = 0.6, ["A320_Sound:Highest_brake_temp"] = 14.94,
        ["I_FCU_AP1"] = 1, ["I_FCU_ATHR"] = 1, ["B_FCU_VERTICALSPEED_DASHED"] = 1, ["I_FCU_EFIS1_FD"] = 0,
    };

    private static readonly Dictionary<string, double> Gql = new()
    {
        ["aircraft.fuel.left.amount.kg"] = 1500.4, ["aircraft.fuel.right.amount.kg"] = 1498.9, ["aircraft.fuel.center.amount.kg"] = 0,
        ["aircraft.fuel.ACT1.amount.kg"] = 0, ["aircraft.fuel.ACT2.amount.kg"] = 0, ["aircraft.fuel.total.amount.kg"] = 2999.3,
        ["aircraft.weight.zfw"] = 43830,
    };

    private static DiagnosisLog Make(List<(string Name, JsonObject Data)> events, PhysicsReading? reading = null, string? role = "pm",
        Func<string, CancellationToken, Task<object?>>? gql = null, double everyS = 5) =>
        new(name => Lvars.TryGetValue(name, out var v) ? v : null,
            gql ?? ((path, _) => Task.FromResult<object?>(Gql.TryGetValue(path, out var v) ? v : null)),
            () => reading ?? new PhysicsReading(12.3, true, 12.5, true, false),
            () => role, () => "following", (n, d) => { lock (events) events.Add((n, d)); }, everyS);

    [Fact]
    public async Task A_line_carries_the_known_values_and_leaves_out_what_is_not_known()
    {
        var o = await Make([]).SnapshotAsync(CancellationToken.None);
        Assert.Equal("pm", o["role"]!.GetValue<string>());
        Assert.Equal("following", o["mode"]!.GetValue<string>());
        Assert.Equal(12.3, o["own_gs_kt"]!.GetValue<double>());
        Assert.Equal(12.5, o["pf_gs_kt"]!.GetValue<double>());
        Assert.False(o["surfaces_on"]!.GetValue<bool>());
        Assert.Equal(1500.4, o["fuel_left_kg"]!.GetValue<double>());
        Assert.Equal(2999.3, o["fuel_total_kg"]!.GetValue<double>());
        Assert.Equal(43830, o["zfw_kg"]!.GetValue<double>());
        Assert.Equal(46829.3, o["gw_est_kg"]!.GetValue<double>(), 3); // zfw + fuel total, an estimate
        Assert.Equal(0.62, o["n1_1_raw"]!.GetValue<double>());
        Assert.Equal(14.94, o["brake_temp_max_raw"]!.GetValue<double>());
        var fcu = o["fcu"]!.AsObject();
        Assert.Equal(1, fcu["AP1"]!.GetValue<double>());
        Assert.Equal(1, fcu["VERTICALSPEED_DASHED"]!.GetValue<double>());
        Assert.False(fcu.ContainsKey("AP2")); // not read: left out, never 0
        // Nothing for vars that are not known to exist.
        Assert.DoesNotContain(o, kv => kv.Key.Contains("epr") || kv.Key.Contains("fd_") || kv.Key.Contains("fma"));
    }

    [Fact]
    public async Task A_PF_line_has_no_follower_fields_and_a_session_less_line_has_no_role()
    {
        var pf = await Make([], new PhysicsReading(60, false, null, null, null), role: "pf").SnapshotAsync(CancellationToken.None);
        Assert.Equal("pf", pf["role"]!.GetValue<string>());
        Assert.False(pf.ContainsKey("pf_gs_kt"));
        Assert.False(pf.ContainsKey("surfaces_on"));
        var none = await Make([], role: null).SnapshotAsync(CancellationToken.None);
        Assert.False(none.ContainsKey("role"));
    }

    [Fact]
    public async Task A_graphql_value_that_fails_or_hangs_is_left_out_and_the_line_still_goes()
    {
        var o = await Make([], gql: async (path, ct) =>
        {
            if (path.Contains("left")) throw new HttpRequestException("Fenix not there");
            if (path.Contains("right")) await Task.Delay(Timeout.Infinite, ct); // hangs until the timeout cancels it
            return Gql.GetValueOrDefault(path);
        }).SnapshotAsync(CancellationToken.None);
        Assert.False(o.ContainsKey("fuel_left_kg"));
        Assert.False(o.ContainsKey("fuel_right_kg"));
        Assert.Equal(2999.3, o["fuel_total_kg"]!.GetValue<double>());
    }

    [Fact]
    public void The_start_line_says_where_values_come_from_and_what_is_not_logged()
    {
        var s = DiagnosisLog.StartEvent();
        var missing = s["missing"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("epr_1_2", missing);
        Assert.Contains("fd_pitch_roll_bars", missing);
        Assert.Contains("gross_weight_live", missing);
        Assert.Contains("aircraft.fuel.left.amount.kg", s["graphql"]!.ToJsonString());
        Assert.Contains("A320_Sound:Highest_brake_temp", s["lvars"]!.ToJsonString());
    }

    [Fact]
    public async Task It_logs_the_start_line_and_then_a_line_every_period_until_stopped()
    {
        var events = new List<(string Name, JsonObject Data)>();
        using var cts = new CancellationTokenSource();
        var run = Make(events, everyS: 0.05).RunAsync(cts.Token);
        await Wait.Until(() => { lock (events) return events.Count(e => e.Name == "diag") >= 3; }, "three diag lines");
        cts.Cancel();
        await run;
        lock (events)
        {
            Assert.Equal("diag_start", events[0].Name);
            Assert.All(events.Skip(1), e => Assert.Equal("diag", e.Name));
        }
    }

    [Fact]
    public async Task A_full_line_is_small_enough_for_the_log()
    {
        // Worst case: every field present, long numbers.
        var o = await Make([], new PhysicsReading(123.456789, false, 123.456789, false, true)).SnapshotAsync(CancellationToken.None);
        var dir = Repo.TempDir("crosscheck-diag-size");
        try
        {
            var log = new BridgeLog(dir);
            log.Event("diag", o);
            var bytes = new FileInfo(log.Path!).Length;
            // 5 s period: 720 lines per hour. Reported in the 0.6.1 notes; the flight-1 logs were ~124 MB/h.
            Assert.InRange(bytes, 300, 1500);
            Assert.True(bytes * 720 < 1_200_000, $"{bytes} B per line = {bytes * 720} B/h");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_extra_vars_are_watched_but_the_engine_never_compares_syncs_or_shows_them()
    {
        var profile = Repo.LoadFixture();
        var plan = SyncEngine.PlanFor(profile, null, DiagnosisLog.ExtraLvars);
        Assert.All(DiagnosisLog.ExtraLvars, v => Assert.Contains(v, plan.Watched));
        // Without the extra list the plan is what it was: only profile vars.
        Assert.DoesNotContain("A320_Sound:ENG_1_N1", SyncEngine.PlanFor(profile).Watched);
    }
}

/// <summary>The diagnosis log in a running bridge: in the log file, never in the panel.</summary>
public sealed class DiagnosisLogRuntimeTests : IAsyncLifetime
{
    private readonly string _dir = Repo.TempDir("crosscheck-dl-runtime");
    private readonly HttpClient _http = new();
    private BridgeRuntime _bridge = null!;

    public async Task InitializeAsync()
    {
        _bridge = new BridgeRuntime(new BridgeOptions
        {
            Server = new Uri("ws://127.0.0.1:9/ws"), ProfilePath = Repo.FixtureProfile, Name = "solo", ApiPort = 0, LogDir = _dir,
            VersionTimeout = TimeSpan.Zero, StrictValidation = true, UiDir = Path.Combine(_dir, "no-ui"), DiagnosisEveryS = 0.1,
        }, p => BridgeBackends.Fake(p));
        await _bridge.StartAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _bridge.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private List<string> LogLines()
    {
        using var fs = new FileStream(_bridge.LogPath!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        var lines = new List<string>();
        while (r.ReadLine() is { } l) lines.Add(l);
        return lines;
    }

    [Fact]
    public async Task Diag_lines_go_to_the_log_file_and_never_to_the_panel_or_the_events()
    {
        await Wait.Until(() => LogLines().Count(l => l.Contains("\"event\":\"diag\"")) >= 2, "diag lines in the log");
        Assert.Contains(LogLines(), l => l.Contains("\"event\":\"diag_start\""));
        var status = await _http.GetStringAsync($"http://127.0.0.1:{_bridge.Api!.Port}/api/status");
        Assert.DoesNotContain("\"diag", status);
        Assert.DoesNotContain("n1_1_raw", status);
        Assert.DoesNotContain(_bridge.Events.Recent, e => e.Type.StartsWith("diag", StringComparison.Ordinal));
    }
}
