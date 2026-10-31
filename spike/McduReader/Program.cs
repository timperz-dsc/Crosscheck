// Spike: read both Fenix A320 MCDU screens as text from the Fenix web backend (GraphQL over
// WebSocket, see PROTOCOL.md) and measure key press (L: var write) -> screen change latency.
//
//   McduReader watch     print both screens side by side whenever one changes (read-only)
//   McduReader probe     press CDU1 DATA, then MENU, and log what the key L: var and screen do
//   McduReader latency   10 rounds of MENU/DATA/FPLN/INIT/PROG on CDU1, then CDU2

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.FlightSimulator.SimConnect;

string[] pageKeys = { "MENU", "DATA", "FPLN", "INIT", "PROG" };
const int Rounds = 10;
const double TimeoutMs = 2000;
const double SettleMs = 1000;

var mode = args.FirstOrDefault() ?? "watch";
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var feed = new McduFeed();
await feed.SeedAsync();
var feedTask = feed.RunAsync(cts.Token);
PrintScreens();

switch (mode)
{
    case "watch":
        feed.Changed += _ => PrintScreens();
        try { await feedTask; } catch (OperationCanceledException) { }
        return 0;

    case "probe":
        using (var sim = new Sim(KeyVars(1, "DATA", "MENU")))
        {
            sim.ValueChanged += (i, v, t) => Console.WriteLine($"  {t,10:F1}  {sim.Names[i]} = {v}");
            feed.Changed += cdu => Console.WriteLine($"  {feed.Screens[cdu]!.At,10:F1}  CDU{cdu} screen -> {feed.Screens[cdu]!.Hash}  \"{feed.Screens[cdu]!.Rows[0].Trim()}\"");
            await Task.Delay(500);
            for (int i = 0; i < sim.Names.Length; i++)
            {
                Console.WriteLine($"\n{sim.Names[i]} currently {sim.Values[i]}; writing 1 at {Clock.Now:F1}");
                await sim.Write(i, 1);
                await Task.Delay(1500);
                if (sim.Values[i] != 0)
                {
                    Console.WriteLine($"Still {sim.Values[i]} after 1.5 s; writing 0 at {Clock.Now:F1}");
                    await sim.Write(i, 0);
                    await Task.Delay(1500);
                }
            }
        }
        PrintScreens();
        return 0;

    case "latency":
        using (var sim = new Sim(KeyVars(1, pageKeys).Concat(KeyVars(2, pageKeys)).ToArray()))
        {
            await Task.Delay(500);
            var results = new Dictionary<int, List<double?>>();
            var crosstalk = new Dictionary<int, int>();
            foreach (var cdu in new[] { 1, 2 })
            {
                (results[cdu], crosstalk[cdu]) = await MeasureCdu(sim, cdu);
                if (cts.IsCancellationRequested) break;
            }
            PrintScreens();
            Console.WriteLine($"\n{"CDU",-5} {"n",3} {"min",7} {"median",7} {"p95",7} {"max",7} {"fail",5} {"other CDU changed",18}");
            foreach (var (cdu, list) in results)
            {
                var ms = list.Where(x => x.HasValue).Select(x => x!.Value).Order().ToList();
                Console.WriteLine($"CDU{cdu,-2} {list.Count,3} {F(Min(ms)),7} {F(Median(ms)),7} {F(P95(ms)),7} {F(Max(ms)),7} {list.Count(x => !x.HasValue),5} {crosstalk[cdu],18}");
            }
        }
        return 0;

    default:
        Console.Error.WriteLine("Usage: McduReader [watch|probe|latency]");
        return 1;
}

