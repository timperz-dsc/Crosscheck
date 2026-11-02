// The `positions` command (CRO-24 prep for gate test 2). Read only, same safety as `observe`:
// the pilot moves each control through its positions, and this records every distinct value
// and classifies it (positions, push button, analog range). It never writes to the sim: it gets
// only read-only sources (never the bridge sim adapter), and ObserveTests checks this file.

namespace ProfileProbe;

sealed class PositionsSettings
{
    /// <summary>One row per control and var: the classification.</summary>
    public required string ResultsPath { get; init; }
    /// <summary>Every change, frame by frame (same columns as observe-results.csv).</summary>
    public required string ChangesPath { get; init; }
    public required string SummaryPath { get; init; }
    /// <summary>A timestamped copy of every run's files goes here too, so no run is ever overwritten.</summary>
    public string? RawDir { get; init; }
    public string? Only { get; init; }
    public int SettleMs { get; init; } = 800;
    public int ConnectSeconds { get; init; } = 60;
}

sealed class PositionsPhase(
    IObserveSource source,
    ISimvarReader simvars,
    TextWriter output,
    Func<CancellationToken, Task<string?>> readLine,
    PositionsSettings settings)
{
    public ObserveRecorder Recorder { get; } = new(PositionsVars.All);

    public static IReadOnlyList<PositionControl> Selected(string? only) =>
        only is null ? PositionsVars.Controls
            : PositionsVars.Controls.Where(c => string.Equals(c.Id, only, StringComparison.OrdinalIgnoreCase) || c.Var == only).ToList();

    public async Task<int> Run(CancellationToken ct)
    {
        var controls = Selected(settings.Only);
        if (controls.Count == 0)
        {
            output.WriteLine($"no control matches --only {settings.Only}; ids: {string.Join(" ", PositionsVars.Controls.Select(c => c.Id))}");
            return 1;
        }
        output.WriteLine($"""
            positions (read only, CRO-24): you move each control, the tool records every value. Nothing is written to the sim.

            Setup, please confirm:
              - MSFS 2024 with the Fenix A320 at a gate, engines off, parking brake set
              - BAT 1+2 and EXT PWR on (aircraft on external power)
              - not connected to VATSIM/IVAO, no GSX service running
            {controls.Count} controls, about {PositionsVars.TotalSeconds(controls) / 60} min.
            Press Enter to connect (Ctrl+C to quit).
            """);
        if (await readLine(ct) is null) return 1;

        source.FrameReceived += Recorder.OnFrame;
        try
        {
            await source.StartAsync(ct);
            await simvars.StartAsync(ct);
            output.WriteLine("connecting...");
            var until = DateTime.UtcNow.AddSeconds(settings.ConnectSeconds);
            while (Recorder.Frames == 0 || simvars.Latest is null)
            {
                if (DateTime.UtcNow > until) throw new SafetyStopException($"no data from the sim after {settings.ConnectSeconds} s");
                await Task.Delay(100, ct);
            }
            ObservePhase.CheckParkedSetup(Recorder, simvars);
            var f0 = Recorder.Frames;
            await Task.Delay(1000, ct);
            output.WriteLine($"connected; {Recorder.Frames - f0} frames per second; title {simvars.Title ?? "?"}");
            await Steps(controls, ct);
        }
        finally
        {
            Recorder.EndSteps(ObserveClock.NowMs);
            source.FrameReceived -= Recorder.OnFrame;
            WriteResults(controls);
        }
        return 0;
    }

    async Task Steps(IReadOnlyList<PositionControl> controls, CancellationToken ct)
    {
        output.WriteLine($"\n{controls.Count} steps, most important first. After each step press Enter. " +
                         "s + Enter = skip, r + Enter = redo, q + Enter = stop and save.");
        var started = ObserveClock.NowMs;
        for (var n = 0; n < controls.Count; n++)
        {
            var c = controls[n];
            var left = PositionsVars.TotalSeconds(controls.Skip(n));
            var elapsed = (ObserveClock.NowMs - started) / 1000;
            output.WriteLine($"\n== [{n + 1}/{controls.Count}] {c.Label}   ({elapsed / 60:0}:{elapsed % 60:00} elapsed, about {left / 60}:{left % 60:00} left)");
            if (c.Hint is not null) output.WriteLine($"   {c.Hint}");
            for (var attempt = 1; ; attempt++)
            {
                var name = $"{c.Id}{(attempt > 1 ? $"-{attempt}" : "")}";
                Recorder.BeginStep(name, c.AsObserve(), null, ObserveClock.NowMs);
                output.Write($"{PositionsVars.Instruction(c)} > ");
                var answer = (await readLine(ct))?.Trim().ToLowerInvariant();
                if (answer is null or "q") { output.WriteLine("stopping; saving what was recorded"); return; }
                await Task.Delay(settings.SettleMs, ct); // let lamps catch up
                ObservePhase.WarnIfNoData(Recorder, output);
                if (answer is "s" or "r") Recorder.SkipCurrent();
                Recorder.BeginStep($"{name}/after", null, null, ObserveClock.NowMs);
                foreach (var l in PositionsAnalysis.Summarize(Recorder.Steps[^2], c, Recorder).Skip(1)) output.WriteLine(l);
                if (answer != "r") break;
            }
        }
        Recorder.BeginStep("end", null, null, ObserveClock.NowMs);
        output.WriteLine("\nall steps done");
    }

    void WriteResults(IReadOnlyList<PositionControl> controls)
    {
        if (!Recorder.Steps.Any(st => st.Control is not null))
        {
            output.WriteLine("no step was started; nothing written");
            return;
        }
        foreach (var p in new[] { settings.ResultsPath, settings.ChangesPath, settings.SummaryPath })
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        var results = PositionsAnalysis.ResultsCsv(Recorder, controls);
        var changes = Recorder.Csv();
        File.WriteAllText(settings.ResultsPath, results);
        File.WriteAllText(settings.ChangesPath, changes);
        if (settings.RawDir is { } raw)
        {
            Directory.CreateDirectory(raw);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.WriteAllText(Path.Combine(raw, $"positions-{stamp}.csv"), results);
            File.WriteAllText(Path.Combine(raw, $"positions-changes-{stamp}.csv"), changes);
        }
        var report = PositionsAnalysis.Report(Recorder, controls);
        File.WriteAllText(settings.SummaryPath, $"positions run {DateTime.Now:yyyy-MM-dd HH:mm:ss}, title {simvars.Title ?? "?"}\n{report}");
        output.WriteLine($"\n{report}");
        output.WriteLine($"written: {settings.ResultsPath}");
        output.WriteLine($"written: {settings.ChangesPath}");
        output.WriteLine($"written: {settings.SummaryPath}");
    }
}
