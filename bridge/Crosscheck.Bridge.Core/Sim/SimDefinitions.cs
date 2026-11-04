namespace Crosscheck.Bridge.Sim;

/// <summary>
/// The SimConnect calls the write path needs, one per method. Each returns the send ID SimConnect gave the
/// call (GetLastSentPacketID), so an exception that arrives later can be traced to the call and its var.
/// The real adapter wraps SimConnect; tests use a fake that refuses definitions like the sim does.
/// </summary>
public interface ISimConnectCalls
{
    /// <summary>Registers the one-double struct for a write definition (once per slot, at connect).</summary>
    uint RegisterOneVar(int definition);
    uint ClearDefinition(int definition);
    uint AddToDefinition(int definition, string lvar);
    uint SetValue(int definition, double value);
}

/// <summary>What a SimConnect call was for, so its exception can be traced.</summary>
public enum SendKind
{
    /// <summary>A read chunk (definition, struct or request) at connect.</summary>
    ReadChunk,
    /// <summary>A write slot's struct at connect.</summary>
    Slot,
    /// <summary>Emptying a write slot before it points at another var.</summary>
    Clear,
    /// <summary>Pointing a write slot at a var (add).</summary>
    Bind,
    /// <summary>The write itself.</summary>
    Write,
}

/// <summary>One remembered call. <paramref name="Attempt"/> numbers the write it belongs to (0 for setup calls).</summary>
public sealed record SendInfo(SendKind Kind, string? Var, int Definition, long Attempt = 0, double Value = 0);

/// <summary>What a SimConnect exception means for the bridge.</summary>
public abstract record SimExceptionOutcome
{
    /// <summary>A write or the binding of its slot was refused twice in a row: that var cannot be written.</summary>
    public sealed record WriteRejected(string Var, string Why) : SimExceptionOutcome;
    /// <summary>A write or its binding was refused once: send the same value again, once, on a fresh slot.</summary>
    public sealed record Retry(string Var, double Value, string Why) : SimExceptionOutcome;
    /// <summary>Traced to a call, but no var is to blame (a refused clear, or a write already counted). Logged only.</summary>
    public sealed record Ignored(string Why) : SimExceptionOutcome;
    /// <summary>A read chunk or write slot could not be set up: the link to the sim is not complete.</summary>
    public sealed record SetupError(string Why) : SimExceptionOutcome;
    /// <summary>No call is known for this send ID (logged only, never blocks a var).</summary>
    public sealed record Unknown(string Why) : SimExceptionOutcome;
}

/// <summary>
/// The bridge's SimConnect data definitions (gate test 3 cause A). Gate tests 2 and 3 registered one
/// definition per writable var (about 1,240); SimConnect silently refused those past roughly 900–1,190, so
/// writes to vars late in the list never landed. Now reads use chunks of <see cref="ChunkSize"/> vars and
/// writes share a small pool of <see cref="PoolSize"/> definitions that are re-pointed to the var being
/// written. The total stays far below the limit for any profile (<see cref="MaxDefinitions"/>), and every
/// call's send ID is remembered, so a refused definition names its var and is never silent.
///
/// Flight 1 (2026-11-04): a slot's first bind cleared a definition the sim had never seen (only its struct was
/// registered). SimConnect answers that with UNRECOGNIZED_ID, and the var was blocked although its write landed.
/// Now a slot is only cleared once it holds a var, a refused clear blocks nothing, and a var is blocked only after
/// <see cref="StrikesToBlock"/> refusals traced to its own calls (the first one is retried once).
/// Not thread-safe: the adapter uses it from its pump thread only.
/// </summary>
public sealed class SimDefinitions
{
    /// <summary>Vars per read definition. Each definition is one change-only request.</summary>
    public const int ChunkSize = 32;
    /// <summary>Write definitions shared by all writable vars.</summary>
    public const int PoolSize = 8;
    /// <summary>First write definition ID (read chunks use 0, 1, 2 …).</summary>
    public const int WriteDefBase = 10_000;
    /// <summary>The most definitions the bridge ever registers. Far below what SimConnect refused in gate test 3.</summary>
    public const int MaxDefinitions = 256;
    /// <summary>Refused writes of one var, traced by send ID and without a landed write between them, that block it.</summary>
    public const int StrikesToBlock = 2;
    /// <summary>Send IDs remembered (connect needs about 35 per read chunk; writes 1–3 each).</summary>
    private const int Remembered = 16_384;

