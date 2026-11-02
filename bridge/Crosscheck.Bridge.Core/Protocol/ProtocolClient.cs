using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Logging;
using Microsoft.Extensions.Logging;

namespace Crosscheck.Bridge.Protocol;

public enum ServerState
{
    Disconnected,
    Connected,
    Reconnecting,
    /// <summary>Left on purpose, or the session could not be resumed.</summary>
    Ended,
}

/// <summary>
/// WebSocket client for protocol v1 (docs/protocol.md). Validates every message in both
/// directions against protocol/schema, keeps last_seq, sends heartbeats, treats 5 s of
/// silence as lost and resumes with the resume token. Messages are handed to
/// <see cref="MessageReceived"/> one at a time, in arrival order.
/// </summary>
public sealed class ProtocolClient : IProtocolChannel, IAsyncDisposable
{
    /// <summary>Protocol version in every envelope (`v`). The server refuses clients with another version.</summary>
    public const int ProtocolVersion = 4;
    public const string OutdatedMessage = "Your Crosscheck is out of date. Download the latest version.";
    public const string ServerOutdatedMessage = "The server runs an older Crosscheck version. Ask whoever runs the server to update it.";

    private readonly Uri _server;
    private readonly BridgeLog _log;
    private readonly ILogger _logger;
    private readonly IClock _clock;
    private readonly MessageValidator _validator = MessageValidator.Shared;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _life = new();
    private readonly object _gate = new();
    private ClientWebSocket? _ws;
    private Timer? _heartbeat;
    private Task? _reconnect;
    private double _lastRxMs;
    private double? _lostSinceMs;
    private long _nonce;
    private (TaskCompletionSource<JsonObject> Tcs, string[] Types)? _waiting;

    public ProtocolClient(Uri server, BridgeLog log, ILogger logger, IClock? clock = null)
    {
        _server = server;
        _log = log;
        _logger = logger;
        _clock = clock ?? SystemClock.Instance;
    }

    public ServerState State { get; private set; } = ServerState.Disconnected;
    public string? SessionId { get; private set; }
    public string? ClientId { get; private set; }
    public string? JoinCode { get; private set; }
    public string? Seat { get; private set; }
    public long LastSeq { get; private set; }
    public double? RttMs { get; private set; }
    public int HeartbeatMs { get; private set; } = 1000;
    public int LostAfterMs { get; private set; } = 5000;
    public int SessionTtlMs { get; private set; } = 300_000;
    public int SnapshotIntervalMs { get; private set; } = 2000;
    private string? _resumeToken;

    /// <summary>Tests: throw instead of dropping an outgoing message that violates the schema.</summary>
    public bool StrictValidation { get; init; }

    public event Action<JsonObject>? MessageReceived;
    public event Action<ServerState>? StateChanged;
    /// <summary>The session was resumed after a connection loss (replayed messages follow).</summary>
    public event Action<bool>? Resumed;

    // ------------------------------------------------------------ session start

    public Task<JsonObject> CreateAsync(JsonObject preflight, string? name, CancellationToken ct) =>
        Handshake("create", Body(preflight, name), ["created", "error"], ct);

    /// <summary>Returns joined, join_rejected or error.</summary>
    public Task<JsonObject> JoinAsync(string code, JsonObject preflight, string? name, CancellationToken ct)
    {
        var body = Body(preflight, name);
        body["join_code"] = code;
        return Handshake("join", body, ["joined", "join_rejected", "error"], ct);
    }

    /// <summary>Compares the preflight with a session before joining. Returns preflight_result, join_rejected or error.</summary>
    public Task<JsonObject> CheckAsync(string code, JsonObject preflight, CancellationToken ct) =>
        Handshake("preflight_check", new JsonObject { ["join_code"] = code, ["preflight"] = preflight.DeepClone() },
            ["preflight_result", "join_rejected", "error"], ct);

    private static JsonObject Body(JsonObject preflight, string? name)
    {
        var body = new JsonObject { ["preflight"] = preflight.DeepClone() };
        if (!string.IsNullOrWhiteSpace(name)) body["name"] = name;
        return body;
    }

    private async Task<JsonObject> Handshake(string type, JsonObject body, string[] expect, CancellationToken ct)
    {
        await Connect(ct);
        var tcs = Wait(expect);
        await SendAsync(type, body);
        return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }

