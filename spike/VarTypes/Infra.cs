// Shared plumbing for the VarTypes spike: SimConnect L: var access and the Fenix GraphQL backend.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.FlightSimulator.SimConnect;

static class Clock
{
    static readonly Stopwatch Watch = Stopwatch.StartNew();
    public static double Now => Watch.Elapsed.TotalMilliseconds;
}

static class Stats
{
    public static string F(double v) => double.IsNaN(v) ? "-" : v.ToString("F1", CultureInfo.InvariantCulture);
    public static double Min(List<double> s) => s.Count == 0 ? double.NaN : s[0];
    public static double Max(List<double> s) => s.Count == 0 ? double.NaN : s[^1];
    public static double Median(List<double> s) =>
        s.Count == 0 ? double.NaN : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    public static double P95(List<double> s) =>
        s.Count == 0 ? double.NaN : s[Math.Clamp((int)Math.Ceiling(0.95 * s.Count) - 1, 0, s.Count - 1)];
}

static class Profiles
{
    public const string V11 = @"C:\Users\t1mbe\Downloads\Yourcontrols-FenixSim A320 v1.1_8ckST\FenixSim A320 v1.1.yaml";
    public const string Big = @"C:\Users\t1mbe\Documents\YourControls\definitions\aircraft\Fenix Simulations - Airbus A320ceo.yaml";

    /// <summary>All L: var names in a profile, including commented-out entries if asked.</summary>
    public static IEnumerable<string> LVars(string path, bool includeComments) =>
        File.ReadLines(path)
            .Where(l => includeComments || !l.TrimStart().StartsWith('#'))
            .SelectMany(l => Regex.Matches(l, @"L:[A-Za-z][A-Za-z0-9_]*").Select(m => m.Value))
            .Distinct();
}

/// <summary>SimConnect on its own pump thread: watches and writes a fixed set of L: vars (and A: simvars).</summary>
sealed class Sim : IDisposable
{
    public readonly string[] Names;
    public readonly double[] Values;
    public readonly bool[] Received;
    public readonly double[] ChangedAt;
    public event Action<int, double, double>? ValueChanged; // index, value, timestamp

    readonly SimConnect sc;
    readonly AutoResetEvent wake = new(false);
    readonly ConcurrentQueue<(int Index, double Value, TaskCompletionSource<double> Sent)> writes = new();
    readonly Thread pump;
    volatile bool stop;

