using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crosscheck.Follower;

public sealed record RunCounters
{
    public int Sent { get; init; }
    public int Lost { get; init; }
    public int Bursts { get; init; }
    public int Reordered { get; init; }
    public int Received { get; init; }
    public int DroppedOld { get; init; }
    /// <summary>Freezes (or slew) sent again because the sim reported them off.</summary>
    public int Reasserts { get; init; }
}

/// <summary>Everything about one run, as written to summary.json.</summary>
public sealed record RunInfo
{
    /// <summary>"offline" (perfect simulated sim) or "sim" (MSFS).</summary>
    public string Source { get; init; } = "offline";
    public DateTime StartedUtc { get; init; }
    public string Recording { get; init; } = "";
    public string Aircraft { get; init; } = "";
    public PlaybackSettings Settings { get; init; } = new();
    public RunCounters Counters { get; init; } = new();
    public MetricsSummary Summary { get; init; } = new();
    /// <summary>How the run ended, e.g. "normal end, stopped on the ground" or "stop key in the air".</summary>
    public string Ended { get; init; } = "";
    public bool EndedInAir { get; init; }
    public double VelocityMismatch { get; init; } = double.NaN;
    public List<string> Notes { get; init; } = [];
}

public static class Report
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RunInfo Build(PlaybackSession s, string source, string recordingName, DateTime startedUtc, string ended, bool endedInAir, int reasserts = 0, IEnumerable<string>? notes = null)
    {
        var info = new RunInfo
        {
            Source = source,
            StartedUtc = startedUtc,
            Recording = recordingName,
            Aircraft = s.Recording.Header.AircraftTitle,
            Settings = s.Settings,
            Counters = new RunCounters
            {
                Sent = s.Schedule.Sent,
                Lost = s.Schedule.Lost,
                Bursts = s.Schedule.Bursts,
                Reordered = s.Schedule.Reordered,
                Received = s.Buffer.Received,
                DroppedOld = s.Buffer.DroppedOld,
                Reasserts = reasserts,
            },
            Summary = s.Metrics.Summarize(),
            Ended = ended,
            EndedInAir = endedInAir,
            VelocityMismatch = Interpolator.VelocityMismatch(s.Recording.Frames),
            Notes = notes?.ToList() ?? [],
        };
        return info;
    }

    public static string RunDirName(RunInfo r) =>
        $"{r.StartedUtc:yyyyMMdd-HHmmss}-{r.Source}-{r.Settings.Method}-{r.Settings.RateHz:0}Hz-{r.Settings.Network.LatencyMs:0}ms";

    /// <summary>Writes report.md, summary.json and frames.csv into a new folder under <paramref name="outRoot"/>; returns the folder.</summary>
    public static string Write(string outRoot, RunInfo info, IReadOnlyList<FrameRow> rows)
    {
        var dir = Path.Combine(outRoot, RunDirName(info));
        var unique = dir;
        for (var i = 2; Directory.Exists(unique); i++) unique = $"{dir}-{i}";
        Directory.CreateDirectory(unique);
        File.WriteAllText(Path.Combine(unique, "summary.json"), JsonSerializer.Serialize(info, Json));
        File.WriteAllText(Path.Combine(unique, "report.md"), Markdown(info));
        using (var w = new StreamWriter(Path.Combine(unique, "frames.csv")))
            WriteCsv(w, rows);
        return unique;
    }

    public static RunInfo Load(string dir) =>
        JsonSerializer.Deserialize<RunInfo>(File.ReadAllText(Path.Combine(dir, "summary.json")), Json)
        ?? throw new InvalidDataException($"{dir}: empty summary.json");

    private static string F(double v, string fmt = "0.00") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.InvariantCulture) : "-";

    public static string Markdown(RunInfo r)
    {
        var s = r.Settings;
        var sb = new StringBuilder();
        sb.AppendLine($"# Follower run: method {s.Method}, {s.RateHz:0} Hz, {s.Network.LatencyMs:0} ± {s.Network.JitterMs:0} ms ({r.Source})");
        sb.AppendLine();
        sb.AppendLine($"- Recording: `{r.Recording}` ({r.Aircraft}), started {r.StartedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- Ended: {r.Ended}{(r.EndedInAir ? " **(in the air)**" : "")}");
        sb.AppendLine($"- Network: latency {s.Network.LatencyMs} ms, jitter {s.Network.JitterMs} ms {s.Network.Shape.ToString().ToLowerInvariant()}, loss {s.Network.LossPct} %, stalls {s.Network.BurstPct} % × {s.Network.BurstMs} ms, {(s.Network.AllowReorder ? "reordering allowed" : "in order")}, seed {s.Network.Seed}");
        sb.AppendLine($"- Playout: buffer {s.Playout.BufferMs} ms, extrapolate ≤ {s.Playout.MaxExtrapolationMs} ms, blend {s.Playout.BlendMs} ms, tangents {s.Playout.Tangents}, velocities written: {s.Velocities}");
        sb.AppendLine($"- Packets: {r.Counters.Sent} sent, {r.Counters.Lost} lost, {r.Counters.Bursts} stalls, {r.Counters.Reordered} out of order, {r.Counters.DroppedOld} dropped as older than the newest; re-freezes: {r.Counters.Reasserts}");
        sb.AppendLine($"- Late: {r.Summary.LateStretches} stretches, longest {F(r.Summary.LongestLateMs, "0")} ms");
        sb.AppendLine($"- Recorded velocity vs position change: {F(r.VelocityMismatch * 100, "0.0")} % median mismatch{(r.VelocityMismatch > 0.05 ? " **(high: check the velocity axes, or use --tangents fd)**" : "")}");
        if (s.Method == Method.A)
            sb.AppendLine("- Method A leaves the aircraft live: the Fenix fly-by-wire may react to the placed attitude. The reaction columns separate that from the placement.");
        foreach (var n in r.Notes) sb.AppendLine($"- {n}");
        sb.AppendLine();
        sb.AppendLine("Errors are the follower as read back from the sim against the recording at the time it should show. ");
        sb.AppendLine("Along-track error is mostly timing; cross-track and vertical are what you see out of the window. ");
        sb.AppendLine("Jitter is |follower acceleration − recording acceleration| per frame; a spike is above " +
                      $"{F(s.Metrics.SpikeAccel, "0.#")} m/s² or {F(s.Metrics.SpikeAngAccel, "0")} °/s². " +
                      "Commanded error is read-back against what was written (how well the sim obeyed).");
        sb.AppendLine();
        sb.AppendLine("| Phase | s | pos p95/max m | cross p95 m | vert p95 m | att p95/max ° | att p95 w/o reaction ° | cmd pos p95 m | jitter p95/p99/max m/s² | ang jitter p95/max °/s² | spikes (/min) | extrap / hold frames | reaction / stick touched frames | lag ms | frame ms p95/max |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var p in r.Summary.Phases.Append(r.Summary.All))
        {
            sb.AppendLine($"| {p.Phase} | {F(p.Seconds, "0")} | {F(p.PosErrM.P95)} / {F(p.PosErrM.Max)} | {F(p.CrossErrM.P95)} | {F(p.VertErrM.P95)} | " +
                          $"{F(p.AttErrDeg.P95)} / {F(p.AttErrDeg.Max)} | {F(p.AttErrNoReactionDeg.P95)} | {F(p.CmdPosErrM.P95)} | " +
                          $"{F(p.AccelResid.P95)} / {F(p.AccelResid.P99)} / {F(p.AccelResid.Max)} | {F(p.AngAccelResid.P95, "0.0")} / {F(p.AngAccelResid.Max, "0.0")} | " +
                          $"{p.Spikes} ({F(p.SpikesPerMin, "0.0")}) | {p.Extrapolated} / {p.Holding} | {p.ReactionFrames} / {p.StickTouchedFrames} | {F(p.LagMeanMs, "0")} | {F(p.FrameMs.P95, "0.0")} / {F(p.FrameMs.Max, "0.0")} |");
        }
        return sb.ToString();
    }

    public static void WriteCsv(TextWriter w, IEnumerable<FrameRow> rows)
    {
        w.WriteLine("now,frame_ms,truth_t,phase,status,late_ms,lag_ms,pos_err_m,along_err_m,cross_err_m,vert_err_m,att_err_deg,cmd_pos_err_m,cmd_att_err_deg,accel_resid,ang_accel_resid,spike,stick_neutral,reaction,elev_diff_deg,ail_diff_deg,rud_diff_deg");
        var c = CultureInfo.InvariantCulture;
        foreach (var r in rows)
            w.WriteLine(string.Join(',',
                r.Now.ToString("0.0000", c), r.FrameMs.ToString("0.00", c), r.TruthT.ToString("0.0000", c), r.Phase, r.Status,
                r.LateMs.ToString("0.0", c), r.LagMs.ToString("0.0", c), r.PosErrM.ToString("0.000", c), r.AlongErrM.ToString("0.000", c),
                r.CrossErrM.ToString("0.000", c), r.VertErrM.ToString("0.000", c), r.AttErrDeg.ToString("0.000", c),
                r.CmdPosErrM.ToString("0.000", c), r.CmdAttErrDeg.ToString("0.000", c), r.AccelResid.ToString("0.000", c),
                r.AngAccelResid.ToString("0.00", c), r.Spike ? 1 : 0, r.StickNeutral ? 1 : 0, r.Reaction ? 1 : 0,
                r.ElevDiffDeg.ToString("0.00", c), r.AilDiffDeg.ToString("0.00", c), r.RudDiffDeg.ToString("0.00", c)));
    }

    /// <summary>Short label with every setting that differs from the defaults.</summary>
    public static string RunLabel(RunInfo r)
    {
        var s = r.Settings;
        var parts = new List<string> { r.Source, s.Method.ToString(), $"{s.RateHz:0} Hz", $"{s.Network.LatencyMs:0}±{s.Network.JitterMs:0} ms", $"buf {s.Playout.BufferMs:0}" };
        if (s.Network.BurstPct > 0) parts.Add($"stalls {s.Network.BurstPct}%×{s.Network.BurstMs:0} ms");
        if (s.Network.LossPct > 0) parts.Add($"loss {s.Network.LossPct}%");
        if (s.Network.AllowReorder) parts.Add("reorder");
        if (s.Playout.MaxExtrapolationMs != 250) parts.Add($"extrap {s.Playout.MaxExtrapolationMs:0}");
        if (s.Playout.BlendMs != 200) parts.Add($"blend {s.Playout.BlendMs:0}");
        if (s.Playout.Tangents != Tangents.Velocity) parts.Add("fd");
        if (s.Velocities != VelocityMode.Body) parts.Add($"vel {s.Velocities.ToString().ToLowerInvariant()}");
        return string.Join(' ', parts);
    }

    /// <summary>One table, one row per run and phase, for comparing methods and settings.</summary>
    public static string Compare(IEnumerable<RunInfo> runs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Run | Phase | pos p95 m | cross p95 m | vert p95 m | att p95 ° | att p95 w/o reaction ° | jitter p99 m/s² | spikes/min | extrap / hold | reaction frames | ended |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in runs)
        {
            var name = RunLabel(r);
            foreach (var p in r.Summary.Phases.Append(r.Summary.All))
                sb.AppendLine($"| {name} | {p.Phase} | {F(p.PosErrM.P95)} | {F(p.CrossErrM.P95)} | {F(p.VertErrM.P95)} | {F(p.AttErrDeg.P95)} | {F(p.AttErrNoReactionDeg.P95)} | " +
                              $"{F(p.AccelResid.P99)} | {F(p.SpikesPerMin, "0.0")} | {p.Extrapolated} / {p.Holding} | {p.ReactionFrames} | {(r.EndedInAir ? "in the air" : "ground")} |");
        }
        return sb.ToString();
    }
}
