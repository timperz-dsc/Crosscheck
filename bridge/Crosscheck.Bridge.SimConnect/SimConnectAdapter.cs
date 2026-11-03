// The real sim adapter. Reads and writes profile L: vars through SimConnect data definitions
// (proven in spike/LvarRoundtrip and spike/VarTypes). It maps no client events and sends no
// events of any kind, so it cannot stop, speed up, move or reposition the aircraft.
// A test (SafetyTripwireTests) fails if event APIs ever appear in this file.
//
// Definitions (gate test 3 cause A): reads use chunks of 32 vars; writes share a pool of 8 definitions that
// are re-pointed to the var being written (SimDefinitions). Every SimConnect exception is traced to its call:
// a refused write blocks that var (WriteRejected, a red line), a refused read or slot definition is a setup
// error (SetupError, the bridge refuses to start a session). Never silent.

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
    internal const int ChunkSize = SimDefinitions.ChunkSize;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, double> _values = new(StringComparer.Ordinal);
    /// <summary>Vars written but not yet read back: name → time the write was sent.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _awaitingReadBack = new(StringComparer.Ordinal);
    private static readonly TimeSpan ReadBackGiveUp = TimeSpan.FromMilliseconds(100);
    private readonly ConcurrentQueue<(string Name, double Value, TaskCompletionSource Done)> _writes = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    /// <summary>Used from the pump thread only.</summary>
    private readonly SimDefinitions _definitions = new();
    private readonly List<string> _setupErrors = [];
    private string[][] _chunks = [];
    /// <summary>The write path's calls on the current connection (pump thread).</summary>
    private Calls? _calls;
    private Thread? _pump;
    private volatile bool _stop;
    private volatile SimState _state = SimState.Disconnected;

    public SimConnectAdapter(Profile profile, ILogger log)
    {
        _log = log;
        Guard = new SimSafetyGuard(profile);
    }

    public SimState State => _state;
    public SimSafetyGuard Guard { get; }
    public GuardedStreamWriter Stream { get; } = new();

    public event Action<SimState>? StateChanged;
    public event Action<string, double>? ValueChanged;
    public event Action<string, string>? WriteRejected;
    public event Action<string>? SetupError;
    public event Action<string>? SimException;

    public IReadOnlyList<string> SetupErrors
    {
        get { lock (_setupErrors) return _setupErrors.ToList(); }
    }

    public Task StartAsync(SimPlan plan, CancellationToken ct)
    {
        // Too many definitions for this profile: refuse loudly instead of losing writes silently.
        if (SimDefinitions.BudgetError(plan.Watched.Count) is { } tooMany)
        {
            AddSetupError(tooMany);
            return Task.CompletedTask;
        }
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
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _writes.Enqueue((name, value, done));
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
                    _definitions.Write(_calls!, w.Name, w.Value);
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
        _definitions.Reset();
        lock (_setupErrors) _setupErrors.Clear();
        sc.OnRecvOpen += (_, d) =>
        {
            _log.LogInformation("Connected to {App} {Major}.{Minor}", d.szApplicationName, d.dwApplicationVersionMajor, d.dwApplicationVersionMinor);
            SetState(SimState.Connected);
        };
        sc.OnRecvQuit += (_, _) => _quit = true;
        sc.OnRecvException += (_, d) => OnException((SIMCONNECT_EXCEPTION)d.dwException, d.dwSendID, d.dwIndex);
        sc.OnRecvSimobjectData += OnData;

        for (var c = 0; c < _chunks.Length; c++)
        {
            var names = _chunks[c];
            for (var i = 0; i < ChunkSize; i++)
            {
                // The last chunk is padded by repeating its last var, so every chunk has the same layout.
                var name = names[Math.Min(i, names.Length - 1)];
                sc.AddToDataDefinition((Def)c, "L:" + name, "number", SIMCONNECT_DATATYPE.FLOAT64, 0, Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_UNUSED);
                _definitions.Note(sc.GetLastSentPacketID(), SendKind.ReadChunk, name, c);
            }
            sc.RegisterDataDefineStruct<Chunk>((Def)c);
            _definitions.Note(sc.GetLastSentPacketID(), SendKind.ReadChunk, names[0], c);
            sc.RequestDataOnSimObject((Req)c, (Def)c, Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
            _definitions.Note(sc.GetLastSentPacketID(), SendKind.ReadChunk, names[0], c);
        }
        _calls = new Calls(sc);
        _definitions.SetupSlots(_calls);
        return sc;
    }

    /// <summary>Pump thread. Traces the exception to its call: a refused write blocks its var, a refused setup call stops sessions.</summary>
    private void OnException(SIMCONNECT_EXCEPTION exception, uint sendId, uint index)
    {
        var outcome = _definitions.OnException(sendId, exception.ToString());
        var text = outcome switch
        {
            SimExceptionOutcome.WriteRejected r => $"{r.Var}: {r.Why}",
            SimExceptionOutcome.SetupError s => s.Why,
            SimExceptionOutcome.Unknown u => u.Why,
            _ => exception.ToString(),
        } + $" (index {index})";
        _log.LogWarning("SimConnect exception: {Text}", text);
        try { SimException?.Invoke(text); } catch (Exception e) { _log.LogError(e, "SimException handler failed"); }
        switch (outcome)
        {
            case SimExceptionOutcome.WriteRejected r:
                _awaitingReadBack.TryRemove(r.Var, out _);
                try { WriteRejected?.Invoke(r.Var, r.Why); } catch (Exception e) { _log.LogError(e, "WriteRejected handler failed"); }
                break;
            case SimExceptionOutcome.SetupError s:
                AddSetupError(s.Why);
                break;
            // Unknown: logged (SimException) only. Only a refused call the bridge can name blocks sessions.
        }
    }

    private void AddSetupError(string why)
    {
        lock (_setupErrors) _setupErrors.Add(why);
        try { SetupError?.Invoke(why); } catch (Exception e) { _log.LogError(e, "SetupError handler failed"); }
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

    /// <summary>The write path's SimConnect calls, each returning its send ID.</summary>
    private sealed class Calls(Microsoft.FlightSimulator.SimConnect.SimConnect sc) : ISimConnectCalls
    {
        public uint RegisterOneVar(int definition)
        {
            sc.RegisterDataDefineStruct<OneVar>((Def)definition);
            return sc.GetLastSentPacketID();
        }

        public uint ClearDefinition(int definition)
        {
            sc.ClearDataDefinition((Def)definition);
            return sc.GetLastSentPacketID();
        }

        public uint AddToDefinition(int definition, string lvar)
        {
            sc.AddToDataDefinition((Def)definition, "L:" + lvar, "number", SIMCONNECT_DATATYPE.FLOAT64, 0, Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_UNUSED);
            return sc.GetLastSentPacketID();
        }

        public uint SetValue(int definition, double value)
        {
            sc.SetDataOnSimObject((Def)definition, Microsoft.FlightSimulator.SimConnect.SimConnect.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, new OneVar { Value = value });
            return sc.GetLastSentPacketID();
        }
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
