using Crosscheck.Bridge.Sim;

namespace Crosscheck.Bridge.Follow;

public sealed record FakePhysicsOptions
{
    /// <summary>One sim frame every this many ms (16 ≈ 60 fps).</summary>
    public int FrameMs { get; init; } = 16;
    /// <summary>Flags are reported every this many frames (the real link: every 6th).</summary>
    public int FlagsEvery { get; init; } = 6;
    /// <summary>No timer: the test pushes every own frame by hand with <see cref="FakePhysicsLink.PushFrame"/>.</summary>
    public bool Manual { get; init; }
}

/// <summary>
/// In-memory physics link for --sim fake and the tests. The own aircraft is, in order of precedence: frozen
/// where it was last placed (any freeze on), flying a script (<see cref="Fly"/>), or moving on with its last
/// velocity (released after a placement: it keeps rolling or flying, like the real sim). Every write passes the
/// same role gate and <see cref="FollowerSafety"/> as the real link.
/// </summary>
public sealed class FakePhysicsLink : IPhysicsLink
{
    private readonly object _gate = new();
    private readonly FakePhysicsOptions _options;
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private readonly Dictionary<string, double> _lvars = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refusedReported = new(StringComparer.Ordinal);
    private Timer? _timer;
    private volatile bool _follower;
    private bool _connected;
    private bool _latLon, _alt, _att;
    private PhysicsFrame _state;
    private double _stateAt;
    private IReadOnlyList<PhysicsFrame>? _script;
    private double _scriptStartSim;
    private long _frames;

    public FakePhysicsLink(FakePhysicsOptions? options = null)
    {
        _options = options ?? new FakePhysicsOptions();
        var first = Synthetic.Circuit()[0];
        _state = first;
    }

    public bool Connected
    {
        get { lock (_gate) return _connected; }
    }

    public bool IsFollower => _follower;
    public event Action<bool>? ConnectedChanged;
    public event Action<OwnFrame>? FrameRead;
    public event Action<FreezeFlags>? FlagsRead;
    public event Action<string, string>? WriteRefused;

    // ---------------------------------------------------------------- what tests look at

    /// <summary>Every placement written: (sim time, frame).</summary>
    public List<(double At, PhysicsFrame Frame)> Placed { get; } = [];
    /// <summary>For each entry of <see cref="Placed"/>: whether elevator, aileron and rudder were written with it.</summary>
    public List<bool> SurfacesWritten { get; } = [];
    /// <summary>Every freeze event sent: (event, on).</summary>
    public List<(string Event, bool On)> FreezeEvents { get; } = [];
    /// <summary>Placements and freezes refused by the role gate.</summary>
    public int GateRefused { get; private set; }
    public FreezeFlags Flags
    {
        get { lock (_gate) return new FreezeFlags(_latLon, _alt, _att); }
    }
    public PhysicsFrame State
    {
        get { lock (_gate) return _state; }
    }
    public double SimTime => _clock.Elapsed.TotalSeconds;

    private readonly List<PhysicsFrame> _history = [];

    /// <summary>The own aircraft's frames as read (T = this sim's clock), the last 60 s: the truth for tests.</summary>
    public List<PhysicsFrame> History()
    {
        lock (_gate) return [.. _history];
    }

    public List<(double At, PhysicsFrame Frame)> PlacedCopy()
    {
        lock (_gate) return [.. Placed];
    }

    public List<(string Event, bool On)> FreezeEventsCopy()
    {
        lock (_gate) return [.. FreezeEvents];
    }

    // ---------------------------------------------------------------- test controls

    /// <summary>The pilot flies this script from its frame at <paramref name="fromT"/> on (re-timed to now).</summary>
    public void Fly(IReadOnlyList<PhysicsFrame> script, double fromT = 0)
    {
        lock (_gate)
        {
            _script = script;
            _scriptStartSim = SimTime - fromT;
        }
    }

    /// <summary>The aircraft sits still at <paramref name="at"/> (no script).</summary>
    public void Park(PhysicsFrame at)
    {
        lock (_gate)
        {
            _script = null;
            _state = at with { VelWorldX = 0, VelWorldY = 0, VelWorldZ = 0, VelBodyY = 0, VelBodyZ = 0, GroundSpeedKt = 0 };
            _stateAt = SimTime;
        }
    }

    public void SetLvar(string name, double value)
    {
        lock (_gate) _lvars[name] = value;
    }

