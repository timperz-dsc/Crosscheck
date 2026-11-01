// The real sim adapter. Reads and writes profile L: vars through SimConnect data definitions
// (proven in spike/LvarRoundtrip and spike/VarTypes). It maps no client events and sends no
// events of any kind, so it cannot stop, speed up, move or reposition the aircraft.
// A test (SafetyTripwireTests) fails if event APIs ever appear in this file.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Crosscheck.Bridge.Sim;
using Crosscheck.Profiles.Model;
using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;

namespace Crosscheck.Bridge.SimConnect;

public sealed class SimConnectAdapter : ISimAdapter
{
    /// <summary>Vars per read definition. Each definition is one change-only request.</summary>
    internal const int ChunkSize = 32;
    private const int WriteDefBase = 10_000;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, double> _values = new(StringComparer.Ordinal);
    /// <summary>Vars written but not yet read back: name → time the write was sent.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _awaitingReadBack = new(StringComparer.Ordinal);
    private static readonly TimeSpan ReadBackGiveUp = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentQueue<(int Def, string Name, double Value, TaskCompletionSource Done)> _writes = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private string[][] _chunks = [];
    private Dictionary<string, int> _writeIndex = new(StringComparer.Ordinal);
    private Thread? _pump;
    private volatile bool _stop;
    private volatile SimState _state = SimState.Disconnected;

    public SimConnectAdapter(Profile profile, ILogger log)
    {
        _log = log;
        Guard = new SimSafetyGuard(profile);
        _writeIndex = profile.Vars.Where(v => Guard.IsWritable(v.Name))
            .Select((v, i) => (v.Name, i)).ToDictionary(x => x.Name, x => WriteDefBase + x.i, StringComparer.Ordinal);
    }

    public SimState State => _state;
    public SimSafetyGuard Guard { get; }
    public GuardedStreamWriter Stream { get; } = new();

    public event Action<SimState>? StateChanged;
    public event Action<string, double>? ValueChanged;

    public Task StartAsync(SimPlan plan, CancellationToken ct)
    {
        _chunks = plan.Watched.Chunk(ChunkSize).ToArray();
        _pump = new Thread(Pump) { IsBackground = true, Name = "SimConnect" };
        _pump.Start();
        return Task.CompletedTask;
    }

    public bool TryGet(string name, out double value)
    {
        if (_state == SimState.Connected) return _values.TryGetValue(name, out value);
        value = 0;
        return false;
    }

    public bool IsFresh(string name) =>
        !_awaitingReadBack.TryGetValue(name, out var sent) || DateTime.UtcNow - sent > ReadBackGiveUp;

    public Task WriteAsync(string name, double value)
    {
        Guard.CheckWrite(name, value);
        if (_state != SimState.Connected) throw new InvalidOperationException("sim not connected");
        var def = _writeIndex[name];
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _writes.Enqueue((def, name, value, done));
        _wake.Set();
        return done.Task;
    }

    // ------------------------------------------------------------------ pump thread

    private void Pump()
    {
        Microsoft.FlightSimulator.SimConnect.SimConnect? sc = null;
        while (!_stop)
        {
            try
            {
                if (sc is null)
                {
                    sc = TryConnect();
                    if (sc is null)
                    {
                        _stopped.Wait(RetryEvery);
                        continue;
                    }
                }
                _wake.WaitOne(10);
                while (_writes.TryDequeue(out var w))
                {
                    sc.SetDataOnSimObject((Def)w.Def, Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_OBJECT_ID_USER,
                        SIMCONNECT_DATA_SET_FLAG.DEFAULT, new OneVar { Value = w.Value });
                    _awaitingReadBack[w.Name] = DateTime.UtcNow;
                    w.Done.TrySetResult();
                }
                sc.ReceiveMessage();
                if (_quit) Disconnect(ref sc, "the sim closed the connection");
            }
            catch (COMException e)
            {
                Disconnect(ref sc, e.Message);
            }
            catch (Exception e)
            {
                // Never let anything here take the bridge (or the sim session) down.
                _log.LogError(e, "SimConnect pump error");
                Disconnect(ref sc, e.Message);
            }
        }
        sc?.Dispose();
    }

