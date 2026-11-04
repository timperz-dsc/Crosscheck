// CRO-17 follower spike, offline tools. No sim needed; runs on Linux and Windows.
//   follower synth <out.cxrec>                     synthetic full circuit
//   follower simulate <rec.cxrec> [options]        full pipeline against a perfect simulated sim
//   follower inspect <rec.cxrec> [--csv <out>]     summary, min/max per field, optional CSV export
//   follower compare <run dir>...                  one table across runs (offline or sim)

using System.Globalization;
using Crosscheck.Follower;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

try
{
    if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
    {
        Usage();
        return 0;
    }
    var rest = args.Skip(1);
    switch (args[0])
    {
        case "synth":
        {
            var a = new Args(rest, []);
            var fps = a.Num("fps", 60, 10, 240);
            a.EnsureAllUsed();
            var path = a.Positionals.SingleOrDefault() ?? throw new UsageException("synth <out.cxrec>");
            var rec = Synthetic.Circuit(fps);
            RecordingFormat.Save(path, rec.Header, rec.Frames);
            Console.WriteLine($"wrote {path}: {rec.Frames.Count} frames, {rec.Duration:0} s");
            return 0;
        }
        case "simulate":
        {
            var a = new Args(rest, PlaybackArgs.Flags);
            var settings = PlaybackArgs.Parse(a);
            var simFps = a.Num("sim-fps", 60, 10, 240);
            var outRoot = a.Str("out") ?? "runs";
            a.EnsureAllUsed();
            if (settings.HandoverTestS is not null) throw new UsageException("--handover-test needs the sim (follower-sim play)");
            var path = a.Positionals.SingleOrDefault() ?? throw new UsageException("simulate <rec.cxrec> [options]");
            var rec = RecordingFormat.Load(path);
            var started = DateTime.UtcNow;
            var session = OfflineRun.Run(rec, settings, simFps);
            var notes = new List<string> { $"offline: perfect simulated sim at {simFps:0} fps; methods differ only in how the read-back is compared" };
            var info = Report.Build(session, "offline", Path.GetFileName(path), started, "normal end", endedInAir: !session.Last.State.OnGround, notes: notes);
            var dir = Report.Write(outRoot, info, session.Metrics.Rows);
            Console.WriteLine(Report.Markdown(info));
            Console.WriteLine($"run folder: {dir}");
            return 0;
        }
        case "inspect":
        {
            var a = new Args(rest, []);
            var csv = a.Str("csv");
            a.EnsureAllUsed();
            var path = a.Positionals.SingleOrDefault() ?? throw new UsageException("inspect <rec.cxrec> [--csv out.csv]");
            Inspect(RecordingFormat.Load(path), path, csv);
            return 0;
        }
        case "compare":
        {
            var a = new Args(rest, []);
            a.EnsureAllUsed();
            if (a.Positionals.Count == 0) throw new UsageException("compare <run dir>...");
            Console.WriteLine(Report.Compare(a.Positionals.Select(Report.Load)));
            return 0;
        }
        default:
            throw new UsageException($"unknown command {args[0]}");
    }
}
catch (UsageException e)
{
    Console.Error.WriteLine($"error: {e.Message}");
    Console.Error.WriteLine("run `follower help` for usage");
    return 2;
}
catch (Exception e) when (e is RecordingFormatException or IOException or ArgumentException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static void Inspect(Recording rec, string path, string? csv)
{
    var raw = rec.Frames;
    Console.WriteLine($"{path}: {raw.Count} frames, {rec.Duration:0.0} s, aircraft \"{rec.Header.AircraftTitle}\", started {rec.Header.StartUtc:yyyy-MM-dd HH:mm:ss} UTC");
    if (rec.Header.Note != "") Console.WriteLine($"note: {rec.Header.Note}");
    if (rec.Truncated) Console.WriteLine("the last record was cut off and dropped");
    if (raw.Count < 2) return;

    // Which clock advances smoothly with the position? (research section 6.8)
    Console.WriteLine();
    Console.WriteLine("clocks (speed err: |position change / clock step - recorded velocity| / speed, frame to frame, while moving; lower is smoother)");
    Console.WriteLine($"{"clock",-10} {"usable",6} {"step ms p50",11} {"p99",7} {"< 3 ms",7} {"repeats",8} {"not moving",10} {"speed err p50",13} {"p95",7}");
    foreach (var c in TimeBase.Candidates)
    {
        var q = TimeBase.Quality(raw, c);
        if (!q.Available)
        {
            Console.WriteLine($"{c,-10} {"no",6}   (not in this recording, or never moves)");
            continue;
        }
        Console.WriteLine($"{c,-10} {(q.Usable ? "yes" : "no"),6} {q.StepMedianMs,11:0.0} {q.StepP99Ms,7:0.0} {q.Tiny,7} {q.Duplicates,8} {q.NotAdvancing,10} {q.SpeedErrP50 * 100,12:0.0}% {q.SpeedErrP95 * 100,6:0.0}%");
    }
    var (source, why) = TimeBase.Choose(raw);
    var (f, info) = TimeBase.Apply(raw, source, why);
    Console.WriteLine($"--clock auto uses: {source} ({why}); it drops {info.DroppedDuplicates} repeated frames and {info.DroppedNotAdvancing} frames whose clock did not move");
    Console.WriteLine();

    var dts = f.Zip(f.Skip(1), (x, y) => (y.T - x.T) * 1000).OrderBy(x => x).ToArray();
    Console.WriteLine($"frame time ms ({source}): median {dts[dts.Length / 2]:0.0}, p95 {dts[(int)(dts.Length * 0.95)]:0.0}, max {dts[^1]:0.0}");
    var mismatch = Interpolator.VelocityMismatch(f);
    Console.WriteLine($"world velocity vs position change over {Interpolator.MismatchWindowS:0.0} s: {mismatch * 100:0.0} % median mismatch" + (mismatch > 0.05 ? "  <-- high: the velocity axes may not be X east, Y up, Z north. Tell us." : " (axes as expected)"));
    foreach (var ph in Enum.GetValues<Phase>())
    {
        var n = f.Count(x => MetricsCollector.PhaseOf(x) == ph);
        if (n > 0) Console.WriteLine($"phase {ph}: {n} frames");
    }
    Console.WriteLine(EndCheck.Problem(f, allowAirEnd: false) is { } p ? $"end: {p}" : "end: on the ground and stopped");
    var taxi = HandoverTest.Candidates(f);
    if (taxi.Count == 0) Console.WriteLine("handover test: no steady taxi stretch (8-35 kt for 10 s or more)");
    foreach (var (from, to, kt) in taxi)
        Console.WriteLine($"handover test: steady taxi {from:0} - {to:0} s at about {kt:0} kt, e.g. --handover-test {(from + to) / 2:0}");
    if (!rec.Has("g_force")) Console.WriteLine("no g_force in this recording (recorded before 2026-11-04): G FORCE will not be written");
    Console.WriteLine();
    Console.WriteLine($"{"field",-22} {"min",14} {"max",14}  sim var [unit]");
    foreach (var s in Fields.All)
    {
        if (!rec.Has(s.Name)) continue;
        var vals = raw.Select(s.Get).ToArray();
        Console.WriteLine($"{s.Name,-22} {vals.Min(),14:0.######} {vals.Max(),14:0.######}  {s.SimVar} [{s.Unit}]");
    }
    if (csv is not null)
    {
        using var w = new StreamWriter(csv);
        RecordingFormat.WriteCsv(w, raw);
        Console.WriteLine($"wrote {csv} (raw frames, t = stopwatch)");
    }
}

static void Usage()
{
    Console.WriteLine("""
        follower: offline tools for the CRO-17 follower spike (docs/spikes/CRO-17-follower.md)

          follower synth <out.cxrec> [--fps 60]       write the synthetic full circuit
          follower simulate <rec.cxrec> [options]     play it through the simulated network into a perfect sim
              --sim-fps <n>               simulated sim frame rate (default 60)
        """ + PlaybackArgs.Help + """

          follower inspect <rec.cxrec> [--csv out]    summary and min/max of every field
          follower compare <run dir>...               one table across runs
        """);
}
