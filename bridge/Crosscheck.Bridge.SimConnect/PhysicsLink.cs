// The physics link to the real sim (CRO-13). Ported from the CRO-17 follower spike (SimLink + Player).
//
// Its own SimConnect connection ("Crosscheck Physics") and thread. Reads the own aircraft every sim frame
// (read only, both sides). ONLY while this side is the follower (SetFollower(true)) it writes the placement
// and sends freeze events. This file is the only place in the bridge that maps or sends a sim event, and only
// the three FREEZE_*_SET events from FollowerSafety can be mapped or sent. Stopping the sim, changing its
// speed, moving the aircraft any other way and every other event are impossible here. SafetyTripwireTests
// checks this file.
//
// Timing (research section 6.8): frames are timed with the sim's own clock (SIMULATION TIME, FrameClock).
// When several state messages are handled in one go, only the newest is raised, so the follower writes
// once per new sim frame.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Crosscheck.Bridge.Follow;
using Crosscheck.Bridge.Sim;
using Microsoft.Extensions.Logging;
using Microsoft.FlightSimulator.SimConnect;
using SC = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace Crosscheck.Bridge.SimConnect;

public sealed class PhysicsLink : IPhysicsLink
{
    private const int MaxDrain = 32;
    private const int MaxLvars = 8;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

    private readonly ILogger _log;
    private readonly object _scLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly Dictionary<uint, int> _extraSends = [];
    private readonly Queue<uint> _extraOrder = new();
    private readonly bool[] _extraRefused = new bool[FollowerSafety.Extras.Count];
    private string[] _lvars = [];
    private SC? _sc;
    private Thread? _pump;
    private volatile bool _stop;
    private volatile bool _quit;
    private volatile bool _follower;
    private volatile bool _connected;
    private FrameClock _clock = new();
    // Messages handled in this go: only the newest state is raised.
    private bool _gotMessage;
    private ReadData? _pending;
    private FlagsData? _pendingFlags;
    private bool _eventsMapped;

    public PhysicsLink(ILogger log)
    {
        _log = log;
    }

    public bool Connected => _connected;
    public bool IsFollower => _follower;
    public event Action<bool>? ConnectedChanged;
    public event Action<OwnFrame>? FrameRead;
    public event Action<FreezeFlags>? FlagsRead;
    public event Action<string, string>? WriteRefused;

    public void Start(IReadOnlyList<string> lvars)
    {
        if (FollowerSafety.Read.Count + MaxLvars != ReadCount || FollowerSafety.Placement.Count != PlacementCount)
            throw new InvalidOperationException("PhysicsLink structs do not match FollowerSafety");
        _lvars = lvars.Take(MaxLvars).ToArray();
        _pump = new Thread(Pump) { IsBackground = true, Name = "SimConnect physics" };
        _pump.Start();
    }

    public void SetFollower(bool follower) => _follower = follower;

    public void Place(in PhysicsFrame f)
    {
        if (!_follower) throw new SafetyViolationException("placement refused: this side is not the follower");
        var values = FollowerSafety.PlacementValues(f);
        var extras = FollowerSafety.ExtraValues(f);
        lock (_scLock)
        {
            if (_sc is not { } sc) return;
            sc.SetDataOnSimObject(Def.Place, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new Placement { V = values });
            for (var i = 0; i < extras.Length; i++)
            {
                if (_extraRefused[i]) continue;
                sc.SetDataOnSimObject(Def.ExtraBase + (uint)i, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_DATA_SET_FLAG.DEFAULT, new One { V = extras[i] });
                var id = sc.GetLastSentPacketID();
                _extraSends[id] = i;
                _extraOrder.Enqueue(id);
                while (_extraOrder.Count > 1024) _extraSends.Remove(_extraOrder.Dequeue());
            }
        }
    }

