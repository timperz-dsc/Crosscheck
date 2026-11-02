// Read-only SimConnect client for the `observe` command (CRO-29 part 1). It defines one data
// definition with every observed L: var and asks for it every sim frame. It does nothing else:
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
    /// <summary>Fixed struct size. Unused slots repeat a harmless sim var so the sizes match.</summary>
    public const int Slots = 64;
    const string Filler = "SIM ON GROUND";

    enum Def { Observe = 11 }
    enum Req { Observe = 11 }

    readonly IReadOnlyList<string> _vars;
    readonly AutoResetEvent _wake = new(false);
    volatile bool _stop;
    Thread? _pump;
    long _frame;

    public event Action<ObserveFrame>? FrameReceived;

    public ObserveReader(IReadOnlyList<string> vars)
    {
        if (vars.Count > Slots) throw new ArgumentException($"at most {Slots} vars, got {vars.Count}");
        _vars = vars;
    }

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
        for (var i = 0; i < Slots; i++)
        {
            var (name, unit) = i < _vars.Count ? ("L:" + _vars[i], "number") : (Filler, "bool");
            sc.AddToDataDefinition(Def.Observe, name, unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
        }
        sc.RegisterDataDefineStruct<Values>(Def.Observe);
        sc.OnRecvSimobjectData += (_, data) =>
        {
            if (data.dwRequestID != (uint)Req.Observe) return;
            var t = ObserveClock.NowMs;
            var all = ((Values)data.dwData[0]).V;
            FrameReceived?.Invoke(new ObserveFrame(Interlocked.Increment(ref _frame), t, all[.._vars.Count]));
        };
        // Every sim frame (interval 0), always sent: one message is one frame.
        sc.RequestDataOnSimObject(Req.Observe, Def.Observe, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        return sc;
    }

    public ValueTask DisposeAsync()
    {
        _stop = true;
        _wake.Set();
        _pump?.Join(TimeSpan.FromSeconds(2));
        return ValueTask.CompletedTask;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Values
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = Slots)]
        public double[] V;
    }
}
