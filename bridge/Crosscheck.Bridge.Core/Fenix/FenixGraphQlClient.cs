using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Crosscheck.Bridge.Fenix;

/// <summary>
/// The real Fenix backend client. Reads only. Polls datarefs over HTTP (the subscription does
/// not push most of them, see spike/VarTypes/GRAPHQL.md) and subscribes only to the MCDU
/// screens, which are pushed reliably. Reconnects on its own; if Fenix is not running, values
/// are simply unknown.
/// </summary>
public sealed class FenixGraphQlClient : IFenixGraphQl
{
    private const string GetQuery = "query GetDataRef($name: String!) { dataRef { dataRef(name: $name) { value } } }";
    private const string SubscribeQuery = "subscription DataRefsChange($names: [String!]!) { dataRefs(names: $names) { name value } }";
    private static readonly string[] McduPaths = ["aircraft.mcdu1.display", "aircraft.mcdu2.display"];

    private readonly Uri _http;
    private readonly Uri _ws;
    private readonly double _pollHz;
    private readonly ILogger _log;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ConcurrentDictionary<string, object?> _cache = new();
    private readonly ConcurrentDictionary<int, McduScreen> _screens = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _poll;
    private Task? _subscription;

    public FenixGraphQlClient(double pollHz, ILogger log, string baseUrl = "localhost:8083")
    {
        _pollHz = Math.Clamp(pollHz, 0.1, 50);
        _log = log;
        _http = new Uri($"http://{baseUrl}/graphql");
        _ws = new Uri($"ws://{baseUrl}/graphql");
    }

    public event Action<int>? McduChanged;

    public Task StartAsync(IReadOnlyCollection<string> pollPaths, CancellationToken ct)
    {
        var token = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token).Token;
        var paths = pollPaths.Distinct().ToArray();
        _poll = Task.Run(() => PollLoop(paths, token), token);
        _subscription = Task.Run(() => SubscriptionLoop(token), token);
        return Task.CompletedTask;
    }

    public object? Cached(string path) => _cache.TryGetValue(path, out var v) ? v : null;

    public McduScreen? Mcdu(int cdu) => _screens.TryGetValue(cdu, out var s) ? s : null;

    public async Task<object?> GetAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var doc = await Post(new { query = GetQuery, variables = new { name = path } }, ct);
            if (doc.RootElement.TryGetProperty("errors", out _)) return null;
            var value = GraphQlValue.From(doc.RootElement.GetProperty("data").GetProperty("dataRef").GetProperty("dataRef").GetProperty("value"));
            _cache[path] = value;
            return value;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string?> SoftwareVersionAsync(CancellationToken ct = default)
    {
        try
        {
            using var doc = await Post(new { query = "query GetSoftwareVersion { settings { softwareVersion } }" }, ct);
            var version = doc.RootElement.GetProperty("data").GetProperty("settings").GetProperty("softwareVersion").GetString();
            return string.IsNullOrWhiteSpace(version) ? null : version;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<JsonDocument> Post(object body, CancellationToken ct)
    {
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var resp = await _client.PostAsync(_http, content, ct);
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>One request at a time, the whole list every 1/pollHz seconds (rate measured in CRO-18).</summary>
    private async Task PollLoop(string[] paths, CancellationToken ct)
    {
        var period = TimeSpan.FromSeconds(1 / _pollHz);
        while (!ct.IsCancellationRequested)
        {
            var started = DateTime.UtcNow;
            foreach (var path in paths)
            {
                if (ct.IsCancellationRequested) return;
                await GetAsync(path, ct);
            }
            var wait = period - (DateTime.UtcNow - started);
            if (wait > TimeSpan.Zero)
            {
                try { await Task.Delay(wait, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task SubscriptionLoop(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var (path, cdu) in McduPaths.Select((p, i) => (p, i + 1)))
                    if (await GetAsync(path, ct) is string xml) SetScreen(cdu, xml);
                await Subscribe(ct);
                backoff = TimeSpan.FromSeconds(2);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.LogDebug("MCDU subscription: {Error}", e.Message);
            }
            try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(10, backoff.TotalSeconds * 2));
        }
    }

    private async Task Subscribe(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("graphql-transport-ws");
        await ws.ConnectAsync(_ws, ct);
        await Send(ws, new { type = "connection_init", payload = new { } }, ct);
        while (!ct.IsCancellationRequested)
        {
            using var doc = JsonDocument.Parse(await Receive(ws, ct));
            var msg = doc.RootElement;
            switch (msg.GetProperty("type").GetString())
            {
                case "connection_ack":
                    await Send(ws, new { id = "1", type = "subscribe", payload = new { query = SubscribeQuery, variables = new { names = McduPaths } } }, ct);
                    break;
                case "ping":
                    await Send(ws, new { type = "pong" }, ct);
                    break;
                case "next":
                    var data = msg.GetProperty("payload").GetProperty("data").GetProperty("dataRefs");
                    foreach (var item in data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [data])
                    {
                        var name = item.GetProperty("name").GetString();
                        var cdu = Array.IndexOf(McduPaths, name) + 1;
                        if (cdu > 0 && GraphQlValue.From(item.GetProperty("value")) is string xml) SetScreen(cdu, xml);
                    }
                    break;
                case "error":
                case "complete":
                    throw new InvalidOperationException("subscription ended by Fenix");
            }
        }
    }

    private void SetScreen(int cdu, string xml)
    {
        if (McduScreen.Parse(xml) is not { } screen) return;
        var old = Mcdu(cdu);
        _screens[cdu] = screen;
        if (old?.Text != screen.Text) McduChanged?.Invoke(cdu);
    }

    private static Task Send(ClientWebSocket ws, object msg, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg), WebSocketMessageType.Text, true, ct);

    private static async Task<string> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("closed by Fenix");
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        foreach (var t in new[] { _poll, _subscription })
        {
            if (t is null) continue;
            try { await t; } catch (OperationCanceledException) { }
        }
        _client.Dispose();
    }
}