async Task<(List<double?>, int)> MeasureCdu(Sim sim, int cdu)
{
    int other = 3 - cdu;
    var latencies = new List<double?>();
    int crosstalk = 0;
    int VarIndex(string key) => Array.IndexOf(sim.Names, $"L:S_CDU{cdu}_KEY_{key}");

    // Warm-up press so the first measured MENU press leaves a different page.
    Console.WriteLine($"\nCDU{cdu}: warm-up PROG");
    await PressAndWait(sim, VarIndex("PROG"), cdu);
    await Task.Delay((int)SettleMs);

    for (int round = 1; round <= Rounds && !cts.IsCancellationRequested; round++)
    {
        foreach (var key in pageKeys)
        {
            var otherBefore = feed.Screens[other]!.Hash;
            var latency = await PressAndWait(sim, VarIndex(key), cdu);
            await Task.Delay((int)SettleMs);
            bool otherChanged = feed.Screens[other]!.Hash != otherBefore;
            if (otherChanged) crosstalk++;
            latencies.Add(latency);
            var s = feed.Screens[cdu]!;
            Console.WriteLine($"CDU{cdu} round {round,2} {key,-5} {(latency is double l ? $"{l,7:F1} ms" : "   FAIL   ")}  {s.Hash}  \"{s.Rows[0].Trim()}\"{(otherChanged ? $"  (CDU{other} changed!)" : "")}");
        }
    }
    return (latencies, crosstalk);
}

// Presses a key and returns ms from the L: write until the screen text hash changes, or null on timeout.
async Task<double?> PressAndWait(Sim sim, int varIdx, int cdu)
{
    var before = feed.Screens[cdu]!;
    double t0 = await sim.Press(varIdx);
    while (Clock.Now - t0 < TimeoutMs)
    {
        var now = feed.Screens[cdu]!;
        if (now.Hash != before.Hash) return now.At - t0;
        await Task.Delay(1);
    }
    return null;
}

void PrintScreens()
{
    var a = feed.Screens[1];
    var b = feed.Screens[2];
    var sb = new StringBuilder();
    sb.AppendLine();
    sb.AppendLine($"CDU1 {a?.Hash,-19}  |  CDU2 {b?.Hash}");
    for (int r = 0; r < Screen.RowCount; r++)
        sb.AppendLine($"{a?.Rows[r] ?? "",-Screen.Cols}  |  {b?.Rows[r]}");
    Console.Write(sb.ToString());
}

static string[] KeyVars(int cdu, params string[] keys) => keys.Select(k => $"L:S_CDU{cdu}_KEY_{k}").ToArray();
static string F(double v) => double.IsNaN(v) ? "-" : v.ToString("F1", CultureInfo.InvariantCulture);
static double Min(List<double> s) => s.Count == 0 ? double.NaN : s[0];
static double Max(List<double> s) => s.Count == 0 ? double.NaN : s[^1];
static double Median(List<double> s) =>
    s.Count == 0 ? double.NaN : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
static double P95(List<double> s) =>
    s.Count == 0 ? double.NaN : s[Math.Clamp((int)Math.Ceiling(0.95 * s.Count) - 1, 0, s.Count - 1)];

static class Clock
{
    static readonly Stopwatch Watch = Stopwatch.StartNew();
    public static double Now => Watch.Elapsed.TotalMilliseconds;
}

/// <summary>One MCDU screen as text: title, 12 lines, scratchpad; 24 columns, colour/size modifiers stripped.</summary>
sealed record Screen(string[] Rows, string Hash, double At)
{
    public const int Cols = 24;
    public const int RowCount = 14;

    public static Screen Parse(string xml, double at)
    {
        var root = XDocument.Parse(xml, LoadOptions.PreserveWhitespace).Root!;
        var rows = root.Elements("title").Concat(root.Elements("line")).Concat(root.Elements("scratchpad"))
            .Select(e => StripModifiers(e.Value).PadRight(Cols))
            .ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', rows))))[..16];
        return new Screen(rows, hash, at);
    }

    // Lowercase letters switch colour (a w c g m r y) or font size (s l); the text itself is uppercase.
    static string StripModifiers(string s) => new(s.Where(c => !char.IsLower(c)).ToArray());
}

/// <summary>Live MCDU screens from the Fenix GraphQL backend (graphql-transport-ws).</summary>
sealed class McduFeed
{
    const string HttpUrl = "http://localhost:8083/graphql";
    const string WsUrl = "ws://localhost:8083/graphql";
    static readonly string[] Names = { "aircraft.mcdu1.display", "aircraft.mcdu2.display" };

    public readonly Screen?[] Screens = new Screen?[3]; // indexed by CDU number
    public event Action<int>? Changed;

