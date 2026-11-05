// The `throttle` command (flight-1 diagnosis item 7). Read only, same safety as `positions`: the pilot moves
// the thrust levers, the tool only reads and records. It gets only read-only sources (never the bridge sim
// adapter), and ObserveTests checks this file for write calls. Ctrl+C stops and saves what was recorded.

namespace ProfileProbe;

sealed class ThrottleSettings
{
    public required string ResultsPath { get; init; }
    public required string SamplesPath { get; init; }
    /// <summary>A timestamped copy of both files goes here too, so no run is ever overwritten.</summary>
    public string? RawDir { get; init; }
    public int ConnectSeconds { get; init; } = 60;
    public int NoiseMs { get; init; } = 10_000;
    public int HoldMs { get; init; } = 2_000;
    /// <summary>A sweep stops by itself after this long, if Enter never comes.</summary>
    public int SweepCapMs { get; init; } = 60_000;
    /// <summary>Sim vars the sim never sent (a rejected name): reported, so a silent 0 is never read as a value.</summary>
    public Func<IReadOnlyList<string>>? MissingSimVars { get; init; }
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
}

/// <summary>Keeps every frame while a step's window is open, and nothing otherwise.</summary>
sealed class ThrottleSampler
{
    readonly object _lock = new();
    ThrottleRecord? _open;
    long _frames;
    double _firstMs = double.NaN, _lastMs = double.NaN, _maxGapMs;

    public long Frames { get { lock (_lock) return _frames; } }
    public double LastMs { get { lock (_lock) return _lastMs; } }
    public double MaxGapMs { get { lock (_lock) return _maxGapMs; } }
    public double FramesPerSecond { get { lock (_lock) return _frames < 2 ? 0 : (_frames - 1) * 1000 / (_lastMs - _firstMs); } }

    public void OnFrame(ObserveFrame f)
    {
        lock (_lock)
        {
            if (!double.IsNaN(_lastMs)) _maxGapMs = Math.Max(_maxGapMs, f.TMs - _lastMs);
            if (double.IsNaN(_firstMs)) _firstMs = f.TMs;
            _lastMs = f.TMs;
            _frames++;
            _open?.Samples.Add(new ThrottleSample(f.TMs, f.Values));
        }
    }

    public void Open(ThrottleRecord r) { lock (_lock) _open = r; }
    public void Close() { lock (_lock) _open = null; }
    public int Count(ThrottleRecord r) { lock (_lock) return r.Samples.Count; }
}