    private TaskCompletionSource<JsonObject> Wait(string[] types)
    {
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _waiting = (tcs, types);
        return tcs;
    }

    private async Task Connect(CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(_server, ct);
        ClientWebSocket? old;
        lock (_gate)
        {
            old = _ws;
            _ws = ws;
            _lastRxMs = _clock.NowMs;
        }
        try { old?.Abort(); } catch (ObjectDisposedException) { }
        _ = Task.Run(() => ReceiveLoop(ws));
    }

    // ------------------------------------------------------------ sending

    public void Send(string type, JsonObject body) => _ = SendAsync(type, body);

    public async Task SendAsync(string type, JsonObject body)
    {
        var env = new JsonObject
        {
            ["v"] = ProtocolVersion,
            ["type"] = type,
            ["session"] = type is "create" or "join" ? null : SessionId,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["body"] = body,
        };
        if (_validator.Validate(env) is { } error)
        {
            if (StrictValidation) throw new InvalidOperationException($"outgoing message invalid: {error}");
            _log.Event("invalid_outgoing", new JsonObject { ["error"] = error });
            _logger.LogError("Not sent, invalid message: {Error}", error);
            return;
        }
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            if (type is not ("ping" or "pong" or "stream")) _log.Event("send_skipped_offline", new JsonObject { ["type"] = type });
            return;
        }
        _log.Message("out", env);
        await _sendLock.WaitAsync();
        try
        {
            await ws.SendAsync(Encoding.UTF8.GetBytes(env.ToJsonString()), WebSocketMessageType.Text, true, _life.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // The receive loop notices the broken connection and resumes.
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ------------------------------------------------------------ receiving

    private async Task ReceiveLoop(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !_life.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, _life.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                Handle(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (ReferenceEquals(ws, _ws)) ConnectionLost();
        }
    }

    private void Handle(string text)
    {
        _lastRxMs = _clock.NowMs;
        JsonObject msg;
        try
        {
            msg = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("not an object");
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException)
        {
            _log.Event("invalid_incoming", new JsonObject { ["error"] = e.Message });
            return;
        }
        if (msg["v"] is JsonValue version && version.TryGetValue<double>(out var v) && v != ProtocolVersion)
        {
            // Another protocol version (an older or newer server): its messages cannot be read, but the
            // pilot must see why instead of a timeout. A waiting create, join or check gets this error.
            _log.Event("wrong_protocol", new JsonObject { ["server_v"] = v, ["type"] = msg["type"]?.DeepClone() });
            msg = new JsonObject
            {
                ["v"] = ProtocolVersion, ["type"] = "error", ["session"] = null, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["body"] = new JsonObject { ["code"] = "unsupported_version", ["message"] = v < ProtocolVersion ? ServerOutdatedMessage : OutdatedMessage },
            };
        }
        if (_validator.Validate(msg) is { } error)
        {
            _log.Event("invalid_incoming", new JsonObject { ["error"] = error });
            _logger.LogWarning("Ignored invalid message from server: {Error}", error);
            return;
        }
        _log.Message("in", msg);
        var type = msg["type"]!.GetValue<string>();
        var body = msg["body"]!.AsObject();
        if (msg["seq"] is JsonValue seq) LastSeq = Math.Max(LastSeq, seq.GetValue<long>());

        switch (type)
        {
            case "ping":
                Send("pong", new JsonObject { ["nonce"] = body["nonce"]!.DeepClone(), ["t"] = body["t"]!.DeepClone() });
                break;
            case "pong":
                RttMs = Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - body["t"]!.GetValue<double>());
                break;
            case "created":
            case "joined":
            case "resumed":
                OnSession(body);
                break;
        }

        (TaskCompletionSource<JsonObject> Tcs, string[] Types)? waiting;
        lock (_gate) waiting = _waiting;
        if (waiting is { } w && w.Types.Contains(type))
        {
            lock (_gate) _waiting = null;
            w.Tcs.TrySetResult(msg);
        }

        try
        {
            MessageReceived?.Invoke(msg);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Handling {Type} failed", type);
            _log.Event("internal_error", new JsonObject { ["type"] = type, ["error"] = e.ToString() });
        }
    }

    private void OnSession(JsonObject body)
    {
        SessionId = body["session_id"]!.GetValue<string>();
        ClientId = body["client_id"]!.GetValue<string>();
        Seat = body["seat"]!.GetValue<string>();
        if (body["resume_token"] is JsonValue token) _resumeToken = token.GetValue<string>();
        if (body["join_code"] is JsonValue code) JoinCode = code.GetValue<string>();
        var cfg = body["config"]!.AsObject();
        HeartbeatMs = cfg["heartbeat_ms"]!.GetValue<int>();
        LostAfterMs = cfg["lost_after_ms"]!.GetValue<int>();
        SessionTtlMs = cfg["session_ttl_ms"]!.GetValue<int>();
        SnapshotIntervalMs = cfg["snapshot_interval_ms"]!.GetValue<int>();
        SetState(ServerState.Connected);
        _heartbeat?.Dispose();
        _heartbeat = new Timer(_ => Heartbeat(), null, HeartbeatMs, HeartbeatMs);
    }

    // ------------------------------------------------------------ liveness and resume

    private void Heartbeat()
    {
        if (State != ServerState.Connected) return;
        if (_clock.NowMs - _lastRxMs > LostAfterMs)
        {
            _log.Event("server_lost", new JsonObject { ["silent_ms"] = (long)(_clock.NowMs - _lastRxMs) });
            _logger.LogWarning("No message from the server for {Ms} ms: reconnecting", LostAfterMs);
            ForceDrop();
            return;
        }
        Send("ping", new JsonObject
        {
            ["nonce"] = Interlocked.Increment(ref _nonce),
            ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["rtt_ms"] = RttMs,
        });
    }

    /// <summary>Drops the connection (tests, or lost detection). A session is resumed automatically.</summary>
    public void ForceDrop()
    {
        try { _ws?.Abort(); } catch (ObjectDisposedException) { }
    }

    private void ConnectionLost()
    {
        if (State == ServerState.Ended || _life.IsCancellationRequested) return;
        if (SessionId is null)
        {
            SetState(ServerState.Disconnected);
            lock (_gate) _waiting?.Tcs.TrySetException(new IOException("connection to the server closed"));
            return;
        }
        _lostSinceMs ??= _clock.NowMs;
        SetState(ServerState.Reconnecting);
        lock (_gate)
        {
            if (_reconnect is { IsCompleted: false }) return;
            _reconnect = Task.Run(ReconnectLoop);
        }
    }

    private async Task ReconnectLoop()
    {
        var delay = TimeSpan.FromMilliseconds(250);
        while (!_life.IsCancellationRequested && State == ServerState.Reconnecting)
        {
            if (_clock.NowMs - _lostSinceMs > SessionTtlMs)
            {
                End("session expired while disconnected");
                return;
            }
            try
            {
                await Connect(_life.Token);
                var tcs = Wait(["resumed", "error"]);
                await SendAsync("resume", new JsonObject { ["client_id"] = ClientId, ["resume_token"] = _resumeToken, ["last_seq"] = LastSeq });
                var answer = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), _life.Token);
                if (answer["type"]!.GetValue<string>() == "resumed")
                {
                    _lostSinceMs = null;
                    var full = answer["body"]!["full_resync"]!.GetValue<bool>();
                    _log.Event("resumed", new JsonObject { ["full_resync"] = full });
                    Resumed?.Invoke(full);
                    return;
                }
                End($"resume refused: {answer["body"]?["message"]}");
                return;
            }
            catch (Exception e) when (e is WebSocketException or TimeoutException or IOException or OperationCanceledException or HttpRequestException)
            {
                _logger.LogDebug("Resume attempt failed: {Error}", e.Message);
            }
            try { await Task.Delay(delay, _life.Token); } catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromMilliseconds(Math.Min(5000, delay.TotalMilliseconds * 2));
        }
    }

    private void End(string why)
    {
        _log.Event("session_ended", new JsonObject { ["why"] = why });
        _logger.LogWarning("Session ended: {Why}", why);
        SetState(ServerState.Ended);
        ForceDrop();
    }

    private void SetState(ServerState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public async Task LeaveAsync()
    {
        if (State == ServerState.Connected) await SendAsync("leave", new JsonObject());
        SetState(ServerState.Ended);
        ForceDrop();
    }

    public async ValueTask DisposeAsync()
    {
        SetState(ServerState.Ended);
        _heartbeat?.Dispose();
        _life.Cancel();
        ForceDrop();
        if (_reconnect is not null)
        {
            try { await _reconnect; } catch (OperationCanceledException) { }
        }
        _ws?.Dispose();
    }
}