    public void SendFreeze(string ev, bool on)
    {
        FollowerSafety.CheckEvent(ev);
        if (on && !_follower) throw new SafetyViolationException($"{ev} refused: this side is not the follower");
        lock (_scLock)
        {
            if (_sc is not { } sc || !_eventsMapped) throw new InvalidOperationException("the sim is not connected");
            var id = (uint)IndexOf(ev);
            sc.TransmitClientEvent(SC.SIMCONNECT_OBJECT_ID_USER, (Ev)id, on ? 1u : 0u, Group.Highest, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
    }

    private static int IndexOf(string ev)
    {
        for (var i = 0; i < FollowerSafety.Events.Count; i++)
            if (FollowerSafety.Events[i] == ev) return i;
        throw new SafetyViolationException($"event {ev} is not allowed");
    }

    // ------------------------------------------------------------------ pump thread

    private void Pump()
    {
        while (!_stop)
        {
            try
            {
                if (_sc is null)
                {
                    if (!TryConnect())
                    {
                        _stopped.Wait(RetryEvery);
                        continue;
                    }
                }
                _wake.WaitOne(10);
                ReadData? state;
                FlagsData? flags;
                lock (_scLock)
                {
                    var n = 0;
                    do
                    {
                        _gotMessage = false;
                        _sc!.ReceiveMessage();
                    } while (_gotMessage && ++n < MaxDrain && !_quit);
                    (state, _pending) = (_pending, null);
                    (flags, _pendingFlags) = (_pendingFlags, null);
                }
                if (_quit)
                {
                    Disconnect("the sim closed the connection");
                    continue;
                }
                // Raised outside the lock: the handlers place and freeze through this link.
                if (state is { } s) RaiseFrame(s);
                if (flags is { } f) FlagsRead?.Invoke(new FreezeFlags(f.LatLon >= 0.5, f.Altitude >= 0.5, f.Attitude >= 0.5));
            }
            catch (COMException e)
            {
                Disconnect(e.Message);
            }
            catch (Exception e)
            {
                // Never let anything here take the bridge (or the sim session) down.
                _log.LogError(e, "SimConnect physics pump error");
                Disconnect(e.Message);
            }
        }
        lock (_scLock)
        {
            try { _sc?.Dispose(); } catch (COMException) { }
            _sc = null;
        }
    }

    private void RaiseFrame(ReadData d)
    {
        var v = d.V;
        if (!_clock.Next(_sw.Elapsed.TotalSeconds, v[0])) return; // the same sim frame again
        var frame = FollowerSafety.FromRead(v) with { T = _clock.Now };
        var lvars = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = 0; i < _lvars.Length; i++) lvars[_lvars[i]] = v[FollowerSafety.Read.Count + i];
        FrameRead?.Invoke(new OwnFrame(frame, lvars));
    }

    private bool TryConnect()
    {
        SC sc;
        try
        {
            sc = new SC("Crosscheck Physics", IntPtr.Zero, 0, _wake, 0);
        }
        catch (COMException)
        {
            return false;
        }
        lock (_scLock)
        {
            _quit = false;
            _clock = new FrameClock();
            Array.Clear(_extraRefused);
            _extraSends.Clear();
            _extraOrder.Clear();
            sc.OnRecvQuit += (_, _) => _quit = true;
            sc.OnRecvException += OnException;
            sc.OnRecvSimobjectData += OnData;

            // Read: the own aircraft every sim frame, plus the profile's stream L: vars, padded to a fixed size.
            foreach (var (var, unit) in FollowerSafety.Read)
                sc.AddToDataDefinition(Def.Read, var, unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
            for (var i = 0; i < MaxLvars; i++)
            {
                var name = _lvars.Length == 0 ? null : _lvars[Math.Min(i, _lvars.Length - 1)];
                if (name is null) sc.AddToDataDefinition(Def.Read, "SIMULATION TIME", "seconds", SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
                else sc.AddToDataDefinition(Def.Read, "L:" + name, "number", SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
            }
            sc.RegisterDataDefineStruct<ReadData>(Def.Read);
            sc.RequestDataOnSimObject(Req.Read, Def.Read, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);

            // Read: the freeze flags every 6th frame (the watchdog).
            foreach (var (_, stateVar) in FollowerSafety.StateVars)
                sc.AddToDataDefinition(Def.Flags, stateVar, "bool", SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<FlagsData>(Def.Flags);
            sc.RequestDataOnSimObject(Req.Flags, Def.Flags, SC.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME, SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 6, 0);

            // Write definitions (used only while the follower): the placement, and each extra on its own.
            foreach (var (var, unit) in FollowerSafety.Placement)
                sc.AddToDataDefinition(Def.Place, FollowerSafety.CheckWriteVar(var), unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
            sc.RegisterDataDefineStruct<Placement>(Def.Place);
            for (var i = 0; i < FollowerSafety.Extras.Count; i++)
            {
                var (var, unit) = FollowerSafety.Extras[i];
                sc.AddToDataDefinition(Def.ExtraBase + (uint)i, FollowerSafety.CheckWriteVar(var), unit, SIMCONNECT_DATATYPE.FLOAT64, 0, SC.SIMCONNECT_UNUSED);
                sc.RegisterDataDefineStruct<One>(Def.ExtraBase + (uint)i);
            }

            // The three freeze events, and nothing else. Event id = index in FollowerSafety.Events.
            for (var i = 0; i < FollowerSafety.Events.Count; i++)
                sc.MapClientEventToSimEvent((Ev)(uint)i, FollowerSafety.CheckEvent(FollowerSafety.Events[i]));
            _eventsMapped = true;
            _sc = sc;
        }
        SetConnected(true);
        _log.LogInformation("Physics link connected");
        return true;
    }

    private void OnException(SC sender, SIMCONNECT_RECV_EXCEPTION d)
    {
        _gotMessage = true;
        var ex = ((SIMCONNECT_EXCEPTION)d.dwException).ToString();
        if (_extraSends.TryGetValue(d.dwSendID, out var i) && !_extraRefused[i])
        {
            _extraRefused[i] = true;
            var var = FollowerSafety.Extras[i].Var;
            _log.LogWarning("The sim refused {Var} ({Why}); it is no longer written", var, ex);
            Task.Run(() => WriteRefused?.Invoke(var, ex));
            return;
        }
        _log.LogWarning("SimConnect physics exception {Exception} (send {SendId}, index {Index})", ex, d.dwSendID, d.dwIndex);
    }

    private void OnData(SC sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        _gotMessage = true;
        switch ((Req)data.dwRequestID)
        {
            case Req.Read: _pending = (ReadData)data.dwData[0]; break;
            case Req.Flags: _pendingFlags = (FlagsData)data.dwData[0]; break;
        }
    }

    private void Disconnect(string why)
    {
        lock (_scLock)
        {
            if (_sc is not null)
            {
                _log.LogWarning("Physics link lost ({Why}); retrying every {Seconds} s", why, RetryEvery.TotalSeconds);
                try { _sc.Dispose(); } catch (COMException) { }
            }
            _sc = null;
            _eventsMapped = false;
        }
        SetConnected(false);
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        try { ConnectedChanged?.Invoke(connected); } catch (Exception e) { _log.LogError(e, "ConnectedChanged handler failed"); }
    }

    public ValueTask DisposeAsync()
    {
        _stop = true;
        _stopped.Set();
        _wake.Set();
        _pump?.Join(TimeSpan.FromSeconds(2));
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// crosscheck restore: connects, switches all three freezes off, reads the flags back for up to 2 s.
    /// For a crashed bridge that left the aircraft frozen. Returns (all off, what happened).
    /// </summary>
    public static async Task<(bool AllOff, string Text)> RestoreAsync(ILogger log, TimeSpan connectTimeout)
    {
        var link = new PhysicsLink(log);
        FreezeFlags? flags = null;
        var asked = false;
        var flagsAfterAsk = new TaskCompletionSource<FreezeFlags>(TaskCreationOptions.RunContinuationsAsynchronously);
        link.FlagsRead += f =>
        {
            flags = f;
            if (asked) flagsAfterAsk.TrySetResult(f);
        };
        link.Start([]);
        try
        {
            var until = DateTime.UtcNow + connectTimeout;
            while (!link.Connected && DateTime.UtcNow < until) await Task.Delay(100);
            if (!link.Connected) return (false, "The sim was not found. Start MSFS and run crosscheck restore again.");
            foreach (var ev in FollowerSafety.Events) link.SendFreeze(ev, false);
            await Task.Delay(200);
            asked = true;
            var done = await Task.WhenAny(flagsAfterAsk.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            if (done != flagsAfterAsk.Task) return (false, "Freezes switched off, but the sim did not confirm it. Check that the aircraft moves.");
            var f = flagsAfterAsk.Task.Result;
            return f.AnyOn
                ? (false, "WARNING: the sim still reports a freeze on. Run crosscheck restore again.")
                : (true, "Released: the sim reports all freezes off.");
        }
        finally
        {
            await link.DisposeAsync();
        }
    }

    private enum Def : uint
    {
        Read = 1,
        Flags = 2,
        Place = 3,
        ExtraBase = 10,
    }

    private enum Req : uint
    {
        Read = 1,
        Flags = 2,
    }

    private enum Ev : uint { }

    private enum Group : uint { Highest = 1 }

    private const int ReadCount = 18 + MaxLvars;
    private const int PlacementCount = 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadData
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = ReadCount)]
        public double[] V;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlagsData
    {
        public double LatLon, Altitude, Attitude;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = PlacementCount)]
        public double[] V;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct One
    {
        public double V;
    }
}