sealed class ThrottlePhase(
    IObserveSource source,
    ISimvarReader simvars,
    TextWriter output,
    Func<CancellationToken, Task<string?>> readLine,
    ThrottleSettings settings)
{
    public IReadOnlyList<string> Vars { get; } = ThrottleVars.All;
    public ThrottleSampler Sampler { get; } = new();
    /// <summary>Only for the parked setup check (it reads the latest frame).</summary>
    public ObserveRecorder Recorder { get; } = new(ThrottleVars.All);
    public List<ThrottleRecord> Records { get; } = [];
    Task<string?>? _pending;

    /// <summary>The next line; a line still awaited from a capped sweep comes first.</summary>
    Task<string?> Next(CancellationToken ct)
    {
        var t = _pending ?? readLine(ct);
        _pending = null;
        return t;
    }

    public async Task<int> Run(CancellationToken ct)
    {
        output.WriteLine("""
            throttle (read only, flight-1 diagnosis item 7): you move the thrust levers, the tool records them.
            Nothing is written to the sim.

            Setup, please confirm:
              - MSFS 2024 with the Fenix A320 at a gate, engines off (ENG MASTER 1+2 OFF), parking brake set
              - BAT 1+2 and EXT PWR on (aircraft on external power)
              - not connected to VATSIM/IVAO, no GSX service running
              - use the thrust levers the way you fly (your hardware throttle, if you have one)
            9 steps, about 5 min. Ctrl+C at any time stops and saves what was recorded.
            Press Enter to connect.
            """);
        if (await Next(ct) is null) return 1;

        source.FrameReceived += Recorder.OnFrame;
        source.FrameReceived += Sampler.OnFrame;
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
            await Task.Delay(1000, ct);
            output.WriteLine($"connected; about {Sampler.FramesPerSecond:0} frames per second; title {simvars.Title ?? "?"}");
            if (Missing() is { Count: > 0 } missing) output.WriteLine($"warning: the sim sends no value for {string.Join(", ", missing)}; those columns stay 0");
            await Steps(ct);
        }
        finally
        {
            Sampler.Close();
            source.FrameReceived -= Sampler.OnFrame;
            source.FrameReceived -= Recorder.OnFrame;
            WriteResults();
        }
        return 0;
    }

    async Task Steps(CancellationToken ct)
    {
        var steps = ThrottleVars.Steps;
        output.WriteLine("\nAt each step: put the levers where it says, then press Enter. s + Enter = skip, " +
                         "r + Enter = redo the step before, q + Enter = stop and save.");
        for (var n = 0; n < steps.Length; n++)
        {
            var step = steps[n];
            output.WriteLine($"\n== [{n + 1}/{steps.Length}] {step.Say}");
            output.Write(step.Kind == ThrottleStepKind.Sweep ? "   Enter at MAX REV to start > " : "   Enter when ready > ");
            var answer = (await Next(ct))?.Trim().ToLowerInvariant();
            if (answer is null or "q") { output.WriteLine("stopping; saving what was recorded"); return; }
            if (answer == "r" && n > 0) { n -= 2; continue; }
            if (answer == "s") { output.WriteLine("   skipped"); continue; }
            ObservePhase.CheckParkedSetup(Recorder, simvars);
            if (!await Capture(step, ct)) { output.WriteLine("stopping; saving what was recorded"); return; }
        }
        output.Write("\nall steps done. Enter = save, r + Enter = redo the last step > ");
        var last = (await Next(ct))?.Trim().ToLowerInvariant();
        if (last == "r")
        {
            output.WriteLine($"\n== {steps[^1].Say}");
            output.Write("   Enter at MAX REV to start > ");
            if ((await Next(ct))?.Trim().ToLowerInvariant() is null or "q") return;
            await Capture(steps[^1], ct);
        }
    }

    /// <returns>false: the pilot stopped (q) during a sweep.</returns>
    async Task<bool> Capture(ThrottleStep step, CancellationToken ct)
    {
        foreach (var old in Records.Where(r => r.Step.Id == step.Id)) old.Replaced = true;
        var record = new ThrottleRecord { Step = step, Attempt = Records.Count(r => r.Step.Id == step.Id) + 1 };
        Records.Add(record);
        Sampler.Open(record);
        var stopped = false;
        try
        {
            switch (step.Kind)
            {
                case ThrottleStepKind.Sweep:
                    output.WriteLine($"   recording: move the {step.Lever!.Name} lever slowly to TOGA, then press Enter");
                    output.Write("   > ");
                    var line = Next(ct);
                    var done = await Task.WhenAny(line, Task.Delay(settings.SweepCapMs, ct));
                    ct.ThrowIfCancellationRequested();
                    if (done != line) { _pending = line; output.WriteLine($"\n   stopped after {settings.SweepCapMs / 1000} s"); }
                    else stopped = (await line)?.Trim().ToLowerInvariant() is null or "q";
                    break;
                default:
                    var ms = step.Kind == ThrottleStepKind.Noise ? settings.NoiseMs : settings.HoldMs;
                    output.WriteLine($"   recording {ms / 1000.0:0.#} s, hold still...");
                    await Task.Delay(ms, ct);
                    break;
            }
            record.Complete = !stopped;
        }
        finally
        {
            Sampler.Close();
        }
        ObservePhase.WarnIfNoData(Recorder, output);
        Summarize(record);
        return !stopped;
    }

    void Summarize(ThrottleRecord r)
    {
        if (r.Samples.Count == 0) { output.WriteLine("   no frames recorded: r + Enter at the next step to redo"); return; }
        foreach (var lever in ThrottleVars.Levers)
        {
            var s = ThrottleAnalysis.Stats(ThrottleAnalysis.Values(r, Vars, lever.Input));
            var g = ThrottleAnalysis.Stats(ThrottleAnalysis.Values(r, Vars, lever.Gate));
            output.WriteLine($"   {lever.Name,-5} input min {s.Min:0.####} max {s.Max:0.####} mean {s.Mean:0.####}   gate {g.Min:0.##}{(g.Spread > 0 ? $"…{g.Max:0.##}" : "")}   ({s.N} frames)");
        }
        if (r.Step.Kind != ThrottleStepKind.Hold) return;
        var noise = ThrottleAnalysis.Valid(Records).FirstOrDefault(x => x.Step.Kind == ThrottleStepKind.Noise);
        var band = ThrottleAnalysis.Band(noise is null ? [] : ThrottleVars.Levers.Select(l => ThrottleAnalysis.Stats(ThrottleAnalysis.Values(noise, Vars, l.Input))));
        var moved = ThrottleVars.Levers.Where(l => ThrottleAnalysis.Stats(ThrottleAnalysis.Values(r, Vars, l.Input)).Spread > 5 * band).Select(l => l.Name).ToList();
        if (moved.Count > 0) output.WriteLine($"   warning: the {string.Join(" and ", moved)} lever moved during the hold; r + Enter at the next step redoes this one");
    }

    IReadOnlyList<string> Missing() => settings.MissingSimVars?.Invoke() ?? [];

    void WriteResults()
    {
        if (Records.All(r => r.Samples.Count == 0))
        {
            output.WriteLine("nothing recorded; nothing written");
            return;
        }
        var info = new ThrottleAnalysis.RunInfo(settings.Now(), simvars.Title, Sampler.FramesPerSecond, Sampler.MaxGapMs, Missing());
        var report = ThrottleAnalysis.Report(Records, Vars, info);
        var csv = ThrottleAnalysis.Csv(Records, Vars);
        foreach (var p in new[] { settings.ResultsPath, settings.SamplesPath })
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(settings.ResultsPath, report);
        File.WriteAllText(settings.SamplesPath, csv);
        if (settings.RawDir is { } raw)
        {
            Directory.CreateDirectory(raw);
            var stamp = info.At.ToString("yyyyMMdd-HHmmss");
            File.WriteAllText(Path.Combine(raw, $"throttle-results-{stamp}.md"), report);
            File.WriteAllText(Path.Combine(raw, $"throttle-samples-{stamp}.csv"), csv);
        }
        output.WriteLine($"\nwritten: {settings.ResultsPath}");
        output.WriteLine($"written: {settings.SamplesPath}");
    }
}
