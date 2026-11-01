using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Logging;
using Crosscheck.Bridge.Protocol;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crosscheck.Bridge.Tests;

public static class Repo
{
    public static string Root { get; } = Find();
    /// <summary>Test data (Fixtures/profile.yaml), so changes to the real profile never break these tests.</summary>
    public static string FixtureProfile => Path.Combine(Root, "bridge", "Crosscheck.Bridge.Tests", "Fixtures", "profile.yaml");

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
        throw new InvalidOperationException("repo root not found");
    }

    public static Profile LoadFixture()
    {
        var r = ProfileLoader.LoadFile(FixtureProfile);
        Assert.True(r.IsValid, string.Join("\n", r.FormatIssues()));
        return r.Profile!;
    }

    public static string TempDir(string name = "crosscheck-test")
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public static class Wait
{
    public static Task Until(Func<bool> condition, string what, int timeoutMs = 5000) => Until(condition, () => what, timeoutMs);

    public static async Task Until(Func<bool> condition, Func<string> what, int timeoutMs = 5000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"timed out: {what()}");
            await Task.Delay(10);
        }
    }
}

/// <summary>In-memory stand-in for the protocol client: records what the engine sends, injects server messages.</summary>
public sealed class MemoryChannel : IProtocolChannel
{
    private long _seq;
    public string? ClientId { get; set; } = "c_me000001";
    public ServerState State { get; set; } = ServerState.Connected;
    public int SnapshotIntervalMs { get; set; } = 60_000;
    public ConcurrentQueue<(string Type, JsonObject Body)> Sent { get; } = new();

    public event Action<JsonObject>? MessageReceived;
    public event Action<bool>? Resumed;

    /// <summary>Outgoing messages that do not match the protocol schema. Must stay empty.</summary>
    public ConcurrentQueue<string> Invalid { get; } = new();

    public void Send(string type, JsonObject body)
    {
        var envelope = new JsonObject { ["v"] = 1, ["type"] = type, ["session"] = "s_test0001", ["ts"] = 1, ["body"] = body.DeepClone() };
        if (MessageValidator.Shared.Validate(envelope) is { } error) Invalid.Enqueue(error);
        Sent.Enqueue((type, (JsonObject)body.DeepClone()));
    }

    public List<JsonObject> SentOf(string type) => Sent.Where(s => s.Type == type).Select(s => s.Body).ToList();

    public long NextSeq() => Interlocked.Increment(ref _seq);

    public void Inject(string type, JsonObject body, long? seq = null)
    {
        var msg = new JsonObject { ["v"] = 1, ["type"] = type, ["session"] = "s_test0001", ["ts"] = 0, ["body"] = body };
        if (seq is not null) msg["seq"] = seq;
        MessageReceived?.Invoke(msg);
    }

    public void RaiseResumed(bool full) => Resumed?.Invoke(full);
}

/// <summary>A sync engine with the Fenix profile, a fast fake sim, fake GraphQL and an in-memory channel.</summary>
public sealed class EngineHarness : IAsyncDisposable
{
    public const string OtherClient = "c_other0001";
    public Profile Profile { get; } = Repo.LoadFixture();
    public FakeSimAdapter Sim { get; }
    public FakeFenixGraphQl Gql { get; }
    public MemoryChannel Channel { get; } = new();
    public EventHub Hub { get; } = new();
    public BridgeStatus Status { get; } = new();
    public SyncEngine Engine { get; }

    public EngineHarness(EngineOptions? options = null, bool withUnsynced = false)
    {
        Sim = new FakeSimAdapter(Profile, new FakeSimOptions { FrameMs = 5, EffectLagMs = 20, McduLagMs = 30 });
        Gql = new FakeFenixGraphQl(Sim);
        Unsynced = withUnsynced ? UnsyncedList.LoadNextTo(Repo.FixtureProfile, Profile) : UnsyncedList.Empty;
        Engine = new SyncEngine(Profile, Sim, Gql, Channel, Hub, new BridgeLog(null), Status, NullLogger.Instance, options: options, unsynced: Unsynced);
    }

    public UnsyncedList Unsynced { get; }

    /// <summary>Starts sim and engine and opens a session in which this side is PF or PM.</summary>
    public async Task StartAsync(bool pf)
    {
        Engine.Start();
        await Sim.StartAsync(Engine.Plan, CancellationToken.None);
        await Task.Delay(30); // first frame: initial values, no commands
        Channel.Inject(pf ? "created" : "joined", new JsonObject
        {
            ["session_id"] = "s_test0001",
            ["client_id"] = Channel.ClientId,
            ["seat"] = pf ? "host" : "guest",
            ["role"] = pf ? "pf" : "pm",
            ["pf_client_id"] = pf ? Channel.ClientId : OtherClient,
            ["config"] = new JsonObject { ["heartbeat_ms"] = 1000, ["lost_after_ms"] = 5000, ["session_ttl_ms"] = 300000, ["snapshot_interval_ms"] = 60000 },
        });
    }

    public List<JsonObject> Commands => Channel.SentOf("command");

    public JsonObject? AckFor(string id) => Channel.SentOf("ack").LastOrDefault(a => a["id"]!.GetValue<string>() == id);

    /// <summary>A command from the other pilot, as the server relays it.</summary>
    public string Remote(string var, string cls, double? value = null, double? delta = null, int settleMs = 30, string? id = null)
    {
        id ??= $"{OtherClient}-{Guid.NewGuid():N}"[..40];
        var body = new JsonObject { ["id"] = id, ["var"] = var, ["class"] = cls };
        if (delta is not null) body["delta"] = delta;
        else body["value"] = value;
        body["group"] = Profile.Vars.First(v => v.Name == var).Group;
        body["settle_ms"] = settleMs;
        body["origin"] = new JsonObject { ["client_id"] = OtherClient, ["seat"] = "guest", ["role"] = "pm" };
        body["attempt"] = 0;
        Channel.Inject("command", body, Channel.NextSeq());
        return id;
    }

    public string Correction(string var, string cls, double target, string source, string? id = null)
    {
        id ??= $"corr:{Channel.NextSeq()}";
        Channel.Inject("correction", new JsonObject
        {
            ["id"] = id, ["var"] = var, ["class"] = cls, ["target"] = target,
            ["group"] = Profile.Vars.First(v => v.Name == var).Group, ["settle_ms"] = 20, ["source"] = source,
            ["request_id"] = "r1", ["attempt"] = 0,
        }, Channel.NextSeq());
        return id;
    }

    public Task<JsonObject> WaitAck(string id, int timeoutMs = 3000) =>
        Wait.Until(() => AckFor(id) is not null, $"ack for {id}", timeoutMs).ContinueWith(_ => AckFor(id)!);

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Sim.DisposeAsync();
        Assert.Empty(Channel.Invalid); // every message the engine sent matches the protocol schema
    }
}