    public async Task SeedAsync()
    {
        using var http = new HttpClient();
        foreach (var name in Names)
        {
            var body = JsonSerializer.Serialize(new
            {
                query = "query GetDataRef($name: String!) { dataRef { dataRef(name: $name) { value } } }",
                variables = new { name },
            });
            var resp = await http.PostAsync(HttpUrl, new StringContent(body, Encoding.UTF8, "application/json"));
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            Apply(name, doc.RootElement.GetProperty("data").GetProperty("dataRef").GetProperty("dataRef").GetProperty("value").GetString()!);
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        ws.Options.AddSubProtocol("graphql-transport-ws");
        await ws.ConnectAsync(new Uri(WsUrl), ct);
        await Send(ws, new { type = "connection_init", payload = new { } }, ct);

        while (!ct.IsCancellationRequested)
        {
            using var doc = JsonDocument.Parse(await Receive(ws, ct));
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
                            variables = new { names = Names },
                        },
                    }, ct);
                    break;
                case "ping":
                    await Send(ws, new { type = "pong" }, ct);
                    break;
                case "next":
                    var data = msg.GetProperty("payload").GetProperty("data").GetProperty("dataRefs");
                    foreach (var item in data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : new[] { data })
                        Apply(item.GetProperty("name").GetString()!, item.GetProperty("value").GetString()!);
                    break;
                case "error":
                case "complete":
                    throw new InvalidOperationException($"Subscription ended: {msg}");
            }
        }
    }

    void Apply(string name, string xml)
    {
        int cdu = Array.IndexOf(Names, name) + 1;
        if (cdu == 0) return;
        var screen = Screen.Parse(xml, Clock.Now);
        if (Screens[cdu]?.Hash == screen.Hash) return; // colour-only or identical update
        Screens[cdu] = screen;
        Changed?.Invoke(cdu);
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

/// <summary>SimConnect on its own pump thread: watches and writes a set of L: vars.</summary>
sealed class Sim : IDisposable
{
    // How a key press is sent through the L: var. Determined with the probe mode, see PROTOCOL.md.
    const int ReleaseAfterMs = 100; // Fenix leaves the key var at 1, so release it; the press registers on 0 -> 1

    public readonly string[] Names;
    public readonly double[] Values;
    public event Action<int, double, double>? ValueChanged; // index, value, timestamp

    readonly SimConnect sc;
    readonly AutoResetEvent wake = new(false);
    readonly ConcurrentQueue<(int Index, double Value, TaskCompletionSource<double> Sent)> writes = new();
    readonly Thread pump;
    volatile bool stop;

    public Sim(string[] names)
    {
        Names = names;
        Values = new double[names.Length];
        sc = new SimConnect("Crosscheck McduReader", IntPtr.Zero, 0, wake, 0);
        sc.OnRecvException += (_, d) => Console.Error.WriteLine($"SimConnect exception {(SIMCONNECT_EXCEPTION)d.dwException} (sendID {d.dwSendID})");
        sc.OnRecvSimobjectData += (_, d) =>
        {
            int i = (int)d.dwRequestID;
            Values[i] = ((OneVar)d.dwData[0]).Value;
            ValueChanged?.Invoke(i, Values[i], Clock.Now);
        };
        for (int i = 0; i < names.Length; i++)
        {
            sc.AddToDataDefinition((Def)i, names[i], "number", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<OneVar>((Def)i);
            sc.RequestDataOnSimObject((Req)i, (Def)i, SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }
        pump = new Thread(Pump) { IsBackground = true, Name = "SimConnect" };
        pump.Start();
    }

    /// <summary>Writes a value; completes with the timestamp taken just before the write was sent.</summary>
    public Task<double> Write(int index, double value)
    {
        var tcs = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        writes.Enqueue((index, value, tcs));
        wake.Set();
        return tcs.Task;
    }

    /// <summary>Presses a key; returns the timestamp of the first write.</summary>
    public async Task<double> Press(int index)
    {
        double t0 = await Write(index, 1);
        if (ReleaseAfterMs >= 0)
        {
            await Task.Delay(ReleaseAfterMs);
            await Write(index, 0);
        }
        return t0;
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
