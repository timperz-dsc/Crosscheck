namespace Crosscheck.Bridge.Sync;

/// <summary>
/// Remembers our own writes so their read-back is never mistaken for a pilot action and
/// sent back (echo). Each write registers the value we expect to see and a deadline.
/// A change that matches a pending value is ours: consumed together with older entries.
/// A change that matches nothing means something else changed the var: its entries are
/// dropped, and the change counts as a pilot action.
/// </summary>
public sealed class EchoTracker(IClock clock)
{
    private readonly Dictionary<string, List<(double Value, double Deadline, bool Stale)>> _pending = new(StringComparer.Ordinal);
    /// <summary>The value of our own write whose read-back was consumed last, per var.</summary>
    private readonly Dictionary<string, double> _lastConsumed = new(StringComparer.Ordinal);
    private long _suppressed;

    public long Suppressed => _suppressed;

    /// <param name="stale">
    /// A value the sim already showed when we wrote over it (an encoder flush): its late report is ours only
    /// until our next write was read back. After that the same value means a pilot moved it again.
    /// </param>
    public void Expect(string name, double value, double windowMs, bool stale = false)
    {
        if (!_pending.TryGetValue(name, out var list)) _pending[name] = list = [];
        list.Add((value, clock.NowMs + windowMs, stale));
    }

    /// <summary>
    /// true if the sim shows exactly our last read-back write while a newer write of ours is still on its
    /// way: an in-between value of our own quick writes, never a pilot action (gate test 2 cause F).
    /// </summary>
    public bool InBetween(string name, double shown) =>
        Pending(name) > 0 && _lastConsumed.TryGetValue(name, out var last) && last == shown;

    /// <summary>true if this change is the read-back of our own write (do not send it).</summary>
    /// <param name="fresh">The sim already read back our latest write: stale entries no longer match.</param>
    public bool TryConsume(string name, double value, bool fresh = false)
    {
        if (!_pending.TryGetValue(name, out var list)) return false;
        var now = clock.NowMs;
        list.RemoveAll(e => e.Deadline < now);
        var index = list.FindIndex(e => e.Value == value && !(e.Stale && fresh));
        if (index < 0)
        {
            _pending.Remove(name);
            _lastConsumed.Remove(name);
            return false;
        }
        list.RemoveRange(0, index + 1);
        _lastConsumed[name] = value;
        if (list.Count == 0) _pending.Remove(name);
        _suppressed++;
        return true;
    }

    public void Forget(string name)
    {
        _pending.Remove(name);
        _lastConsumed.Remove(name);
    }

    public int Pending(string name) => _pending.TryGetValue(name, out var list) ? list.Count(e => e.Deadline >= clock.NowMs) : 0;
}
