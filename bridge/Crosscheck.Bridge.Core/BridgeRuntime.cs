using System.Reflection;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Follow;
using Crosscheck.Bridge.Logging;
using Crosscheck.Bridge.Protocol;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crosscheck.Bridge;

public enum SessionMode
{
    Create,
    Join,
}

public sealed record BridgeOptions
{
    public required Uri Server { get; init; }
    public required string ProfilePath { get; init; }
    /// <summary>Create or join right at start (headless). null = wait for the UI.</summary>
    public SessionMode? Mode { get; init; }
    public string? JoinCode { get; init; }
    public string? Name { get; init; }
    /// <summary>Local API port; 0 = any free port, -1 = off.</summary>
    public int ApiPort { get; init; } = 8790;
    /// <summary>Folder with the built UI (index.html). null = wwwroot next to the program.</summary>
    public string? UiDir { get; init; }
    /// <summary>null = default (%LOCALAPPDATA%\CrosscheckData\logs).</summary>
    public string? LogDir { get; init; }
    /// <summary>Where the server address set in the UI is saved. null = not saved (tests).</summary>
    public string? SettingsPath { get; init; }
    public string? Airac { get; init; }
    public TimeSpan VersionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Physics stream and follower settings (CRO-13): rate, playout buffer, extrapolation cap.</summary>
    public PhysicsOptions Physics { get; init; } = new();
    /// <summary>The diagnosis log line (0.6.1) is written this often, seconds (0 = only the start line).</summary>
    public double DiagnosisEveryS { get; init; } = 5;
    public bool StrictValidation { get; init; }
    public EngineOptions Engine { get; init; } = new();
}

/// <summary>
/// Sim, Fenix backend, package version source and physics link for one bridge (real or fake). Without a
/// physics link the in-memory one is used (tests).
/// </summary>
public sealed record BridgeBackends(ISimAdapter Sim, IFenixGraphQl GraphQl, IPackageVersionSource Package, IPhysicsLink? Physics = null)
{
    /// <summary>Everything in memory; the package version is the profile's first build.</summary>
    public static BridgeBackends Fake(Profile profile, FakeSimOptions? options = null, FakePhysicsOptions? physics = null)
    {
        var sim = new FakeSimAdapter(profile, options);
        return new BridgeBackends(sim, new FakeFenixGraphQl(sim), new FixedPackageVersion(profile.Aircraft.Builds.FirstOrDefault()), new FakePhysicsLink(physics));
    }
}

public sealed class ProfileInvalidException(IEnumerable<string> issues)
    : Exception("The profile is not valid:\n" + string.Join("\n", issues));

public sealed class JoinRejectedException(JsonObject body)
    : Exception($"Join refused: {body["message"]}\n" + string.Join("\n",
        (body["diff"] as JsonArray ?? []).Select(d => $"  {d!["severity"]}: {d["field"]}: session {d["reference"]}, you {d["joining"]} – {d["message"]}")))
{
    public JsonObject Body { get; } = body;
}

/// <summary>The result of the last preflight_check (shown to the joiner before joining).</summary>
public sealed record PreflightCheck(string Code, JsonObject Mine, JsonObject Reference, JsonArray Rejects, JsonArray Warnings)
{
    public bool Joinable => Rejects.Count == 0;
}

/// <summary>
/// One bridge: profile, sim, Fenix backend, local API and log run for the whole program; a
/// session (protocol client + sync engine) is created on request and can be left and started
/// again. Headless runs (BridgeOptions.Mode) create or join right at start.
/// </summary>
public sealed class BridgeRuntime : IBridgeControl, IAsyncDisposable
{
    private readonly BridgeOptions _options;
    private readonly Func<Profile, BridgeBackends> _backends;
    private readonly ILoggerFactory _loggers;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private ILogger _logger = NullLogger.Instance;
    private IPackageVersionSource _package = null!;
    private Task<AircraftVersions>? _versions;
    private CancellationTokenSource _life = new();
    private Uri _server = null!;

    public BridgeRuntime(BridgeOptions options, Func<Profile, BridgeBackends> backends, ILoggerFactory? loggers = null)
    {
        _options = options;
        _server = options.Server;
        _backends = backends;
        _loggers = loggers ?? NullLoggerFactory.Instance;
    }

