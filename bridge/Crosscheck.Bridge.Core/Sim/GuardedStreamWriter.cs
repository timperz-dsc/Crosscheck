namespace Crosscheck.Bridge.Sim;

/// <summary>
/// Applies stream frames (position, attitude, ...) from the PF to this sim. Only active on the
/// non-PF side: while this side is PF every frame is refused, so the flying pilot's aircraft
/// can never be moved. Phase 1 placeholder: frames are counted, nothing is written to the sim
/// until the follower spike (CRO-17) defines how.
/// </summary>
public sealed class GuardedStreamWriter
{
    private volatile bool _isPf = true; // safe default: refuse until a role is known
    private long _applied;
    private long _refused;

    public bool IsPf => _isPf;
    public long Applied => Interlocked.Read(ref _applied);
    public long Refused => Interlocked.Read(ref _refused);

    public void SetRole(bool isPf) => _isPf = isPf;

    /// <summary>Returns false (and does nothing) while this side is PF.</summary>
    public bool Apply(IReadOnlyDictionary<string, double> frame)
    {
        if (_isPf)
        {
            Interlocked.Increment(ref _refused);
            return false;
        }
        Interlocked.Increment(ref _applied);
        return true;
    }
}
