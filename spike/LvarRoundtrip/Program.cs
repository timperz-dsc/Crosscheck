// Spike: measure write -> read-back latency of Fenix A320 L: variables via SimConnect (MSFS 2024).
// L: vars are used directly in data definitions, as documented in the MSFS 2024 SDK
// (SimConnect_AddToDataDefinition: "It is possible to get and/or set an RPN "L" variable").

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.FlightSimulator.SimConnect;

const int Iterations = 30;
const double TimeoutMs = 2000;
const double GapMs = 1000;
const double Eps = 0.01;
const double GateMs = 150;

string[] testVars =
{
    "L:S_OH_EXT_LT_NAV_LOGO",
    "L:S_OH_SIGNS_SMOKING",
    "L:S_OH_INT_LT_DOME",
    "L:S_OH_ELEC_APU_MASTER",
    "L:S_OH_ELEC_BAT1",
};
const string CautionVar = "L:I_MIP_MASTER_CAUTION_CAPT";

if (args.Contains("--check"))
{
    // Loads the managed SimConnect assembly without connecting to the sim.
    var asm = typeof(SimConnect).Assembly;
    Console.WriteLine($"Managed SimConnect: {asm.FullName}");
    Console.WriteLine($"Native SimConnect.dll in output: {File.Exists(Path.Combine(AppContext.BaseDirectory, "SimConnect.dll"))}");
    Console.WriteLine($"Process is 64-bit: {Environment.Is64BitProcess}");
    return 0;
}

var clock = Stopwatch.StartNew();
double Now() => clock.Elapsed.TotalMilliseconds;

using var wake = new AutoResetEvent(false);
SimConnect sc;
try
{
    sc = new SimConnect("Crosscheck LvarRoundtrip", IntPtr.Zero, 0, wake, 0);
}
catch (COMException e)
{
    Console.Error.WriteLine($"Could not connect to MSFS (is it running?): {e.Message}");
    return 1;
}

// Measurement state. Everything runs on this one thread: ReceiveMessage dispatches the handlers below.
var phase = Phase.WaitFirstFrame;
int varIdx = 0, iter = 0;
double nextAt = 0, writeAt = 0, written = 0;
var originals = new double?[testVars.Length];
var samples = testVars.Select(_ => new List<Sample>()).ToArray();
AllVars latest = default;
long frames = 0;
double firstFrameAt = 0;
var cautionSeen = new SortedSet<double>();
bool quit = false, cancel = false;

sc.OnRecvOpen += (_, d) => Console.WriteLine($"Connected to {d.szApplicationName} {d.dwApplicationVersionMajor}.{d.dwApplicationVersionMinor}");
sc.OnRecvQuit += (_, _) => { Console.Error.WriteLine("Sim closed the connection."); quit = true; };
sc.OnRecvException += (_, d) => Console.Error.WriteLine($"SimConnect exception {(SIMCONNECT_EXCEPTION)d.dwException} (sendID {d.dwSendID}, index {d.dwIndex})");
sc.OnRecvSimobjectData += (_, d) =>
{
    if (d.dwRequestID == (uint)Req.All) OnFrame((AllVars)d.dwData[0], Now());
};