    public static string BridgeVersion =>
        typeof(BridgeRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public Profile Profile { get; private set; } = null!;
    public string ProfileHash { get; private set; } = "";
    public ISimAdapter Sim { get; private set; } = null!;
    public IFenixGraphQl GraphQl { get; private set; } = null!;
    /// <summary>The physics stream: streams while PF, follows (freezes and places) while PM (CRO-13).</summary>
    public PhysicsSync Physics { get; private set; } = null!;
    /// <summary>The current session's protocol client (null when not in a session).</summary>
    public ProtocolClient? Client { get; private set; }
    /// <summary>The current session's sync engine (null when not in a session).</summary>
    public SyncEngine? Engine { get; private set; }
    public EventHub Events { get; } = new();
    public BridgeStatus Status { get; } = new();
    public BridgeLog Log { get; private set; } = null!;
    public LocalApi? Api { get; private set; }
    public AircraftVersions? Versions => _versions is { IsCompletedSuccessfully: true } t ? t.Result : null;
    public string? JoinCode => Client?.JoinCode;
    /// <summary>This side created the current session.</summary>
    public bool Hosting { get; private set; }
    public PreflightCheck? Preflight { get; private set; }
    /// <summary>The last thing that went wrong with a session request, in words (shown on the start screen).</summary>
    public string? LastError { get; private set; }
    /// <summary>True from the moment a create/join/check starts until the session is over. Updates never install then.</summary>
    public bool SessionActive => Preflight is not null || _sessionLock.CurrentCount == 0 || (Engine is not null && Client?.State != ServerState.Ended);
    /// <summary>Self-update (null when not installed with the installer, and in tests).</summary>
    public Updater? Updater { get; set; }
    public Labels Labels { get; private set; } = null!;
    /// <summary>Which vars the pilots see as one control on the panel (CRO-34).</summary>
    public ControlMap Controls { get; private set; } = null!;
    /// <summary>Controls watched read-only because they are not synced yet (unsynced.csv next to the profile).</summary>
    public UnsyncedList Unsynced { get; private set; } = UnsyncedList.Empty;
    public Uri ServerUrl => _server;

    /// <summary>Path of this bridge's own log file (null if logging is off). A pilot can send it after a test.</summary>
    public string? LogPath => Log.Path;

    /// <summary>Loads the profile and starts sim, Fenix backend and local API; in headless mode also creates or joins.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var loaded = ProfileLoader.LoadFile(_options.ProfilePath);
        if (!loaded.IsValid) throw new ProfileInvalidException(loaded.FormatIssues());
        Profile = loaded.Profile!;
        Labels = new Labels(Profile);
        Unsynced = UnsyncedList.LoadNextTo(_options.ProfilePath, Profile);
        Controls = new ControlMap(Profile, Labels, Unsynced.Labels);
        ProfileHash = global::Crosscheck.Bridge.ProfileHash.Of(await File.ReadAllBytesAsync(_options.ProfilePath, ct));

        Log = new BridgeLog(_options.LogDir ?? BridgeLog.DefaultDirectory);
        _logger = _loggers.CreateLogger("Crosscheck.Bridge");
        Log.Event("bridge_start", new JsonObject
        {
            ["version"] = BridgeVersion, ["profile"] = _options.ProfilePath, ["profile_hash"] = ProfileHash, ["server"] = ServerUrl.ToString(),
        });

        var backends = _backends(Profile);
        Sim = backends.Sim;
        GraphQl = backends.GraphQl;
        _package = backends.Package;
        Sim.StateChanged += s => Status.Sim = s == SimState.Connected ? "connected" : "disconnected";
        Sim.WriteRejected += OnWriteRejected;
        Sim.SetupError += OnSimSetupError;
        Sim.SimException += what => Log.Event("sim_exception", new JsonObject { ["what"] = what });
        await Sim.StartAsync(SyncEngine.PlanFor(Profile, Unsynced.Names, DiagnosisLog.ExtraLvars), ct);
        Status.Sim = Sim.State == SimState.Connected ? "connected" : "disconnected";
        StartPhysics(backends.Physics ?? new FakePhysicsLink());
        await GraphQl.StartAsync(SyncEngine.GraphQlPathsFor(Profile), ct);
        StartDiagnosis();
        _versions = ReadVersionsAsync(_life.Token);

        if (_options.ApiPort >= 0)
            Api = await LocalApi.StartAsync(_options.ApiPort, this, Events, _options.UiDir ?? Path.Combine(AppContext.BaseDirectory, "wwwroot"));

        switch (_options.Mode)
        {
            case SessionMode.Create:
            {
                var r = await CreateAsync(ct);
                if (!r.Ok) throw new InvalidOperationException(r.Error);
                break;
            }
            case SessionMode.Join:
            {
                var code = _options.JoinCode ?? throw new ArgumentException("join needs a code");
                var r = await JoinAsync(code, ct);
                if (r.Rejected is { } body) throw new JoinRejectedException(body);
                if (!r.Ok) throw new InvalidOperationException(r.Error);
                break;
            }
        }
    }

