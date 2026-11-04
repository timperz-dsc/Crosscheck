// Plays a recording into this sim as the follower (CRO-17 spike; offline, own sim only).
// Every sim frame: read the follower back, step the PlaybackSession (network simulation,
// playout buffer, metrics; all in Follower.Core), write the follower's position, attitude and
// velocities. Method B also switches the sim's freezes on, method C slew. Whatever ends the
// run (normal end, stop key, Ctrl+C, a crash, process exit, the sim quitting), every freeze
// and slew this run switched on is switched off again through PlacementState.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Crosscheck.Follower;
using Microsoft.FlightSimulator.SimConnect;
using SC = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace Crosscheck.Follower.Sim;

sealed class Player
{
    private readonly PlaybackSession _session;
    private readonly PlaybackSettings _settings;
    private readonly string _outRoot;
    private readonly string _recordingName;
    private readonly object _scLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly List<string> _notes = [];
    private SC? _sc;
    private PlacementState? _placement;
    private readonly Stopwatch _clock = new();
    private bool _engaged;
    private bool _finished;
    private bool _quit;
    private Frame _lastActual;
    private bool _haveActual;
    private FlagsData _flags;
    private double _flagsAt = double.NaN;

    public Player(PlaybackSession session, string outRoot, string recordingName)
    {
        _session = session;
        _settings = session.Settings;
        _outRoot = outRoot;
        _recordingName = recordingName;
    }

