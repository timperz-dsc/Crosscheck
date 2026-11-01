using System.Reflection;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Fenix;
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
    /// <summary>null = default (%LOCALAPPDATA%\Crosscheck\logs).</summary>
    public string? LogDir { get; init; }
    /// <summary>Where the server address set in the UI is saved. null = not saved (tests).</summary>
    public string? SettingsPath { get; init; }
    public string? Airac { get; init; }
    public TimeSpan VersionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public double StreamHz { get; init; }
    public bool StrictValidation { get; init; }
    public EngineOptions Engine { get; init; } = new();
}

/// <summary>Sim, Fenix backend and package version source for one bridge (real or fake).</summary>
public sealed record BridgeBackends(ISimAdapter Sim, IFenixGraphQl GraphQl, IPackageVersionSource Package)
{
    /// <summary>Everything in memory; the package version is the profile's first build.</summary>
    public static BridgeBackends Fake(Profile profile, FakeSimOptions? options = null)
    {
        var sim = new FakeSimAdapter(profile, options);
        return new BridgeBackends(sim, new FakeFenixGraphQl(sim), new FixedPackageVersion(profile.Aircraft.Builds.FirstOrDefault()));
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
    private Timer? _stream;
    private long _frame;

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
    public Labels Labels { get; private set; } = null!;
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
        await Sim.StartAsync(SyncEngine.PlanFor(Profile, Unsynced.Names), ct);
        Status.Sim = Sim.State == SimState.Connected ? "connected" : "disconnected";
        await GraphQl.StartAsync(SyncEngine.GraphQlPathsFor(Profile), ct);
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
            var preflight = await PreflightAsync(ct);
            var (client, _) = NewSession();
            var answer = await client.CreateAsync(preflight, _options.Name, ct);
            if (answer["type"]!.GetValue<string>() != "created")
            {
                await EndSessionAsync(leave: false);
                return Error($"The server refused: {answer["body"]?["message"]}");
            }
            Hosting = true;
            Preflight = null;
            LastError = null;
            StartStream();
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

    public async Task<ApiResult> CheckAsync(string code)
    {
        await _sessionLock.WaitAsync(_life.Token);
        try
        {
            if (Engine is not null) return ApiResult.Fail("You are already in a session.");
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
                    return Error($"The server refused: {body["message"]}");
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
            var preflight = await PreflightAsync(ct);
            var (client, _) = NewSession();
            var answer = await client.JoinAsync(code.Trim().ToUpperInvariant(), preflight, _options.Name, ct);
            var type = answer["type"]!.GetValue<string>();
            var body = answer["body"]!.AsObject();
            if (type != "joined")
            {
                await EndSessionAsync(leave: false);
                var result = Error(type == "join_rejected" ? body["message"]!.GetValue<string>() : $"The server refused: {body["message"]}");
                return type == "join_rejected" ? result with { Rejected = body } : result;
            }
            foreach (var w in body["warnings"] as JsonArray ?? [])
                Events.Publish("warning", w!["message"]!.GetValue<string>(), w.AsObject());
            Status.JoinCode = code.Trim().ToUpperInvariant(); // the joiner knows the code it typed
            Hosting = false;
            Preflight = null;
            LastError = null;
            StartStream();
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

    public async Task<ApiResult> LeaveAsync()
    {
        await _sessionLock.WaitAsync();
        try
        {
            if (Engine is null) return ApiResult.Fail("Not in a session.");
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
        Engine.RequestControl(confirmPeerLost ? ["@peer_lost"] : null);
        return ApiResult.Success;
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

    public JsonObject StatusJson() => UiStatus.Build(this);

    public JsonArray Recent() => UiStatus.Recent(Events);

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
        engine.Start();
        Client = client;
        Engine = engine;
        return (client, engine);
    }

    private async Task EndSessionAsync(bool leave)
    {
        _stream?.Dispose();
        _stream = null;
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

    private void StartStream()
    {
        if (_options.StreamHz <= 0) return;
        var period = TimeSpan.FromSeconds(1 / Math.Min(30, _options.StreamHz));
        // Placeholder frames until the follower spike (CRO-17) defines the field set; sent only while PF.
        _stream = new Timer(_ => Engine?.SendStreamFrame(Interlocked.Increment(ref _frame), new Dictionary<string, double> { ["placeholder"] = 0 }), null, period, period);
    }

    /// <summary>Counters for /status (safety guard, stream writer, engine).</summary>
    internal JsonObject StatusCounters()
    {
        var c = Engine?.Counters() ?? new JsonObject();
        c["safety_refused"] = Sim.Guard.Refused;
        c["stream_applied"] = Sim.Stream.Applied;
        c["stream_refused"] = Sim.Stream.Refused;
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
        if (Sim is not null) await Sim.DisposeAsync();
        Api = null;
        GraphQl = null!;
        Sim = null!;
    }
}