    /// <summary>
    /// The read-only diagnosis log (0.6.1): one "diag" line every few seconds in the log file only, never in the panel
    /// or on the wire. Stops with the bridge.
    /// </summary>
    private void StartDiagnosis()
    {
        var sim = Sim;
        var gql = GraphQl;
        var physics = Physics;
        var diag = new DiagnosisLog(
            name => sim.TryGet(name, out var v) ? v : null,
            (path, token) => gql.GetAsync(path, token),
            () => physics.Reading(),
            () => Engine is { } e ? (e.IsPf ? "pf" : "pm") : null,
            () => PhysicsSync.ModeWord(physics.Mode),
            (name, data) => Log.Event(name, data),
            _options.DiagnosisEveryS);
        _ = Task.Run(() => diag.RunAsync(_life.Token));
    }

    /// <summary>
    /// The physics stream (CRO-13). The role comes from the sync engine through Sim.Stream (same call, so a
    /// bridge that becomes PF releases at once); frames from the PF come the same way. Thrust levers (profile
    /// stream vars) are read with the aircraft and written only once the profile gives them a range.
    /// </summary>
    private void StartPhysics(IPhysicsLink link)
    {
        var levers = Profile.Vars.Where(v => v.Class == SyncClass.Stream).ToList();
        Physics = new PhysicsSync(link, Log, Events, _options.Physics,
            levers.ToDictionary(v => v.Name, v => v.Tolerance ?? 0.1, StringComparer.Ordinal),
            name => Sim.Guard.IsWritable(name), (name, value) => Sim.WriteAsync(name, value))
        {
            Rtt = () => Client?.RttMs,
        };
        Sim.Stream.RoleChanged += Physics.SetRole;
        Sim.Stream.FrameReceived += Physics.OnStream;
        Events.Published += e =>
        {
            switch (e.Type)
            {
                case "peer_lost" or "peer_left": Physics.OnPeerState(e.Type[5..]); break;
                case "control_changed": Physics.OnNewPf(); break;
            }
        };
        link.ConnectedChanged += c => Log.Event("physics_link", new JsonObject { ["connected"] = c });
        link.Start(levers.Select(v => v.Name).ToList());
    }

    /// <summary>
    /// SimConnect refused a write of this var (gate test 3 cause A): blocked for as long as the bridge runs, logged,
    /// and shown to both pilots as a red line. Never silent. Adapter thread.
    /// </summary>
    private void OnWriteRejected(string name, string why)
    {
        var first = !Sim.Guard.CannotWrite(name);
        Sim.Guard.BlockCannotWrite(name, why);
        Log.Event("write_rejected", new JsonObject { ["var"] = name, ["why"] = why, ["first"] = first });
        if (!first) return;
        _logger.LogWarning("SimConnect refused writing {Var}: {Why}", name, why);
        if (Engine is { } engine) engine.ReportCannotWrite(name, why);
        else Events.Publish("cannot_write", $"Crosscheck cannot write {Labels.Var(name)}. Set it by hand in both cockpits.", new JsonObject { ["var"] = name });
    }

    /// <summary>The link to the sim is incomplete: logged, shown, and no session can start (loud, never silent). Adapter thread.</summary>
    private void OnSimSetupError(string why)
    {
        Log.Event("sim_setup_error", new JsonObject { ["why"] = why });
        _logger.LogError("SimConnect setup error: {Why}", why);
        Events.Publish("sim_setup_error", $"Crosscheck could not set up its link to the sim: {why}", new JsonObject { ["why"] = why });
    }

    /// <summary>Why no session may start because the link to the sim is incomplete (null if it is complete).</summary>
    public string? SimSetupProblem => Sim?.SetupErrors is { Count: > 0 } errors
        ? $"Crosscheck could not set up its link to the sim ({errors.Count} {(errors.Count == 1 ? "error" : "errors")}: {errors[0]}). Do not fly with Crosscheck – send the log."
        : null;