    public int Run(StopSignal stop)
    {
        var started = DateTime.UtcNow;
        Console.WriteLine("connecting to the sim ...");
        while (_sc is null && !stop.IsSet)
        {
            _sc = SimLink.TryConnect("Crosscheck Follower Player (spike, offline only)", _wake);
            if (_sc is null) stop.Wait(TimeSpan.FromSeconds(2));
        }
        if (_sc is null) return 1;

        _placement = new PlacementState((ev, data) =>
        {
            lock (_scLock)
                if (_sc is { } sc) SimLink.Send(sc, ev, data);
        });
        // Last line of defence: a crash, a closed console window or a shutdown still undoes freeze and slew.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => EmergencyRelease("process exit");
        AppDomain.CurrentDomain.UnhandledException += (_, _) => EmergencyRelease("unhandled exception");
        using var closed = PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => EmergencyRelease("console window closed"));
        using var shutdown = PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => EmergencyRelease("shutdown"));

        string ended;
        try
        {
            lock (_scLock)
            {
                _sc.OnRecvQuit += (_, _) => _quit = true;
                _sc.OnRecvException += (_, d) => Console.WriteLine($"SimConnect exception {(SIMCONNECT_EXCEPTION)d.dwException} (send {d.dwSendID}, index {d.dwIndex})");
                _sc.OnRecvSimobjectData += OnData;
                SimLink.MapAllowedEvents(_sc);
                SimLink.DefinePlacement(_sc);
                SimLink.RequestFlags(_sc);
                SimLink.RequestState(_sc);
            }
            Console.WriteLine($"playing: method {_settings.Method}, {_settings.RateHz:0} Hz, {_settings.Network.LatencyMs:0} ± {_settings.Network.JitterMs:0} ms, buffer {_settings.Playout.BufferMs:0} ms. Press S or Esc (or Ctrl+C) to stop.");
            while (!stop.IsSet && !_quit && !_finished)
            {
                _wake.WaitOne(10);
                lock (_scLock) _sc.ReceiveMessage();
            }
            ended = _finished ? "normal end of the recording" : _quit ? "the sim closed" : $"stopped ({stop.Reason})";
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            ended = $"error: {e.Message}";
            Console.WriteLine($"\n{ended}");
        }
        finally
        {
            Release();
        }

        var inAir = _haveActual && !_lastActual.OnGround;
        if (_finished && !inAir && _lastActual.GroundSpeedKt <= EndCheck.StoppedKt) ended += ", stopped on the ground";
        else if (_finished && inAir) ended += _settings.AllowAirEnd ? ", in the air (--allow-air-end)" : ", but the sim reports the follower in the air";
        if (_placement is { Reasserts: > 0 } pl) _notes.Add($"freeze/slew sent again {pl.Reasserts} times because the sim reported it off");
        _notes.Add(_settings.Method switch
        {
            Method.A => "method A: position, attitude and velocities written every frame; no freeze",
            Method.B => "method B: as A, with the sim's freeze lat/lon, altitude and attitude on",
            _ => "method C: slew on; position and attitude written every frame",
        });

        var info = Report.Build(_session, "sim", _recordingName, started, ended, inAir, _placement?.Reasserts ?? 0, _notes);
        var dir = Report.Write(_outRoot, info, _session.Metrics.Rows);
        Console.WriteLine();
        Console.WriteLine(Report.Markdown(info));
        Console.WriteLine($"run folder: {dir}");
        if (inAir) Console.WriteLine("\n*** The aircraft is in the air and yours: fly it. ***");
        return 0;
    }

    private void OnData(SC sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID == (uint)Req.Flags)
        {
            _flags = (FlagsData)data.dwData[0];
            _flagsAt = _clock.Elapsed.TotalSeconds;
            CheckFlags();
            return;
        }
        if (data.dwRequestID != (uint)Req.State || _finished || _placement is not { IsUndone: false } placement) return;

        if (!_clock.IsRunning) _clock.Start();
        var t = _clock.Elapsed.TotalSeconds;
        var actual = SimLink.ToFrame(t, (StateData)data.dwData[0]);
        _lastActual = actual;
        _haveActual = true;

        var target = _session.OnSimFrame(t, actual);
        var slewing = _settings.Method == Method.C;
        SimLink.Place(sender, target.State, _settings.Velocities, slewing);
        if (!_engaged)
        {
            // Freeze or slew after the first placement, so the aircraft is frozen where the recording starts.
            _engaged = true;
            foreach (var ev in AllowedEvents.Engage(_settings.Method)) placement.Engage(ev);
        }
        if (_session.Finished) _finished = true;
        if (_session.Metrics.Rows.Count % 120 == 1)
            Console.Write($"\r{t,6:0} s  {target.Status,-12}  {actual.GroundSpeedKt,4:0} kt  {(actual.OnGround ? "ground" : "air   ")}   ");
    }

    private void CheckFlags()
    {
        if (_placement is not { IsUndone: false } placement || !_engaged || _clock.Elapsed.TotalSeconds < 1) return;
        foreach (var ev in placement.Engaged)
            if (SimLink.FlagFor(ev, _flags) < 0.5 && placement.Reassert(ev))
                Console.WriteLine($"\n{ev}: the sim reports it off; sent again");
    }

    /// <summary>Undo freeze and slew, then read the flags back for up to 1.5 s.</summary>
    private void Release()
    {
        if (_placement is null) return;
        try
        {
            var undone = _placement.Undo();
            if (undone.Count > 0) Console.WriteLine($"\nreleased: {string.Join(", ", undone.Select(u => u.Event))}");
            var asked = _clock.Elapsed.TotalSeconds;
            var deadline = Stopwatch.StartNew();
            while (_sc is not null && !_quit && deadline.Elapsed < TimeSpan.FromSeconds(1.5))
            {
                _wake.WaitOne(20);
                lock (_scLock) _sc.ReceiveMessage();
                if (_flagsAt > asked && AllOff()) break;
            }
            if (_sc is not null && !_quit)
            {
                var msg = AllOff() ? "sim reports freeze and slew off" : "WARNING: the sim still reports a freeze or slew on. Run `follower-sim restore`.";
                Console.WriteLine(msg);
                _notes.Add(msg);
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"\nWARNING: release failed ({e.Message}). Run `follower-sim restore` now.");
            _notes.Add($"release failed: {e.Message}");
        }
        finally
        {
            lock (_scLock)
            {
                try { _sc?.Dispose(); } catch (COMException) { }
                _sc = null;
            }
        }
    }

    private bool AllOff() => _flags.LatLon < 0.5 && _flags.Altitude < 0.5 && _flags.Attitude < 0.5 && _flags.Slew < 0.5;

    /// <summary>From another thread (process exit, crash): send the undo at once, no waiting.</summary>
    private void EmergencyRelease(string why)
    {
        try
        {
            if (_placement?.Undo() is { Count: > 0 } undone)
                Console.Error.WriteLine($"{why}: released {string.Join(", ", undone.Select(u => u.Event))}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"{why}: release failed ({e.Message}). Run `follower-sim restore`.");
        }
    }
}
