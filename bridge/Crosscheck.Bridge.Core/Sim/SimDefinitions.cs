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
    /// <summary>Pointing a write slot at a var (clear + add).</summary>
    Bind,
    /// <summary>The write itself.</summary>
    Write,
}

public sealed record SendInfo(SendKind Kind, string? Var, int Definition);

/// <summary>What a SimConnect exception means for the bridge.</summary>
public abstract record SimExceptionOutcome
{
    /// <summary>A write or the binding of its slot was refused: that var cannot be written.</summary>
    public sealed record WriteRejected(string Var, string Why) : SimExceptionOutcome;
    /// <summary>A read chunk or write slot could not be set up: the link to the sim is not complete.</summary>
    public sealed record SetupError(string Why) : SimExceptionOutcome;
    /// <summary>No call is known for this send ID (logged only).</summary>
    public sealed record Unknown(string Why) : SimExceptionOutcome;
}

/// <summary>
/// The bridge's SimConnect data definitions (gate test 3 cause A). Gate tests 2 and 3 registered one
/// definition per writable var (about 1,240); SimConnect silently refused those past roughly 900–1,190, so
/// writes to vars late in the list never landed. Now reads use chunks of <see cref="ChunkSize"/> vars and
/// writes share a small pool of <see cref="PoolSize"/> definitions that are re-pointed to the var being
/// written. The total stays far below the limit for any profile (<see cref="MaxDefinitions"/>), and every
/// call's send ID is remembered, so a refused definition names its var and is never silent.
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
    /// <summary>Send IDs remembered (connect needs about 35 per read chunk; writes 1–3 each).</summary>
    private const int Remembered = 16_384;

    private readonly string?[] _slotVar = new string?[PoolSize];
    private readonly long[] _slotUsed = new long[PoolSize];
    private readonly Dictionary<string, int> _varSlot = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, SendInfo> _sends = [];
    private readonly Queue<uint> _sendOrder = new();
    private long _tick;

    /// <summary>Read definitions needed for this many watched vars.</summary>
    public static int ReadChunks(int watched) => (watched + ChunkSize - 1) / ChunkSize;

    /// <summary>All definitions for this many watched vars: read chunks plus the write pool.</summary>
    public static int Total(int watched) => ReadChunks(watched) + PoolSize;

    /// <summary>null if the plan fits; otherwise why the bridge must not start (refused loudly, never silently).</summary>
    public static string? BudgetError(int watched) => Total(watched) <= MaxDefinitions ? null
        : $"the profile needs {Total(watched)} SimConnect definitions for {watched} watched vars; the bridge allows at most {MaxDefinitions}";

    /// <summary>The var a write slot points at right now (tests, logs).</summary>
    public string? SlotVar(int slot) => _slotVar[slot];

    /// <summary>A new connection: no slot points anywhere, no send is known.</summary>
    public void Reset()
    {
        Array.Clear(_slotVar);
        Array.Clear(_slotUsed);
        _varSlot.Clear();
        _sends.Clear();
        _sendOrder.Clear();
    }

    /// <summary>Remembers a call made outside this class (read chunks at connect).</summary>
    public void Note(uint sendId, SendKind kind, string? var, int definition)
    {
        if (sendId == 0) return;
        if (_sends.TryAdd(sendId, new SendInfo(kind, var, definition))) _sendOrder.Enqueue(sendId);
        else _sends[sendId] = new SendInfo(kind, var, definition);
        while (_sendOrder.Count > Remembered) _sends.Remove(_sendOrder.Dequeue());
    }

    /// <summary>Registers the struct of every write slot (once per connection).</summary>
    public void SetupSlots(ISimConnectCalls calls)
    {
        for (var slot = 0; slot < PoolSize; slot++)
            Note(calls.RegisterOneVar(WriteDefBase + slot), SendKind.Slot, null, WriteDefBase + slot);
    }

    /// <summary>
    /// Writes one value: a slot already pointing at the var is reused, otherwise the least recently used slot
    /// is re-pointed (clear + add) first. Calls are made in order on one thread, so a slot is never re-pointed
    /// between its binding and its write.
    /// </summary>
    public void Write(ISimConnectCalls calls, string name, double value)
    {
        var slot = Acquire(name, out var rebind);
        var def = WriteDefBase + slot;
        if (rebind)
        {
            Note(calls.ClearDefinition(def), SendKind.Bind, name, def);
            Note(calls.AddToDefinition(def, name), SendKind.Bind, name, def);
        }
        Note(calls.SetValue(def, value), SendKind.Write, name, def);
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
        slot = 0;
        for (var i = 1; i < PoolSize; i++)
            if (_slotUsed[i] < _slotUsed[slot]) slot = i;
        if (_slotVar[slot] is { } old) _varSlot.Remove(old);
        _slotVar[slot] = name;
        _varSlot[name] = slot;
        _slotUsed[slot] = _tick;
        rebind = true;
        return slot;
    }

    /// <summary>What an exception with this send ID means. A refused write frees its slot for the next var.</summary>
    public SimExceptionOutcome OnException(uint sendId, string exception)
    {
        if (!_sends.TryGetValue(sendId, out var info))
            return new SimExceptionOutcome.Unknown($"SimConnect exception {exception} for an unknown call (send {sendId})");
        switch (info.Kind)
        {
            case SendKind.Bind or SendKind.Write when info.Var is { } name:
                if (_varSlot.Remove(name, out var slot))
                {
                    _slotVar[slot] = null;
                    _slotUsed[slot] = 0;
                }
                var step = info.Kind == SendKind.Bind ? "definition" : "write";
                return new SimExceptionOutcome.WriteRejected(name, $"SimConnect refused the {step} ({exception})");
            case SendKind.Slot:
                return new SimExceptionOutcome.SetupError($"SimConnect refused write slot {info.Definition - WriteDefBase} ({exception})");
            default:
                return new SimExceptionOutcome.SetupError($"SimConnect refused read definition {info.Definition}{(info.Var is { } v ? $" ({v} …)" : "")} ({exception})");
        }
    }
}
