using System.Text.Json.Nodes;

namespace Crosscheck.Bridge.Sim;

/// <summary>
/// The stream's role gate between the sync engine and the physics side (Follow.PhysicsSync, CRO-13). Frames
/// are passed on only while this side is not PF: while PF every frame is refused, so the flying pilot's
/// aircraft can never be moved. <see cref="RoleChanged"/> is raised in the same call as <see cref="SetRole"/>,
/// so a bridge that becomes PF releases its freezes at once.
/// </summary>
public sealed class GuardedStreamWriter
{
    private volatile bool _isPf = true; // safe default: refuse until a role is known
    private long _applied;
    private long _refused;

    public bool IsPf => _isPf;
    public long Applied => Interlocked.Read(ref _applied);
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>The role from the server (true = PF). Raised on every SetRole, also when unchanged (resume).</summary>
    public event Action<bool>? RoleChanged;

    /// <summary>A frame from the PF, passed on while this side is not PF: (frame number, data).</summary>
    public event Action<long, JsonObject>? FrameReceived;

    public void SetRole(bool isPf)
    {
        _isPf = isPf;
        RoleChanged?.Invoke(isPf);
    }

    /// <summary>Returns false (and does nothing) while this side is PF.</summary>
    public bool Apply(long frame, JsonObject data)
    {
        if (_isPf)
        {
            Interlocked.Increment(ref _refused);
            return false;
        }
        Interlocked.Increment(ref _applied);
        FrameReceived?.Invoke(frame, data);
        return true;
    }
}
