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
    private readonly Dictionary<string, List<(double Value, double Deadline)>> _pending = new(StringComparer.Ordinal);
    private long _suppressed;

    public long Suppressed => _suppressed;

    public void Expect(string name, double value, double windowMs)
    {
        if (!_pending.TryGetValue(name, out var list)) _pending[name] = list = [];
        list.Add((value, clock.NowMs + windowMs));
    }

    /// <summary>true if this change is the read-back of our own write (do not send it).</summary>
    public bool TryConsume(string name, double value)
    {
        if (!_pending.TryGetValue(name, out var list)) return false;
        var now = clock.NowMs;
        list.RemoveAll(e => e.Deadline < now);
        var index = list.FindIndex(e => e.Value == value);
        if (index < 0)
        {
            _pending.Remove(name);
            return false;
        }
        list.RemoveRange(0, index + 1);
        if (list.Count == 0) _pending.Remove(name);
        _suppressed++;
        return true;
    }

    public void Forget(string name) => _pending.Remove(name);

    public int Pending(string name) => _pending.TryGetValue(name, out var list) ? list.Count(e => e.Deadline >= clock.NowMs) : 0;
}