    private readonly string?[] _slotVar = new string?[PoolSize];
    private readonly long[] _slotUsed = new long[PoolSize];
    /// <summary>The slot's definition holds a var in the sim (an add went out), so it must be cleared before re-use.</summary>
    private readonly bool[] _slotDefined = new bool[PoolSize];
    /// <summary>A clear was refused: what the definition holds is unknown, so the slot is not used again on this connection.</summary>
    private readonly bool[] _slotRetired = new bool[PoolSize];
    private readonly Dictionary<string, int> _varSlot = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, SendInfo> _sends = [];
    private readonly Queue<uint> _sendOrder = new();
    /// <summary>var → refusals counted since its last landed write.</summary>
    private readonly Dictionary<string, int> _strikes = new(StringComparer.Ordinal);
    /// <summary>var → the last write attempt that was counted as a strike (one strike per attempt).</summary>
    private readonly Dictionary<string, long> _struckAttempt = new(StringComparer.Ordinal);
    /// <summary>var → (newest write attempt, its value).</summary>
    private readonly Dictionary<string, (long Attempt, double Value)> _lastWrite = new(StringComparer.Ordinal);
    private long _tick;
    private long _attempt;
    private uint _lastNoted;

    /// <summary>Read definitions needed for this many watched vars.</summary>
    public static int ReadChunks(int watched) => (watched + ChunkSize - 1) / ChunkSize;

    /// <summary>All definitions for this many watched vars: read chunks plus the write pool.</summary>
    public static int Total(int watched) => ReadChunks(watched) + PoolSize;

    /// <summary>null if the plan fits; otherwise why the bridge must not start (refused loudly, never silently).</summary>
    public static string? BudgetError(int watched) => Total(watched) <= MaxDefinitions ? null
        : $"the profile needs {Total(watched)} SimConnect definitions for {watched} watched vars; the bridge allows at most {MaxDefinitions}";

    /// <summary>The var a write slot points at right now (tests, logs).</summary>
    public string? SlotVar(int slot) => _slotVar[slot];

    /// <summary>Refusals counted for this var since its last landed write (tests, logs).</summary>
    public int Strikes(string name) => _strikes.GetValueOrDefault(name);

    /// <summary>A new connection: no slot points anywhere, no send is known.</summary>
    public void Reset()
    {
        Array.Clear(_slotVar);
        Array.Clear(_slotUsed);
        Array.Clear(_slotDefined);
        Array.Clear(_slotRetired);
        _varSlot.Clear();
        _sends.Clear();
        _sendOrder.Clear();
        _strikes.Clear();
        _struckAttempt.Clear();
        _lastWrite.Clear();
        _lastNoted = 0;
    }

    /// <summary>Remembers a call made outside this class (read chunks at connect).</summary>
    public void Note(uint sendId, SendKind kind, string? var, int definition) => Note(sendId, new SendInfo(kind, var, definition));

    private void Note(uint sendId, SendInfo info)
    {
        if (sendId == 0) return;
        _lastNoted = sendId;
        if (_sends.TryAdd(sendId, info)) _sendOrder.Enqueue(sendId);
        else _sends[sendId] = info;
        while (_sendOrder.Count > Remembered) _sends.Remove(_sendOrder.Dequeue());
    }

    /// <summary>Registers the struct of every write slot (once per connection).</summary>
    public void SetupSlots(ISimConnectCalls calls)
    {
        for (var slot = 0; slot < PoolSize; slot++)
        {
            // Registering a struct may send no packet; then the "last sent" ID is the previous call's, which must
            // keep its own meaning (e.g. a read chunk request).
            var before = _lastNoted;
            var id = calls.RegisterOneVar(WriteDefBase + slot);
            if (id != before) Note(id, SendKind.Slot, null, WriteDefBase + slot);
        }
    }

    /// <summary>
    /// Writes one value: a slot already pointing at the var is reused, otherwise the least recently used slot is
    /// re-pointed first: cleared (only if it already holds a var), then the var is added. Calls are made in order on
    /// one thread and SimConnect handles them in order, so a write always follows its own binding.
    /// </summary>
    public void Write(ISimConnectCalls calls, string name, double value)
    {
        var slot = Acquire(name, out var rebind);
        var def = WriteDefBase + slot;
        var attempt = ++_attempt;
        _lastWrite[name] = (attempt, value);
        if (rebind)
        {
            if (_slotDefined[slot]) Note(calls.ClearDefinition(def), new SendInfo(SendKind.Clear, name, def, attempt, value));
            Note(calls.AddToDefinition(def, name), new SendInfo(SendKind.Bind, name, def, attempt, value));
            _slotDefined[slot] = true;
        }
        Note(calls.SetValue(def, value), new SendInfo(SendKind.Write, name, def, attempt, value));
    }

