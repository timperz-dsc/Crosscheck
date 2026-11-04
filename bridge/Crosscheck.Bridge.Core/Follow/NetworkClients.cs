using System.Diagnostics;

namespace Crosscheck.Bridge.Follow;

/// <summary>
/// Online-network pilot clients (VATSIM, IVAO, PilotEdge). Only the PF may be connected to a network: the
/// follower's aircraft is placed by Crosscheck and must not be seen as a second aircraft (CRO-13 preflight).
/// Process names from the CRO-17 spike.
/// </summary>
public static class NetworkClients
{
    public static readonly IReadOnlyList<string> ProcessNames =
        ["vPilot", "xPilot", "swiftguistd", "swiftcore", "swiftdata", "Altitude", "IvAp", "IVAO_Pilot_Core", "PilotEdge", "PECLIENT"];

    private static readonly object Gate = new();
    private static (DateTime At, IReadOnlyList<string> Found) _cache = (DateTime.MinValue, []);

    public static IReadOnlyList<string> Find(IEnumerable<string> runningProcessNames) =>
        runningProcessNames.Where(p => ProcessNames.Any(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Network clients running on this computer now (looked up at most every 5 s).</summary>
    public static IReadOnlyList<string> Running()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _cache.At < TimeSpan.FromSeconds(5)) return _cache.Found;
            IReadOnlyList<string> found;
            try
            {
                var names = Process.GetProcesses().Select(p =>
                {
                    using (p) return p.ProcessName;
                }).ToList();
                found = Find(names);
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                found = [];
            }
            _cache = (DateTime.UtcNow, found);
            return found;
        }
    }
}