    private async Task<AircraftVersions> ReadVersionsAsync(CancellationToken ct)
    {
        var v = await AircraftVersionReader.ReadAsync(GraphQl, _package, Profile.Aircraft.Builds, _options.VersionTimeout, TimeSpan.FromSeconds(2), ct);
        Status.PackageVersion = v.PackageVersion;
        Status.AppVersion = v.AppVersion;
        Status.Warnings = v.Warnings;
        foreach (var w in v.Warnings)
        {
            _logger.LogWarning("{Warning}", w);
            Events.Publish("warning", w);
            Log.Event("warning", new JsonObject { ["message"] = w });
        }
        return v;
    }

    private async Task<JsonObject> PreflightAsync(CancellationToken ct)
    {
        var v = await _versions!.WaitAsync(ct);
        return new JsonObject
        {
            ["bridge_version"] = BridgeVersion,
            ["profile_id"] = Profile.Aircraft.Id,
            ["profile_hash"] = ProfileHash,
            ["aircraft"] = new JsonObject { ["package_version"] = v.PackageVersion, ["app_version"] = v.AppVersion },
            ["airac"] = _options.Airac,
        };
    }

    // ------------------------------------------------------------------ sessions (IBridgeControl)

    /// <summary>Changes the session server (and saves it). Only between sessions.</summary>
    public ApiResult SetServer(string? address)
    {
        if (Client is not null || Preflight is not null || _sessionLock.CurrentCount == 0)
            return ApiResult.Fail("Leave the session before changing the server.");
        if (!ServerAddress.TryNormalize(address, out var uri, out var error)) return ApiResult.Fail(error);
        _server = uri;
        LastError = null;
        Log.Event("server_changed", new JsonObject { ["server"] = uri.ToString() });
        if (_options.SettingsPath is { } path)
        {
            try
            {
                new UserSettings(uri.ToString()).Save(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ApiResult.Fail($"Using {uri} for now, but could not save it: {e.Message}");
            }
        }
        return ApiResult.Success;
    }

    public Task<ApiResult> CreateAsync() => CreateAsync(_life.Token);

    public async Task<ApiResult> CreateAsync(CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (Engine is not null) return ApiResult.Fail("You are already in a session.");
            if (SimSetupProblem is { } setup) return Error(setup);
            var preflight = await PreflightAsync(ct);
            var (client, _) = NewSession();
            var answer = await client.CreateAsync(preflight, _options.Name, ct);
            if (answer["type"]!.GetValue<string>() != "created")
            {
                await EndSessionAsync(leave: false);
                return Error(Refusal(answer["body"]));
            }
            Hosting = true;
            Preflight = null;
            LastError = null;
            return ApiResult.Success;
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or TimeoutException or HttpRequestException)
        {
            await EndSessionAsync(leave: false);
            return Error($"Could not reach the server at {ServerUrl}.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    /// <summary>The text for a refused create, check or join. A version refusal is shown as the server wrote it.</summary>
    private string Refusal(JsonNode? body)
    {
        if (body?["code"]?.GetValue<string>() != "unsupported_version") return $"The server refused: {body?["message"]}";
        // Our version is not accepted: look for an update at once (Updater is null in tests and headless runs).
        if (Updater is { } updater) _ = updater.CheckNowAsync("unsupported_version");
        return $"{body["message"]}";
    }

    public async Task<ApiResult> CheckAsync(string code)
    {
        await _sessionLock.WaitAsync(_life.Token);
        try
        {
            if (Engine is not null) return ApiResult.Fail("You are already in a session.");
            if (SimSetupProblem is { } setup) return Error(setup);
            var preflight = await PreflightAsync(_life.Token);
            await using var client = new ProtocolClient(ServerUrl, Log, _logger) { StrictValidation = _options.StrictValidation };
            var answer = await client.CheckAsync(code.Trim().ToUpperInvariant(), preflight, _life.Token);
            var body = answer["body"]!.AsObject();
            switch (answer["type"]!.GetValue<string>())
            {
                case "preflight_result":
                    Preflight = new PreflightCheck(body["join_code"]!.GetValue<string>(), preflight, body["reference"]!.AsObject(),
                        (JsonArray)body["rejects"]!.DeepClone(), (JsonArray)body["warnings"]!.DeepClone());
                    LastError = null;
                    return ApiResult.Success;
                case "join_rejected":
                    return Error(body["reason"]?.GetValue<string>() == "session_full"
                        ? "This session already has two pilots."
                        : $"No session with the code {code.Trim().ToUpperInvariant()}. Check the code with the captain.");
                default:
                    return Error(Refusal(body));
            }
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or TimeoutException or HttpRequestException)
        {
            return Error($"Could not reach the server at {ServerUrl}.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public Task<ApiResult> JoinAsync(string code) => JoinAsync(code, _life.Token);

    public async Task<ApiResult> JoinAsync(string code, CancellationToken ct)
    {
        await _sessionLock.WaitAsync(ct);
        try
        {
            if (Engine is not null) return ApiResult.Fail("You are already in a session.");
            if (SimSetupProblem is { } setup) return Error(setup);
            var preflight = await PreflightAsync(ct);
            var (client, _) = NewSession();
            var answer = await client.JoinAsync(code.Trim().ToUpperInvariant(), preflight, _options.Name, ct);
            var type = answer["type"]!.GetValue<string>();
            var body = answer["body"]!.AsObject();
            if (type != "joined")
            {
                await EndSessionAsync(leave: false);
                var result = Error(type == "join_rejected" ? body["message"]!.GetValue<string>() : Refusal(body));
                return type == "join_rejected" ? result with { Rejected = body } : result;
            }
            foreach (var w in body["warnings"] as JsonArray ?? [])
                Events.Publish("warning", w!["message"]!.GetValue<string>(), w.AsObject());
            Status.JoinCode = code.Trim().ToUpperInvariant(); // the joiner knows the code it typed
            Hosting = false;
            Preflight = null;
            LastError = null;
            return ApiResult.Success;
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or TimeoutException or HttpRequestException)
        {
            await EndSessionAsync(leave: false);
            return Error($"Could not reach the server at {ServerUrl}.");
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public ApiResult CancelPreflight()
    {
        Preflight = null;
        LastError = null;
        return ApiResult.Success;
    }

    public Task<ApiResult> LeaveAsync() => LeaveAsync(releaseInAir: false);

    /// <summary>
    /// Leaves the session. Leaving releases the freezes; while this side holds the aircraft frozen in the air,
    /// the pilot is asked first (owner decision 2026-11-04): the answer comes back with
    /// <c>releaseInAir: true</c>, or the leave is cancelled.
    /// </summary>
    public async Task<ApiResult> LeaveAsync(bool releaseInAir)
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (Engine is null) return ApiResult.Fail("Not in a session.");
            if (Physics.Frozen && Physics.InAir && !releaseInAir)
                return ApiResult.Fail("You are in the air. Leaving releases the aircraft: it will fly on from where it is now.") with { Confirm = "release_in_air" };
            await EndSessionAsync(leave: true);
            return ApiResult.Success;
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    public ApiResult TakeControl(bool confirmPeerLost)
    {
        if (Engine is null) return ApiResult.Fail("Not in a session.");
        // The PF is lost and the server cannot be reached: the pilot's yes releases the aircraft here and now;
        // this pilot flies on unsynced (owner decision 2026-11-04). Asked for control again once reconnected.
        if (confirmPeerLost && Physics.Mode == FollowMode.PfLost && Client?.State != ServerState.Connected)
        {
            Physics.TakeAircraftByPilot();
            Log.Event("took_aircraft_offline", new JsonObject());
            return ApiResult.Success;
        }
        Engine.RequestControl(confirmPeerLost ? ["@peer_lost"] : null);
        return ApiResult.Success;
    }

    /// <summary>"Release aircraft": every freeze off (a crashed bridge, or holding after the PF was lost). Pilot action.</summary>
    public ApiResult ReleaseAircraft()
    {
        var r = Physics.ReleaseAircraft();
        Log.Event("release_aircraft_request", new JsonObject { ["ok"] = r.Ok });
        return r;
    }

    public ApiResult GiveControl()
    {
        if (Engine is null) return ApiResult.Fail("Not in a session.");
        Engine.GiveControl();
        return ApiResult.Success;
    }

    public async Task<ApiResult> PilotSetAsync(string name, double value)
    {
        if (Engine is null) return ApiResult.Fail("Not in a session.");
        try
        {
            await Engine.PilotSetAsync(name, value);
            return ApiResult.Success;
        }
        catch (InvalidOperationException e) // includes SafetyViolationException
        {
            return ApiResult.Fail(e.Message);
        }
    }

    public async Task<ApiResult> PressHereOnlyAsync(string name)
    {
        if (Engine is null) return ApiResult.Fail("Not in a session.");
        try
        {
            await Engine.PressHereOnlyAsync(name);
            return ApiResult.Success;
        }
        catch (InvalidOperationException e) // includes SafetyViolationException
        {
            return ApiResult.Fail(e.Message);
        }
    }

    public async Task<ApiResult> ReleaseItAsync(string name)
    {
        if (Engine is null) return ApiResult.Fail("Not in a session.");
        try
        {
            await Engine.ReleaseItAsync(name);
            return ApiResult.Success;
        }
        catch (InvalidOperationException e) // includes SafetyViolationException
        {
            return ApiResult.Fail(e.Message);
        }
    }

    public ApiResult ApplyUpdate() => Updater?.ApplyAndRestart() ?? ApiResult.Fail("This copy was not installed with the installer.");

    public JsonObject StatusJson() => UiStatus.Build(this);

    public JsonArray Recent() => UiStatus.Recent(Events, Controls);

    private ApiResult Error(string message)
    {
        LastError = message;
        Log.Event("session_request_failed", new JsonObject { ["message"] = message });
        return ApiResult.Fail(message);
    }

    private (ProtocolClient, SyncEngine) NewSession()
    {
        var client = new ProtocolClient(ServerUrl, Log, _logger) { StrictValidation = _options.StrictValidation };
        client.StateChanged += s =>
        {
            Status.Server = s.ToString().ToLowerInvariant();
            if (s is ServerState.Reconnecting or ServerState.Ended)
                Events.Publish("server_" + Status.Server, s == ServerState.Ended ? "Session ended." : "Connection to the server lost; resuming.");
        };
        var engine = new SyncEngine(Profile, Sim, GraphQl, client, Events, Log, Status, _logger, options: _options.Engine, unsynced: Unsynced);
        client.StateChanged += s => Physics.OnServerConnected(s == ServerState.Connected);
        // A pilot who took the aircraft while the server was gone asks for control as soon as it is back.
        client.Resumed += _ =>
        {
            if (Physics.Mode == FollowMode.Released && !engine.IsPf) engine.RequestControl(["@peer_lost"]);
        };
        Physics.StartSession((no, data) => engine.SendStreamFrame(no, data));
        engine.Start();
        Client = client;
        Engine = engine;
        return (client, engine);
    }

    private async Task EndSessionAsync(bool leave)
    {
        // Session end releases every freeze this side holds (owner decision 2026-11-04). A leave in the air
        // was confirmed by the pilot (LeaveAsync); quitting the bridge is the pilot's decision too.
        Physics?.EndSession(leave ? "left" : "session_end");
        var client = Client;
        var engine = Engine;
        Client = null;
        Engine = null;
        if (client is not null && leave)
        {
            try { await client.LeaveAsync(); } catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or ObjectDisposedException) { }
        }
        if (engine is not null) await engine.DisposeAsync();
        if (client is not null) await client.DisposeAsync();
        Status.ResetSession();
        Hosting = false;
    }

    /// <summary>Counters for /status (safety guard, stream writer, engine).</summary>
    internal JsonObject StatusCounters()
    {
        var c = Engine?.Counters() ?? new JsonObject();
        c["safety_refused"] = Sim.Guard.Refused;
        c["stream_applied"] = Sim.Stream.Applied;
        c["stream_refused"] = Sim.Stream.Refused;
        c["stream_sent"] = Physics.FramesSent;
        c["stream_placed"] = Physics.FramesPlaced;
        Status.RttMs = Client?.RttMs;
        return c;
    }

    /// <summary>Leaves the session on purpose (frees the slot) and stops.</summary>
    public async Task StopAsync()
    {
        if (Engine is not null) await EndSessionAsync(leave: true);
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _life.Cancel();
        if (Engine is not null || Client is not null) await EndSessionAsync(leave: false);
        if (Api is not null) await Api.DisposeAsync();
        if (GraphQl is not null) await GraphQl.DisposeAsync();
        if (Physics is not null) await Physics.DisposeAsync();
        if (Sim is not null) await Sim.DisposeAsync();
        Physics = null!;
        Api = null;
        GraphQl = null!;
        Sim = null!;
    }
}