    /// <summary>
    /// One own sim frame, by hand (for <see cref="FakePhysicsOptions.Manual"/>): the frame as the real link would read it
    /// (T = this sim's clock, chosen by the test). Returns after the handlers ran.
    /// </summary>
    public void PushFrame(PhysicsFrame frame)
    {
        OwnFrame own;
        lock (_gate)
        {
            _state = frame;
            _stateAt = frame.T;
            own = new OwnFrame(frame, new Dictionary<string, double>(_lvars));
            _history.Add(frame);
            if (_history.Count > 4000) _history.RemoveRange(0, 1000);
        }
        FrameRead?.Invoke(own);
    }

    /// <summary>The sim switches a freeze off by itself (the watchdog must send it again).</summary>
    public void DropFreeze(string ev)
    {
        lock (_gate) SetFlag(ev, false);
    }

    /// <summary>The sim refuses this placement var from now on.</summary>
    public void Refuse(string var)
    {
        lock (_gate) _refused.Add(var);
    }

    /// <summary>Simulates a crashed bridge: freezes stay on in the sim, nothing releases them.</summary>
    public void SetFlagsDirectly(bool on)
    {
        lock (_gate) _latLon = _alt = _att = on;
    }

    public void SetConnected(bool connected)
    {
        lock (_gate)
        {
            if (_connected == connected) return;
            _connected = connected;
        }
        ConnectedChanged?.Invoke(connected);
    }

    // ---------------------------------------------------------------- IPhysicsLink

    public void Start(IReadOnlyList<string> lvars)
    {
        lock (_gate)
            foreach (var n in lvars) _lvars.TryAdd(n, 0);
        _clock.Start();
        _stateAt = 0;
        if (!_options.Manual) _timer = new Timer(_ => Tick(), null, _options.FrameMs, _options.FrameMs);
        SetConnected(true);
    }

    public void SetFollower(bool follower) => _follower = follower;

    public void Place(in PhysicsFrame frame, bool writeSurfaces = true)
    {
        if (!_follower)
        {
            lock (_gate) GateRefused++;
            throw new SafetyViolationException("placement refused: this side is not the follower");
        }
        foreach (var (v, _) in FollowerSafety.Placement) FollowerSafety.CheckWriteVar(v);
        lock (_gate)
        {
            var now = SimTime;
            Placed.Add((now, frame));
            SurfacesWritten.Add(writeSurfaces);
            _state = frame;
            _stateAt = now;
            _script = null; // a placed aircraft is no longer flown by its own pilot
            foreach (var (v, _) in FollowerSafety.Extras)
                if (_refused.Contains(v) && _refusedReported.Add(v))
                    _ = Task.Run(() => WriteRefused?.Invoke(v, "refused (test)"));
        }
    }

    public void SendFreeze(string ev, bool on)
    {
        FollowerSafety.CheckEvent(ev);
        if (on && !_follower)
        {
            lock (_gate) GateRefused++;
            throw new SafetyViolationException($"{ev} refused: this side is not the follower");
        }
        lock (_gate)
        {
            FreezeEvents.Add((ev, on));
            SetFlag(ev, on);
        }
    }

    private void SetFlag(string ev, bool on)
    {
        switch (ev)
        {
            case FollowerSafety.FreezeLatLon: _latLon = on; break;
            case FollowerSafety.FreezeAltitude: _alt = on; break;
            case FollowerSafety.FreezeAttitude: _att = on; break;
        }
    }

    private void Tick()
    {
        OwnFrame own;
        FreezeFlags? flags = null;
        lock (_gate)
        {
            if (!_connected) return;
            var now = SimTime;
            if (_latLon || _alt || _att)
            {
                // Frozen: stays where it was placed.
            }
            else if (_script is { } script)
            {
                var f = Interpolator.Sample(script, now - _scriptStartSim);
                _state = f;
                _stateAt = now;
            }
            else
            {
                // Released: moves on with its last velocity (it keeps rolling or flying).
                _state = Interpolator.Extrapolate(_state, null, _state.T + (now - _stateAt));
                _stateAt = now;
            }
            own = new OwnFrame(_state with { T = now }, new Dictionary<string, double>(_lvars));
            _state = _state with { T = now };
            _history.Add(own.Frame);
            if (_history.Count > 4000) _history.RemoveRange(0, 1000);
            if (++_frames % _options.FlagsEvery == 0) flags = new FreezeFlags(_latLon, _alt, _att);
        }
        try
        {
            FrameRead?.Invoke(own);
            if (flags is { } fl) FlagsRead?.Invoke(fl);
        }
        catch
        {
            // Like the real link: a handler error never stops the frames.
        }
    }

    public ValueTask DisposeAsync()
    {
        _timer?.Dispose();
        return ValueTask.CompletedTask;
    }
}
