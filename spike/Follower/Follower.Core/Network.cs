namespace Crosscheck.Follower;

/// <summary>One stream frame as the PF's bridge would send it: the latest sim frame at send time.</summary>
public readonly record struct Packet(int Seq, double SendTime, Frame Frame);

/// <summary>A packet and when it reaches the follower.</summary>
public readonly record struct Arrival(Packet Packet, double ArrivalTime);

public static class PfSource
{
    /// <summary>
    /// Plays the recording as a PF bridge would: every 1/<paramref name="rateHz"/> s it sends the
    /// latest recorded frame at or before the send time. A frame is never sent twice (if the sim
    /// ran slower than the stream rate, that send slot is skipped).
    /// </summary>
    public static IReadOnlyList<Packet> Sends(IReadOnlyList<Frame> frames, double rateHz)
    {
        if (rateHz <= 0) throw new ArgumentOutOfRangeException(nameof(rateHz));
        var packets = new List<Packet>();
        if (frames.Count == 0) return packets;
        var t0 = frames[0].T;
        var end = frames[^1].T;
        var last = -1;
        var seq = 0;
        for (var k = 0; ; k++)
        {
            var s = t0 + k / rateHz;
            if (s > end + 1e-9) break;
            var i = Interpolator.IndexAtOrBefore(frames, s);
            if (i == last) continue;
            last = i;
            packets.Add(new Packet(seq++, s, frames[i]));
        }
        // The last frame always goes out, so the follower ends where the recording ends.
        if (last != frames.Count - 1) packets.Add(new Packet(seq, Math.Max(end, packets.Count > 0 ? packets[^1].SendTime : end), frames[^1]));
        return packets;
    }
}

public enum JitterShape
{
    /// <summary>Delay = latency + uniform(-jitter, +jitter).</summary>
    Uniform,
    /// <summary>Delay = latency + normal(0, jitter), never below zero.</summary>
    Normal,
}

public sealed record NetworkOptions
{
    public double LatencyMs { get; init; } = 35;
    public double JitterMs { get; init; } = 10;
    public JitterShape Shape { get; init; } = JitterShape.Uniform;
    /// <summary>Packets lost, percent.</summary>
    public double LossPct { get; init; }
    /// <summary>
    /// Chance per packet, percent, that the link stalls for <see cref="BurstMs"/> (a Wi-Fi hiccup).
    /// Everything sent during the stall arrives together when it ends.
    /// </summary>
    public double BurstPct { get; init; }
    public double BurstMs { get; init; } = 150;
    /// <summary>
    /// Let packets overtake each other (UDP-like). Off by default: the bridge streams over a
    /// WebSocket (TCP), which delivers in order, so a late packet holds up the ones behind it.
    /// </summary>
    public bool AllowReorder { get; init; }
    public int Seed { get; init; } = 1;
}

public sealed record NetworkSchedule(IReadOnlyList<Arrival> Arrivals, int Sent, int Lost, int Bursts, int Reordered);

public static class NetworkSimulator
{
    /// <summary>
    /// Arrival time of every packet, sorted by arrival. Same options and seed give the same
    /// schedule. With <see cref="NetworkOptions.AllowReorder"/>, packets can overtake each other
    /// when the jitter is larger than the send interval.
    /// </summary>
    public static NetworkSchedule Schedule(IReadOnlyList<Packet> packets, NetworkOptions o)
    {
        var rng = new Random(o.Seed);
        var arrivals = new List<Arrival>(packets.Count);
        int lost = 0, bursts = 0;
        var stallUntil = double.NegativeInfinity;
        var lastArrival = double.NegativeInfinity;
        foreach (var p in packets)
        {
            // Draw every random number for every packet, so changing one option does not reshuffle the others.
            var lossRoll = rng.NextDouble() * 100;
            var burstRoll = rng.NextDouble() * 100;
            var jitter = o.Shape == JitterShape.Uniform ? (rng.NextDouble() * 2 - 1) * o.JitterMs : Gaussian(rng) * o.JitterMs;
            if (burstRoll < o.BurstPct && p.SendTime >= stallUntil)
            {
                stallUntil = p.SendTime + o.BurstMs / 1000;
                bursts++;
            }
            if (lossRoll < o.LossPct)
            {
                lost++;
                continue;
            }
            var arrival = Math.Max(p.SendTime, stallUntil) + Math.Max(0, o.LatencyMs + jitter) / 1000;
            if (!o.AllowReorder)
            {
                arrival = Math.Max(arrival, lastArrival);
                lastArrival = arrival;
            }
            arrivals.Add(new Arrival(p, arrival));
        }
        arrivals.Sort((a, b) => a.ArrivalTime != b.ArrivalTime ? a.ArrivalTime.CompareTo(b.ArrivalTime) : a.Packet.Seq.CompareTo(b.Packet.Seq));
        var reordered = 0;
        var maxSeq = -1;
        foreach (var a in arrivals)
        {
            if (a.Packet.Seq < maxSeq) reordered++;
            else maxSeq = a.Packet.Seq;
        }
        return new NetworkSchedule(arrivals, packets.Count, lost, bursts, reordered);
    }

    private static double Gaussian(Random rng)
    {
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }
}