    /// <summary>A read-back shows this var at the value last written: its writes work, earlier refusals no longer count.</summary>
    public void NoteLanded(string name, double value)
    {
        if (_lastWrite.TryGetValue(name, out var last) && last.Value == value) _strikes.Remove(name);
    }

    private int Acquire(string name, out bool rebind)
    {
        _tick++;
        if (_varSlot.TryGetValue(name, out var slot))
        {
            rebind = false;
            _slotUsed[slot] = _tick;
            return slot;
        }
        slot = -1;
        for (var i = 0; i < PoolSize; i++)
            if (!_slotRetired[i] && (slot < 0 || _slotUsed[i] < _slotUsed[slot])) slot = i;
        if (slot < 0) throw new InvalidOperationException("no SimConnect write slot left on this connection");
        if (_slotVar[slot] is { } old) _varSlot.Remove(old);
        _slotVar[slot] = name;
        _varSlot[name] = slot;
        _slotUsed[slot] = _tick;
        rebind = true;
        return slot;
    }

    private void Free(string name)
    {
        if (!_varSlot.Remove(name, out var slot)) return;
        _slotVar[slot] = null;
        _slotUsed[slot] = 0;
    }

    /// <summary>
    /// What an exception with this send ID means. Only a send ID that matches a remembered call is traced; a var is
    /// blocked only after <see cref="StrikesToBlock"/> refusals of its own add or write, each from a different attempt.
    /// </summary>
    public SimExceptionOutcome OnException(uint sendId, string exception)
    {
        if (!_sends.TryGetValue(sendId, out var info))
            return new SimExceptionOutcome.Unknown($"SimConnect exception {exception} for an unknown call (send {sendId})");
        var slot = info.Definition - WriteDefBase;
        switch (info.Kind)
        {
            case SendKind.Clear:
            {
                // Not the var's fault. What the definition holds is now unknown, so this slot is not used again on this
                // connection; the var moves to another slot on its next write.
                if (slot is >= 0 and < PoolSize) _slotRetired[slot] = true;
                if (info.Var is { } v) Free(v);
                var left = _slotRetired.Count(r => !r);
                if (left == 0) return new SimExceptionOutcome.SetupError($"SimConnect refused clearing the last write slot ({exception})");
                return new SimExceptionOutcome.Ignored($"SimConnect refused clearing write slot {slot} for {info.Var} ({exception}); slot retired, {left} left, var not blocked");
            }
            case SendKind.Bind or SendKind.Write when info.Var is { } name:
            {
                Free(name);
                // A refused add leaves the definition empty (or never created): the next bind needs no clear.
                if (info.Kind == SendKind.Bind && slot is >= 0 and < PoolSize) _slotDefined[slot] = false;
                var step = info.Kind == SendKind.Bind ? "definition" : "write";
                var why = $"SimConnect refused the {step} ({exception})";
                if (_struckAttempt.TryGetValue(name, out var counted) && counted == info.Attempt)
                    return new SimExceptionOutcome.Ignored($"{name}: {why}; same attempt already counted");
                _struckAttempt[name] = info.Attempt;
                var strikes = _strikes[name] = _strikes.GetValueOrDefault(name) + 1;
                if (strikes >= StrikesToBlock) return new SimExceptionOutcome.WriteRejected(name, why);
                // A newer write of this var is already on its way: it is the retry.
                if (_lastWrite.TryGetValue(name, out var last) && last.Attempt != info.Attempt)
                    return new SimExceptionOutcome.Ignored($"{name}: {why}; strike {strikes} of {StrikesToBlock}, a newer write follows");
                return new SimExceptionOutcome.Retry(name, info.Value, $"{why}; strike {strikes} of {StrikesToBlock}, retrying once");
            }
            case SendKind.Slot:
                return new SimExceptionOutcome.SetupError($"SimConnect refused write slot {slot} ({exception})");
            default:
                return new SimExceptionOutcome.SetupError($"SimConnect refused read definition {info.Definition}{(info.Var is { } v2 ? $" ({v2} …)" : "")} ({exception})");
        }
    }
}