    /// <param name="vars">Variable names, optionally "NAME|units" (default units "number").</param>
    public Sim(IEnumerable<string> vars)
    {
        var parsed = vars.Select(v => v.Split('|')).ToArray();
        Names = parsed.Select(p => p[0]).ToArray();
        Values = new double[Names.Length];
        Received = new bool[Names.Length];
        ChangedAt = new double[Names.Length];
        sc = new SimConnect("Crosscheck VarTypes", IntPtr.Zero, 0, wake, 0);
        sc.OnRecvException += (_, d) => Console.Error.WriteLine($"SimConnect exception {(SIMCONNECT_EXCEPTION)d.dwException} (sendID {d.dwSendID}, index {d.dwIndex})");
        sc.OnRecvSimobjectData += (_, d) =>
        {
            int i = (int)d.dwRequestID;
            double t = Clock.Now;
            Values[i] = ((OneVar)d.dwData[0]).Value;
            ChangedAt[i] = t;
            Received[i] = true;
            ValueChanged?.Invoke(i, Values[i], t);
        };
        for (int i = 0; i < Names.Length; i++)
        {
            var units = parsed[i].Length > 1 ? parsed[i][1] : "number";
            sc.AddToDataDefinition((Def)i, Names[i], units, SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<OneVar>((Def)i);
            sc.RequestDataOnSimObject((Req)i, (Def)i, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }
        pump = new Thread(Pump) { IsBackground = true, Name = "SimConnect" };
        pump.Start();
    }

    public int IndexOf(string name) => Array.FindIndex(Names, n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Waits until every variable has delivered its first value (or the timeout passes).</summary>
    public async Task WaitForValues(int timeoutMs = 3000)
    {
        var until = Clock.Now + timeoutMs;
        while (Received.Any(r => !r) && Clock.Now < until) await Task.Delay(20);
    }

    /// <summary>Writes a value; completes with the timestamp taken just before the write was sent.</summary>
    public Task<double> Write(int index, double value)
    {
        var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        writes.Enqueue((index, value, tcs));
        wake.Set();
        return tcs.Task;
    }

    void Pump()
    {
        while (!stop)
        {
            wake.WaitOne(5);
            while (writes.TryDequeue(out var w))
            {
                double t = Clock.Now;
                sc.SetDataOnSimObject((Def)w.Index, SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new OneVar { Value = w.Value });
                w.Sent.SetResult(t);
            }
            try { sc.ReceiveMessage(); }
            catch (COMException) { stop = true; }
        }
    }

    public void Dispose()
    {
        stop = true;
        wake.Set();
        pump.Join();
        sc.Dispose();
    }

    enum Def { }
    enum Req { }

    [StructLayout(LayoutKind.Sequential)]
    struct OneVar
    {
        public double Value;
    }
}

/// <summary>Fenix EFB GraphQL backend: one-off reads over HTTP, dataref subscriptions over graphql-transport-ws.</summary>
sealed class Gql
{
    const string HttpUrl = "http://localhost:8083/graphql";
    const string WsUrl = "ws://localhost:8083/graphql";
    static readonly HttpClient Http = new();

    public readonly ConcurrentDictionary<string, string> Values = new();
    public readonly ConcurrentDictionary<string, double> ChangedAt = new();
    public event Action<string, string, double>? ValueChanged; // name, raw JSON value, timestamp

    /// <summary>Raw JSON value of a dataref; "" for unknown names (the server does not distinguish).</summary>
    public static async Task<string> Get(string name)
    {
        var body = JsonSerializer.Serialize(new
        {
            query = "query GetDataRef($name: String!) { dataRef { dataRef(name: $name) { value } } }",
            variables = new { name },
        });
        var resp = await Http.PostAsync(HttpUrl, new StringContent(body, Encoding.UTF8, "application/json"));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (doc.RootElement.TryGetProperty("errors", out var errors)) return "ERROR: " + errors[0].GetProperty("message").GetString();
        return doc.RootElement.GetProperty("data").GetProperty("dataRef").GetProperty("dataRef").GetProperty("value").GetRawText();
    }

    public static double? AsNumber(string? raw) => raw switch
    {
        null => null,
        "true" => 1,
        "false" => 0,
        _ => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
    };

    /// <summary>Subscribes to the given datarefs in one subscription and keeps Values up to date.</summary>
    public async Task Subscribe(IReadOnlyList<string> names, CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("graphql-transport-ws");
        await ws.ConnectAsync(new Uri(WsUrl), ct);
        await Send(ws, new { type = "connection_init", payload = new { } }, ct);
        while (!ct.IsCancellationRequested)
        {
            string text;
            try { text = await Receive(ws, ct); }
            catch (OperationCanceledException) { return; }
            using var doc = JsonDocument.Parse(text);
            var msg = doc.RootElement;
            switch (msg.GetProperty("type").GetString())
            {
                case "connection_ack":
                    await Send(ws, new
                    {
                        id = "1",
                        type = "subscribe",
                        payload = new
                        {
                            query = "subscription DataRefsChange($names: [String!]!) { dataRefs(names: $names) { name value } }",
                            variables = new { names },
                        },
                    }, ct);
                    break;
                case "ping":
                    await Send(ws, new { type = "pong" }, ct);
                    break;
                case "next":
                    double t = Clock.Now;
                    var data = msg.GetProperty("payload").GetProperty("data").GetProperty("dataRefs");
                    foreach (var item in data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : new[] { data })
                    {
                        var name = item.GetProperty("name").GetString()!;
                        var value = item.GetProperty("value").GetRawText();
                        if (Values.GetValueOrDefault(name) != value) ChangedAt[name] = t;
                        Values[name] = value;
                        ValueChanged?.Invoke(name, value, t);
                    }
                    break;
                case "error":
                case "complete":
                    throw new InvalidOperationException($"Subscription ended: {msg}");
            }
        }
    }

    static Task Send(ClientWebSocket ws, object msg, CancellationToken ct) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(msg), WebSocketMessageType.Text, true, ct);

    static async Task<string> Receive(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buffer, ct);
            if (r.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("WebSocket closed by server.");
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>Polls a changing set of datarefs over HTTP as fast as the server answers (one at a time).</summary>
sealed class HttpPoller
{
    public readonly ConcurrentDictionary<string, string> Values = new();
    public readonly ConcurrentDictionary<string, double> ChangedAt = new();
    volatile string[] names = Array.Empty<string>();

    public void Watch(params string[] watch) => names = watch;

    public async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var current = names;
            if (current.Length == 0) { await Task.Delay(10, CancellationToken.None); continue; }
            foreach (var n in current)
            {
                string v;
                try { v = await Gql.Get(n); } catch (HttpRequestException) { continue; }
                double t = Clock.Now;
                if (Values.GetValueOrDefault(n) != v) ChangedAt[n] = t;
                Values[n] = v;
            }
        }
    }
}
