using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Sim;

public sealed record FakeSimOptions
{
    /// <summary>Changes are reported once per frame, coalesced, like SimConnect's CHANGED flag.</summary>
    public int FrameMs { get; init; } = 16;
    /// <summary>Effect vars (verify.lvar) and encoder displays follow after this lag (Fenix: ~105–120 ms).</summary>
    public int EffectLagMs { get; init; } = 110;
    /// <summary>Key press to MCDU screen change (Fenix: ~450 ms).</summary>
    public int McduLagMs { get; init; } = 150;
}

/// <summary>
/// In-memory sim with the same behaviour model as server/test/fake/fakeSim.ts: encoders with
/// display vars, effect vars that lag, MCDU screens fed by key pulses, values that refuse to
/// change and values the aircraft keeps changing back. Writes go through the same
/// <see cref="SimSafetyGuard"/> as the real adapter.
/// </summary>
public sealed class FakeSimAdapter : ISimAdapter
{
    private readonly object _gate = new();
    private readonly FakeSimOptions _options;
    private readonly SimSafetyGuard _guard;
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _reported = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VarEntry> _specs;
    private readonly Dictionary<int, string> _screens = new() { [1] = "", [2] = "" };
    private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double Value, int RevertMs)> _stuck = new(StringComparer.Ordinal);
    /// <summary>Keys whose effect lamp does not change on a press (system unpowered).</summary>
    private readonly HashSet<string> _unpowered = new(StringComparer.Ordinal);
    /// <summary>Hold buttons (FIRE TEST): lamp on only after the button was held this long, off on release.</summary>
    private readonly Dictionary<string, (string Lamp, int MinHoldMs)> _holdEffects = new(StringComparer.Ordinal);
    private HashSet<string> _watched = new(StringComparer.Ordinal);
    private Timer? _frame;
    private SimState _state = SimState.Disconnected;

    public FakeSimAdapter(Profile profile, FakeSimOptions? options = null)
    {
        _options = options ?? new FakeSimOptions();
        _guard = new SimSafetyGuard(profile);
        _specs = profile.Vars.ToDictionary(v => v.Name, StringComparer.Ordinal);
        foreach (var v in profile.Vars)
        {
            _values[v.Name] = 0;
            if (v.Display is { } d) _values.TryAdd(d, 0);
        }
    }

    public SimState State => _state;
    public SimSafetyGuard Guard => _guard;
    public GuardedStreamWriter Stream { get; } = new();
    /// <summary>Every write that reached this sim through the adapter (not pilot actions).</summary>
    public List<(string Name, double Value)> Writes { get; } = [];
    /// <summary>The same writes with the time they reached the sim (ms, monotonic), for timing tests.</summary>
    public List<(string Name, double Value, double AtMs)> TimedWrites { get; } = [];
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    public event Action<SimState>? StateChanged;
    public event Action<string, double>? ValueChanged;
    /// <summary>MCDU screen n changed (fake GraphQL listens to this).</summary>
    public event Action<int>? McduChanged;

    public Task StartAsync(SimPlan plan, CancellationToken ct)
    {
        lock (_gate)
        {
            _watched = plan.Watched.ToHashSet(StringComparer.Ordinal);
            // Watched names outside the profile (controls not synced yet) exist in the cockpit too.
            foreach (var name in _watched) _values.TryAdd(name, 0);
        }
        _frame = new Timer(_ => Frame(), null, _options.FrameMs, _options.FrameMs);
        SetConnected(true);
        return Task.CompletedTask;
    }

    public bool TryGet(string name, out double value)
    {
        lock (_gate)
        {
            if (_state == SimState.Connected && _watched.Contains(name)) return _values.TryGetValue(name, out value);
            value = 0;
            return false;
        }
    }

    /// <summary>The fake stores writes at once, so reads are always fresh.</summary>
    public bool IsFresh(string name) => true;

    public Task WriteAsync(string name, double value)
    {
        if (_state != SimState.Connected) throw new InvalidOperationException("sim not connected");
        _guard.CheckWrite(name, value);
        lock (_gate)
        {
            Writes.Add((name, value));
            TimedWrites.Add((name, value, Clock.Elapsed.TotalMilliseconds));
            if (_refused.Contains(name)) return Task.CompletedTask;
            Set(name, value);
        }
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- test controls

    /// <summary>The pilot operates a switch or knob in this cockpit.</summary>
    public void PilotSet(string name, double value)
    {
        lock (_gate) Set(name, value);
    }

    /// <summary>The pilot turns an encoder by <paramref name="clicks"/>.</summary>
    public void PilotTurn(string name, double clicks)
    {
        lock (_gate) Set(name, _values[name] + clicks);
    }

    /// <summary>
    /// The pilot presses a button and releases it after <paramref name="holdMs"/>: an edge button counts +1
    /// and +1 (Fenix push buttons), a momentary one goes 1 then 0.
    /// </summary>
    public async Task PilotPress(string name, int holdMs = 100)
    {
        var edge = _specs[name].Press == PressMode.Edge;
        lock (_gate) Set(name, edge ? _values[name] + 1 : 1);
        await Task.Delay(holdMs);
        lock (_gate) Set(name, edge ? _values[name] + 1 : 0);
    }

    /// <summary>Like FIRE TEST: <paramref name="lamp"/> comes on only after the button was held <paramref name="minHoldMs"/>.</summary>
    public void HoldEffect(string button, string lamp, int minHoldMs)
    {
        lock (_gate)
        {
            _holdEffects[button] = (lamp, minHoldMs);
            _values.TryAdd(lamp, 0);
        }
    }

    /// <summary>The aircraft's own logic changes a value (no write, no pilot).</summary>
    public void Logic(string name, double value)
    {
        lock (_gate) _values[name] = value;
    }

    public void Refuse(string name)
    {
        lock (_gate) _refused.Add(name);
    }

    /// <summary>A key press is stored, but its effect lamp does not change (system unpowered).</summary>
    public void Unpower(string name)
    {
        lock (_gate) _unpowered.Add(name);
    }

    /// <summary>Every change of <paramref name="name"/> is reverted after <paramref name="revertMs"/>.</summary>
    public void Stick(string name, double value, int revertMs = 30)
    {
        lock (_gate) _stuck[name] = (value, revertMs);
    }

    public void Unstick(string name)
    {
        lock (_gate) _stuck.Remove(name);
    }

    public double Get(string name)
    {
        lock (_gate) return _values[name];
    }

    public string McduText(int cdu)
    {
        lock (_gate) return _screens[cdu];
    }

    /// <summary>Simulates the sim exiting (false) or starting again (true).</summary>
    public void SetConnected(bool connected)
    {
        var next = connected ? SimState.Connected : SimState.Disconnected;
        lock (_gate)
        {
            if (_state == next) return;
            _state = next;
            if (connected) _reported.Clear(); // first data after (re)connect reports every value
        }
        StateChanged?.Invoke(next);
    }

    // ---------------------------------------------------------------- model

    /// <summary>A value lands in the sim (lock held). Side effects follow with their lag.</summary>
    private void Set(string name, double value)
    {
        if (!_values.TryGetValue(name, out var old)) throw new ArgumentException($"unknown var {name}");
        _values[name] = value;
        if (old == value) return;

        if (_stuck.TryGetValue(name, out var stuck) && value != stuck.Value)
            Later(stuck.RevertMs, () => _values[name] = stuck.Value);

        if (!_specs.TryGetValue(name, out var spec)) return;
        if (spec.Class == SyncClass.Delta && spec.Display is { } display)
        {
            var change = value - old;
            Later(_options.EffectLagMs, () => _values[display] += change);
        }
        else if (spec.Class == SyncClass.Pulse && spec.Verify?.Lvar is { } lamp && _values.ContainsKey(lamp))
        {
            // A toggling key (FD): each press toggles its lamp, unless the system is unpowered.
            var pressed = Pressed(spec, old, value);
            if (pressed && !_unpowered.Contains(name)) Later(_options.EffectLagMs, () => _values[lamp] = _values[lamp] == 0 ? 1 : 0);
        }
        else if (spec.Verify?.Lvar is { } effect && _values.ContainsKey(effect))
        {
            Later(_options.EffectLagMs, () => _values[effect] = _values[name]);
        }
        if (_holdEffects.TryGetValue(name, out var hold))
        {
            if (Pressed(spec, old, value))
                Later(hold.MinHoldMs, () => { if (_values[name] == value) _values[hold.Lamp] = 1; });
            else
                Later(_options.EffectLagMs, () => _values[hold.Lamp] = 0);
        }
        if (spec.Class == SyncClass.Pulse && spec.Verify?.Mcdu is int cdu && Pressed(spec, old, value))
        {
            var key = KeyName(name);
            Later(_options.McduLagMs, () => _screens[cdu] += key, () => McduChanged?.Invoke(cdu));
        }
    }

    /// <summary>A press (down edge): an edge button steps up to an odd value; a momentary one leaves 0.</summary>
    private static bool Pressed(VarEntry spec, double old, double value) =>
        spec.Press == PressMode.Edge ? value > old && ((long)value & 1) == 1 : old == 0 && value != 0;

    private static string KeyName(string name)
    {
        var i = name.IndexOf("_KEY_", StringComparison.Ordinal);
        return i >= 0 ? name[(i + 5)..] : name;
    }

    private void Later(int ms, Action change, Action? after = null)
    {
        _ = Task.Delay(ms).ContinueWith(_ =>
        {
            lock (_gate) change();
            after?.Invoke();
        }, TaskScheduler.Default);
    }

    private void Frame()
    {
        List<(string, double)> changed = [];
        lock (_gate)
        {
            if (_state != SimState.Connected) return;
            foreach (var name in _watched)
            {
                if (!_values.TryGetValue(name, out var v)) continue;
                if (_reported.TryGetValue(name, out var r) && r == v) continue;
                _reported[name] = v;
                changed.Add((name, v));
            }
        }
        foreach (var (name, v) in changed) ValueChanged?.Invoke(name, v);
        Interlocked.Increment(ref _framesDelivered);
    }

    private long _framesDelivered;
    /// <summary>Frames whose changes were delivered to ValueChanged (tests wait for the first one).</summary>
    public long FramesDelivered => Interlocked.Read(ref _framesDelivered);

    public ValueTask DisposeAsync()
    {
        _frame?.Dispose();
        return ValueTask.CompletedTask;
    }
}
