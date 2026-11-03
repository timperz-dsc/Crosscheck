// Read-only SimConnect client for the `observe` and `positions` commands. It defines data
// definitions (64 L: vars each) and asks for them every sim frame. It does nothing else:
// no writes, no events, no client data. ObserveTests checks this file for write calls.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;
using SC = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace ProfileProbe;

/// <summary>One sim frame: every observed var, in the order of the reader's var list.</summary>
sealed record ObserveFrame(long Frame, double TMs, double[] Values);

/// <summary>A source of frames. Read only by design: it has no member that could write.</summary>
interface IObserveSource : IAsyncDisposable
{
    event Action<ObserveFrame>? FrameReceived;
    Task StartAsync(CancellationToken ct);
}

/// <summary>One clock for frames and MCDU screens, in ms since the process started.</summary>
static class ObserveClock
{
    static readonly long Start = Stopwatch.GetTimestamp();
    public static double NowMs => Stopwatch.GetElapsedTime(Start).TotalMilliseconds;
}

sealed class ObserveReader : IObserveSource
{
    /// <summary>Fixed struct size per data definition. Unused slots repeat a harmless sim var so the sizes match.</summary>
    public const int Slots = 64;
    /// <summary>At most this many definitions (chunks of <see cref="Slots"/> vars).</summary>
    public const int MaxChunks = 4;
    public const int MaxVars = Slots * MaxChunks;
    const string Filler = "SIM ON GROUND";
    const int FirstId = 11;

    /// <summary>Sim vars get their own definitions, from this id on.</summary>
    const int SimFirstId = 1000;
    public const int MaxSimVars = 64;

    readonly IReadOnlyList<string> _vars;
    /// <summary>Positions in <see cref="_vars"/> of the L: vars and of the sim vars.</summary>
    readonly List<int> _lvars;
    readonly List<int> _simvars;
    readonly bool[] _delivered;
    readonly int _chunks;
    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    Thread? _pump;
    long _frame;

    public event Action<ObserveFrame>? FrameReceived;

    public ObserveReader(IReadOnlyList<string> vars)
    {
        _vars = vars;
        _lvars = Enumerable.Range(0, vars.Count).Where(i => !SimVars.IsSimVar(vars[i])).ToList();
        _simvars = Enumerable.Range(0, vars.Count).Where(i => SimVars.IsSimVar(vars[i])).ToList();
        if (_lvars.Count > MaxVars) throw new ArgumentException($"at most {MaxVars} L: vars, got {_lvars.Count}");
        if (_simvars.Count > MaxSimVars) throw new ArgumentException($"at most {MaxSimVars} sim vars, got {_simvars.Count}");
        _delivered = new bool[_simvars.Count];
        _chunks = Math.Max(1, (_lvars.Count + Slots - 1) / Slots);
    }

    /// <summary>Sim vars the sim has not sent a value for (a name it rejected): their column stays 0.</summary>
    public IReadOnlyList<string> MissingSimVars => _simvars.Where((_, j) => !_delivered[j]).Select(i => _vars[i]).ToList();

    public Task StartAsync(CancellationToken ct)
    {
        _pump = new Thread(Pump) { IsBackground = true, Name = "SimConnect observe" };
        _pump.Start();
        return Task.CompletedTask;
    }

    void Pump()
    {
        SC? sc = null;
        while (!_stop)
        {
            try
            {
                sc ??= Connect();
                if (sc is null) { Thread.Sleep(2000); continue; }
                _wake.WaitOne(20);
                sc.ReceiveMessage();
            }
            catch (COMException)
            {
                try { sc?.Dispose(); } catch (COMException) { }
                sc = null;
            }
        }
        sc?.Dispose();
    }

    SC? Connect()
    {
        SC sc;
        try { sc = new SC("Crosscheck Profile Probe observe (read-only)", IntPtr.Zero, 0, _wake, 0); }
        catch (COMException) { return null; }
        // L: vars: one definition per chunk of 64. SimConnect sends every chunk each frame in request
        // order, so a frame is complete when the last chunk arrives. L: vars are never rejected.
        var merged = new double[_vars.Count];
        var seen = new bool[_chunks];
        for (var c = 0; c < _chunks; c++)
        {
            for (var i = 0; i < Slots; i++)
            {
                var k = c * Slots + i;
                var (name, unit) = k < _lvars.Count ? ("L:" + _vars[_lvars[k]], "number") : (Filler, "bool");
                sc.AddToDataDefinition((Def)(FirstId + c), name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
            }
            sc.RegisterDataDefineStruct<Values>((Def)(FirstId + c));
        }
        // Sim vars ("A:NAME"): one definition each. SimConnect drops a name it does not know from its
        // definition, which in a shared chunk shifted every later value by one (gate 3 prep run,
        // 2026-11-03). On its own, a rejected sim var just never arrives and is reported as missing.
        for (var j = 0; j < _simvars.Count; j++)
        {
            var v = _vars[_simvars[j]];
            sc.AddToDataDefinition((Def)(SimFirstId + j), SimVars.Name(v), SimVars.Unit(v), SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<One>((Def)(SimFirstId + j));
        }
        sc.OnRecvSimobjectData += (_, data) =>
        {
            var id = (int)data.dwRequestID;
            if (id >= SimFirstId && id < SimFirstId + _simvars.Count)
            {
                merged[_simvars[id - SimFirstId]] = ((One)data.dwData[0]).V;
                _delivered[id - SimFirstId] = true;
                return;
            }
            var c = id - FirstId;
            if (c < 0 || c >= _chunks) return;
            var all = ((Values)data.dwData[0]).V;
            var n = Math.Min(Slots, _lvars.Count - c * Slots);
            for (var i = 0; i < n; i++) merged[_lvars[c * Slots + i]] = all[i];
            seen[c] = true;
            if (c != _chunks - 1 || !seen.All(x => x)) return;
            FrameReceived?.Invoke(new ObserveFrame(Interlocked.Increment(ref _frame), ObserveClock.NowMs, (double[])merged.Clone()));
        };
        // Every sim frame (interval 0), always sent: one message per chunk (and per sim var) per frame.
        for (var c = 0; c < _chunks; c++)
            sc.RequestDataOnSimObject((Req)(FirstId + c), (Def)(FirstId + c), SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        for (var j = 0; j < _simvars.Count; j++)
            sc.RequestDataOnSimObject((Req)(SimFirstId + j), (Def)(SimFirstId + j), SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        return sc;
    }

    enum Def { }
    enum Req { }

    public ValueTask DisposeAsync()
    {
        _stop = true;
        _wake.Set();
        _pump?.Join(TimeSpan.FromSeconds(2));
        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct One
    {
        public double V;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Values
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = Slots)]
        public double[] V;
    }
}
