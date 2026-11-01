using System.Reflection;
using System.Security.Cryptography;
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
    public SessionMode Mode { get; init; } = SessionMode.Create;
    public string? JoinCode { get; init; }
    public string? Name { get; init; }
    /// <summary>Local API port; 0 = any free port, -1 = off.</summary>
    public int ApiPort { get; init; } = 8790;
    /// <summary>null = default (%LOCALAPPDATA%\Crosscheck\logs).</summary>
    public string? LogDir { get; init; }
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

/// <summary>One bridge: profile, sim, Fenix backend, protocol client, sync engine, local API, log.</summary>
public sealed class BridgeRuntime : IAsyncDisposable
{
    private readonly BridgeOptions _options;
    private readonly Func<Profile, BridgeBackends> _backends;
    private readonly ILoggerFactory _loggers;
    private Timer? _stream;
    private long _frame;

    public BridgeRuntime(BridgeOptions options, Func<Profile, BridgeBackends> backends, ILoggerFactory? loggers = null)
    {
        _options = options;
        _backends = backends;
        _loggers = loggers ?? NullLoggerFactory.Instance;
    }

    public static string BridgeVersion =>
        typeof(BridgeRuntime).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public Profile Profile { get; private set; } = null!;
    public string ProfileHash { get; private set; } = "";
    public ISimAdapter Sim { get; private set; } = null!;
    public IFenixGraphQl GraphQl { get; private set; } = null!;
    public ProtocolClient Client { get; private set; } = null!;
    public SyncEngine Engine { get; private set; } = null!;
    public EventHub Events { get; } = new();
    public BridgeStatus Status { get; } = new();
    public BridgeLog Log { get; private set; } = null!;
    public LocalApi? Api { get; private set; }
    public AircraftVersions Versions { get; private set; } = null!;
    public string? JoinCode => Client?.JoinCode;

    /// <summary>Loads the profile, starts everything and creates or joins the session.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var loaded = ProfileLoader.LoadFile(_options.ProfilePath);
        if (!loaded.IsValid) throw new ProfileInvalidException(loaded.FormatIssues());
        Profile = loaded.Profile!;
        ProfileHash = "sha256:" + Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(_options.ProfilePath, ct))).ToLowerInvariant();

        Log = new BridgeLog(_options.LogDir ?? BridgeLog.DefaultDirectory);
        var logger = _loggers.CreateLogger("Crosscheck.Bridge");
        Log.Event("bridge_start", new JsonObject
        {
            ["version"] = BridgeVersion, ["profile"] = _options.ProfilePath, ["profile_hash"] = ProfileHash, ["server"] = _options.Server.ToString(),
        });

        var backends = _backends(Profile);
        Sim = backends.Sim;
        GraphQl = backends.GraphQl;
        Client = new ProtocolClient(_options.Server, Log, logger) { StrictValidation = _options.StrictValidation };
        Client.StateChanged += s =>
        {
            Status.Server = s.ToString().ToLowerInvariant();
            if (s is ServerState.Reconnecting or ServerState.Ended)
                Events.Publish("server_" + Status.Server, s == ServerState.Ended ? "Session ended." : "Connection to the server lost; resuming.");
        };
        Engine = new SyncEngine(Profile, Sim, GraphQl, Client, Events, Log, Status, logger, options: _options.Engine);
        Engine.Start();

        await Sim.StartAsync(Engine.Plan, ct);
        await GraphQl.StartAsync(Engine.GraphQlPaths, ct);

        Versions = await AircraftVersionReader.ReadAsync(GraphQl, backends.Package, Profile.Aircraft.Builds, _options.VersionTimeout, TimeSpan.FromSeconds(2), ct);
        Status.PackageVersion = Versions.PackageVersion;
        Status.AppVersion = Versions.AppVersion;
        Status.Warnings = Versions.Warnings;
        foreach (var w in Versions.Warnings)
        {
            logger.LogWarning("{Warning}", w);
            Events.Publish("warning", w);
            Log.Event("warning", new JsonObject { ["message"] = w });
        }

        if (_options.ApiPort >= 0)
            Api = await LocalApi.StartAsync(_options.ApiPort, () => Status.ToJson(StatusCounters).ToJsonString(), Events);

        var preflight = new JsonObject
        {
            ["bridge_version"] = BridgeVersion,
            ["profile_id"] = Profile.Aircraft.Id,
            ["profile_hash"] = ProfileHash,
            ["aircraft"] = new JsonObject { ["package_version"] = Versions.PackageVersion, ["app_version"] = Versions.AppVersion },
            ["airac"] = _options.Airac,
        };
        var answer = _options.Mode == SessionMode.Create
            ? await Client.CreateAsync(preflight, _options.Name, ct)
            : await Client.JoinAsync(_options.JoinCode ?? throw new ArgumentException("join needs a code"), preflight, _options.Name, ct);
        var type = answer["type"]!.GetValue<string>();
        if (type == "join_rejected") throw new JoinRejectedException(answer["body"]!.AsObject());
        if (type == "error") throw new InvalidOperationException($"Server refused: {answer["body"]!["message"]}");
        if (answer["body"]!["warnings"] is JsonArray { Count: > 0 } warnings)
            foreach (var w in warnings) Events.Publish("warning", w!["message"]!.GetValue<string>(), w.AsObject());

        if (_options.StreamHz > 0)
        {
            var period = TimeSpan.FromSeconds(1 / Math.Min(30, _options.StreamHz));
            // Placeholder frames until the follower spike (CRO-17) defines the field set; sent only while PF.
            _stream = new Timer(_ => Engine.SendStreamFrame(Interlocked.Increment(ref _frame), new Dictionary<string, double> { ["placeholder"] = 0 }), null, period, period);
        }
    }

    private JsonObject StatusCounters()
    {
        var c = Engine.Counters();
        c["safety_refused"] = Sim.Guard.Refused;
        c["stream_applied"] = Sim.Stream.Applied;
        c["stream_refused"] = Sim.Stream.Refused;
        Status.RttMs = Client.RttMs;
        return c;
    }

    /// <summary>Leaves the session on purpose (frees the slot) and stops.</summary>
    public async Task StopAsync()
    {
        if (Client is not null) await Client.LeaveAsync();
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        if (Engine is not null) await Engine.DisposeAsync();
        if (Client is not null) await Client.DisposeAsync();
        if (Api is not null) await Api.DisposeAsync();
        if (GraphQl is not null) await GraphQl.DisposeAsync();
        if (Sim is not null) await Sim.DisposeAsync();
    }
}