    private volatile bool _quit;

    private Microsoft.FlightSimulator.SimConnect.SimConnect? TryConnect()
    {
        Microsoft.FlightSimulator.SimConnect.SimConnect sc;
        try
        {
            // Finds the sim through SimConnect itself, never by process name.
            sc = new Microsoft.FlightSimulator.SimConnect.SimConnect("Crosscheck Bridge", IntPtr.Zero, 0, _wake, 0);
        }
        catch (COMException)
        {
            return null; // sim not running yet: try again later
        }
        _quit = false;
        _values.Clear();
        sc.OnRecvOpen += (_, d) =>
        {
            _log.LogInformation("Connected to {App} {Major}.{Minor}", d.szApplicationName, d.dwApplicationVersionMajor, d.dwApplicationVersionMinor);
            SetState(SimState.Connected);
        };
        sc.OnRecvQuit += (_, _) => _quit = true;
        sc.OnRecvException += (_, d) => _log.LogWarning("SimConnect exception {Ex} (send {Send}, index {Index})", (SIMCONNECT_EXCEPTION)d.dwException, d.dwSendID, d.dwIndex);
        sc.OnRecvSimobjectData += OnData;

        for (var c = 0; c < _chunks.Length; c++)
        {
            var names = _chunks[c];
            for (var i = 0; i < ChunkSize; i++)
            {
                // The last chunk is padded by repeating its last var, so every chunk has the same layout.
                var name = names[Math.Min(i, names.Length - 1)];
                sc.AddToDataDefinition((Def)c, "L:" + name, "number", SIMCONNECT_DATATYPE.FLOAT64, 0, Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_UNUSED);
            }
            sc.RegisterDataDefineStruct<Chunk>((Def)c);
            sc.RequestDataOnSimObject((Req)c, (Def)c, Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        }
        foreach (var (name, def) in _writeIndex)
        {
            sc.AddToDataDefinition((Def)def, "L:" + name, "number", SIMCONNECT_DATATYPE.FLOAT64, 0, Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<OneVar>((Def)def);
        }
        return sc;
    }

    private void OnData(Microsoft.FlightSimulator.SimConnect.SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        var c = (int)data.dwRequestID;
        if (c < 0 || c >= _chunks.Length) return;
        var names = _chunks[c];
        var chunk = (Chunk)data.dwData[0];
        for (var i = 0; i < names.Length; i++)
        {
            var value = chunk.V[i];
            if (_values.TryGetValue(names[i], out var old) && old == value) continue;
            _values[names[i]] = value;
            _awaitingReadBack.TryRemove(names[i], out _);
            ValueChanged?.Invoke(names[i], value);
        }
    }

    private void Disconnect(ref Microsoft.FlightSimulator.SimConnect.SimConnect? sc, string why)
    {
        if (sc is not null)
        {
            _log.LogWarning("Sim connection lost ({Why}); retrying every {Seconds} s", why, RetryEvery.TotalSeconds);
            try { sc.Dispose(); } catch (COMException) { }
            sc = null;
        }
        while (_writes.TryDequeue(out var w)) w.Done.TrySetException(new InvalidOperationException("sim disconnected"));
        SetState(SimState.Disconnected);
    }

    private void SetState(SimState state)
    {
        if (_state == state) return;
        _state = state;
        try { StateChanged?.Invoke(state); } catch (Exception e) { _log.LogError(e, "StateChanged handler failed"); }
    }

    public ValueTask DisposeAsync()
    {
        _stop = true;
        _stopped.Set();
        _wake.Set();
        _pump?.Join(TimeSpan.FromSeconds(2));
        return ValueTask.CompletedTask;
    }

    private enum Def { }

    private enum Req { }

    [StructLayout(LayoutKind.Sequential)]
    private struct OneVar
    {
        public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Chunk
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ChunkSize)]
        public double[] V;
    }
}