foreach (var name in testVars.Append(CautionVar))
    sc.AddToDataDefinition(Def.All, name, "number", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
sc.RegisterDataDefineStruct<AllVars>(Def.All);

// One write definition per variable, so a write touches exactly one L: var.
for (int i = 0; i < testVars.Length; i++)
{
    sc.AddToDataDefinition(WriteDef(i), testVars[i], "number", SIMCONNECT_DATATYPE.FLOAT64, 0, SimConnect.SIMCONNECT_UNUSED);
    sc.RegisterDataDefineStruct<OneVar>(WriteDef(i));
}

sc.RequestDataOnSimObject(Req.All, Def.All, SimConnect.SIMCONNECT_OBJECT_ID_USER,
    SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel = true; };

while (!quit && !cancel && phase != Phase.Done)
{
    wake.WaitOne(5);
    try { sc.ReceiveMessage(); }
    catch (COMException e) { Console.Error.WriteLine($"ReceiveMessage failed: {e.Message}"); quit = true; break; }
    Tick(Now());
}

if (cancel && !quit)
{
    Console.WriteLine("Cancelled; restoring original values.");
    for (int i = 0; i < testVars.Length; i++)
        if (originals[i] is double o) Write(i, o);
}
sc.Dispose();

if (phase == Phase.WaitFirstFrame)
{
    Console.Error.WriteLine("No data received. Is a flight with the Fenix A320 loaded?");
    return 1;
}

Report();
return 0;

// ---------------------------------------------------------------------------

void OnFrame(AllVars v, double t)
{
    if (frames++ == 0) firstFrameAt = t;
    latest = v;
    cautionSeen.Add(v.Caution);

    switch (phase)
    {
        case Phase.WaitFirstFrame:
            Console.WriteLine("Initial values:");
            for (int i = 0; i < testVars.Length; i++) Console.WriteLine($"  {testVars[i]} = {v[i]}");
            Console.WriteLine($"  {CautionVar} = {v.Caution}");
            phase = Phase.Gap;
            nextAt = t + GapMs;
            break;

        case Phase.Waiting when Matches(v[varIdx], written):
            samples[varIdx].Add(new Sample(iter, written, t - writeAt, true));
            iter++;
            phase = Phase.Gap;
            nextAt = t + GapMs;
            break;

        case Phase.Gap when iter > 0 && samples[varIdx] is { Count: > 0 } list && list[^1] is { Ok: true, Reverted: false } last
                            && !Matches(v[varIdx], last.Written):
            // The value read back but did not stick (e.g. the aircraft logic overrode it).
            list[^1] = last with { Reverted = true };
            break;

        case Phase.Restoring when Matches(v[varIdx], written):
            NextVar(t);
            break;
    }
}

void Tick(double now)
{
    switch (phase)
    {
        case Phase.WaitFirstFrame when now > 10_000:
            quit = true;
            break;

        case Phase.Waiting when now - writeAt > TimeoutMs:
            samples[varIdx].Add(new Sample(iter, written, null, false));
            iter++;
            phase = Phase.Gap;
            nextAt = now + GapMs;
            break;

        case Phase.Restoring when now - writeAt > TimeoutMs:
            Console.Error.WriteLine($"  WARNING: {testVars[varIdx]} did not read back original value {written}.");
            NextVar(now);
            break;

        case Phase.Gap when now >= nextAt:
            StartNext();
            break;
    }
}

void StartNext()
{
    if (iter == Iterations)
    {
        written = originals[varIdx]!.Value;
        writeAt = Now();
        Write(varIdx, written);
        phase = Phase.Restoring;
        return;
    }

    double current = latest[varIdx];
    if (iter == 0)
    {
        originals[varIdx] = current;
        Console.WriteLine($"Testing {testVars[varIdx]} (original {current}) ...");
    }
    written = Matches(current, 0) ? 1 : 0;
    writeAt = Now();
    Write(varIdx, written);
    phase = Phase.Waiting;
}

void NextVar(double now)
{
    var ok = samples[varIdx].Count(s => s.Ok);
    Console.WriteLine($"  done: {ok}/{Iterations} read back, restored to {originals[varIdx]}");
    varIdx++;
    iter = 0;
    if (varIdx == testVars.Length) phase = Phase.Done;
    else { phase = Phase.Gap; nextAt = now + GapMs; }
}

void Write(int i, double value) =>
    sc.SetDataOnSimObject(WriteDef(i), SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new OneVar { Value = value });

void Report()
{
    Console.WriteLine();
    Console.WriteLine($"{"variable",-26} {"n",3} {"min",7} {"median",7} {"p95",7} {"max",7} {"fail",5} {"revert",6}");
    var all = new List<double>();
    for (int i = 0; i < testVars.Length; i++)
    {
        var ms = samples[i].Where(s => s.Ok).Select(s => s.ReadbackMs!.Value).Order().ToList();
        all.AddRange(ms);
        int fail = samples[i].Count(s => !s.Ok), reverted = samples[i].Count(s => s.Reverted);
        Console.WriteLine($"{testVars[i],-26} {samples[i].Count,3} {F(Min(ms)),7} {F(Median(ms)),7} {F(P95(ms)),7} {F(Max(ms)),7} {fail,5} {reverted,6}");
    }
    all.Sort();
    Console.WriteLine($"{"ALL",-26} {all.Count,3} {F(Min(all)),7} {F(Median(all)),7} {F(P95(all)),7} {F(Max(all)),7}");

    double seconds = (Now() - firstFrameAt) / 1000;
    Console.WriteLine();
    Console.WriteLine($"{CautionVar}: sampled on {frames} frames (~{frames / seconds:F0} frames/s), values seen: {string.Join(", ", cautionSeen)}");

    var csvPath = Path.Combine(ProjectDir(), "results.csv");
    var csv = new StringBuilder("variable,iteration,written,readback_ms,ok\n");
    for (int i = 0; i < testVars.Length; i++)
        foreach (var s in samples[i])
            csv.Append(CultureInfo.InvariantCulture, $"{testVars[i]},{s.Iteration},{s.Written},{(s.ReadbackMs is double r ? r.ToString("F2", CultureInfo.InvariantCulture) : "")},{(s.Ok ? 1 : 0)}\n");
    File.WriteAllText(csvPath, csv.ToString());
    Console.WriteLine($"Samples written to {csvPath}");

    double median = Median(all);
    Console.WriteLine();
    Console.WriteLine(double.IsNaN(median)
        ? "GATE: FAIL (no successful read-backs)"
        : $"GATE: {(median < GateMs ? "PASS" : "FAIL")} (overall median {median:F1} ms, gate < {GateMs} ms)");
}

static Def WriteDef(int i) => (Def)(i + 1);
static bool Matches(double a, double b) => Math.Abs(a - b) < Eps;
static string F(double v) => double.IsNaN(v) ? "-" : v.ToString("F1", CultureInfo.InvariantCulture);
static double Min(List<double> s) => s.Count == 0 ? double.NaN : s[0];
static double Max(List<double> s) => s.Count == 0 ? double.NaN : s[^1];
static double Median(List<double> s) =>
    s.Count == 0 ? double.NaN : s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
static double P95(List<double> s) =>
    s.Count == 0 ? double.NaN : s[Math.Clamp((int)Math.Ceiling(0.95 * s.Count) - 1, 0, s.Count - 1)];

static string ProjectDir()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "LvarRoundtrip.csproj"))) return dir.FullName;
    return Directory.GetCurrentDirectory();
}

enum Phase { WaitFirstFrame, Gap, Waiting, Restoring, Done }
enum Def { All, W0, W1, W2, W3, W4 }
enum Req { All }

record Sample(int Iteration, double Written, double? ReadbackMs, bool Ok, bool Reverted = false);

[StructLayout(LayoutKind.Sequential)]
struct AllVars
{
    public double V0, V1, V2, V3, V4, Caution;
    public readonly double this[int i] => i switch { 0 => V0, 1 => V1, 2 => V2, 3 => V3, 4 => V4, _ => throw new ArgumentOutOfRangeException(nameof(i)) };
}

[StructLayout(LayoutKind.Sequential)]
struct OneVar
{
    public double Value;
}
