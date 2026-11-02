// CRO-29 part 1: the `observe` command. Read only. The pilot clicks push buttons in the
// cockpit step by step, and this records how their L: vars and lamps change, frame by frame.
// It never writes to the sim: it gets only read-only sources (never the bridge sim adapter), and
// ObserveTests checks these files for write calls.

using Crosscheck.Bridge.Fenix;

namespace ProfileProbe;

sealed class ObserveSettings
{
    public required string CsvPath { get; init; }
    public required string SummaryPath { get; init; }
    /// <summary>A timestamped copy of every run's CSV goes here too, so no run is ever overwritten.</summary>
    public string? RawDir { get; init; }
    public string? Only { get; init; }
    public int SettleMs { get; init; } = 1000;
    public int ConnectSeconds { get; init; } = 60;
}

sealed class ObservePhase(
    IObserveSource source,
    ISimvarReader simvars,
    IFenixGraphQl? gql,
    TextWriter output,
    Func<CancellationToken, Task<string?>> readLine,
    ObserveSettings settings)
{
    public const string McduScreenVar = "MCDU1_SCREEN";

    public ObserveRecorder Recorder { get; } = new(ObserveVars.All);

    public static IReadOnlyList<ObserveControl> Selected(string? only) =>
        only is null ? ObserveVars.Controls : ObserveVars.Controls.Where(c => string.Equals(c.Id, only, StringComparison.OrdinalIgnoreCase) || c.Button == only).ToList();

    public async Task<int> Run(CancellationToken ct)
    {
        var controls = Selected(settings.Only);
        if (controls.Count == 0)
        {
            output.WriteLine($"no control matches --only {settings.Only}; ids: {string.Join(" ", ObserveVars.Controls.Select(c => c.Id))}");
            return 1;
        }
        output.WriteLine("""
            observe (read only, CRO-29): you click, the tool records. Nothing is written to the sim.

            Setup, please confirm:
              - MSFS 2024 with the Fenix A320 at a gate, engines off, parking brake set
              - BAT 1+2 and EXT PWR on (aircraft on external power)
              - not connected to VATSIM/IVAO, no GSX service running
            Press Enter to connect (Ctrl+C to quit).
            """);
        if (await readLine(ct) is null) return 1;

        void OnMcdu(int cdu)
        {
            if (cdu == 1 && gql?.Mcdu(1) is { } s)
                Recorder.OnText(ObserveClock.NowMs, McduScreenVar, $"{s.Rows[0].Trim()} | {s.Rows[^1].Trim()}");
        }
        source.FrameReceived += Recorder.OnFrame;
        if (gql is not null) gql.McduChanged += OnMcdu;
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
            CheckSetup();
            var f0 = Recorder.Frames;
            await Task.Delay(1000, ct);
            output.WriteLine($"connected; {Recorder.Frames - f0} frames per second; title {simvars.Title ?? "?"}");
            if (gql is not null)
            {
                await gql.StartAsync([], ct);
                var version = await gql.SoftwareVersionAsync(ct);
                output.WriteLine(version is null ? "warning: Fenix GraphQL does not answer; MCDU screen changes are not recorded" : $"Fenix {version}");
            }

            await Steps(controls, ct);
        }
        finally
        {
            Recorder.EndSteps(ObserveClock.NowMs);
            source.FrameReceived -= Recorder.OnFrame;
            if (gql is not null) gql.McduChanged -= OnMcdu;
            WriteResults();
        }
        return 0;
    }

    async Task Steps(IReadOnlyList<ObserveControl> controls, CancellationToken ct)
    {
        var total = controls.Count * 3;
        var n = 0;
        output.WriteLine($"\n{total} steps. After each step press Enter. s + Enter = skip, r + Enter = redo, q + Enter = stop and save.");
        foreach (var c in controls)
        {
            output.WriteLine($"\n== {c.Label} ({c.Button})");
            if (c.Hint is not null) output.WriteLine($"   {c.Hint}");
            foreach (var kind in Enum.GetValues<StepKind>())
            {
                n++;
                for (var attempt = 1; ; attempt++)
                {
                    var name = $"{c.Id}/{kind.ToString().ToLowerInvariant()}{(attempt > 1 ? $"-{attempt}" : "")}";
                    Recorder.BeginStep(name, c, kind, ObserveClock.NowMs);
                    output.Write($"[{n}/{total}] {ObserveVars.Instruction(c, kind)} > ");
                    var answer = (await readLine(ct))?.Trim().ToLowerInvariant();
                    if (answer is null or "q") { output.WriteLine("stopping; saving what was recorded"); return; }
                    await Task.Delay(settings.SettleMs, ct); // let lamps and screens catch up
                    LinkCheck();
                    var step = Recorder.Steps[^1];
                    if (answer is "s" or "r") Recorder.SkipCurrent();
                    var lines = ObserveAnalysis.Summarize(step, Recorder.Rows, Recorder.ValueBefore(c.Button, step.StartMs));
                    foreach (var l in lines.Skip(1)) output.WriteLine(l);
                    if (answer != "r") break;
                }
            }
        }
        Recorder.BeginStep("end", null, null, ObserveClock.NowMs);
        output.WriteLine("\nall steps done");
    }

    void CheckSetup() => CheckParkedSetup(Recorder, simvars);

    /// <summary>Read-only check of the parked setup, like SafetyMonitor.TakeBaseline. Stops, never fixes.</summary>
    internal static void CheckParkedSetup(ObserveRecorder rec, ISimvarReader simvars)
    {
        double Lvar(string name) => rec.TryGet(name, out var v) ? v : throw new SafetyStopException($"cannot read {name}");
        var s = simvars.Latest ?? throw new SafetyStopException("no data from the sim (SIM ON GROUND, N1)");
        if (s.OnGround < 0.5) throw new SafetyStopException("not on the ground");
        if (s.GroundSpeedKt > 1) throw new SafetyStopException($"moving ({s.GroundSpeedKt:0.0} kt)");
        if (Lvar(SafetyVars.ParkingBrake) < 0.5) throw new SafetyStopException("parking brake is not set");
        foreach (var m in SafetyVars.EngineMasters)
            if (Lvar(m) != 0) throw new SafetyStopException($"{m} is not off");
        if (Math.Max(s.N1Eng1, s.N1Eng2) > 2 || s.Combustion1 > 0 || s.Combustion2 > 0) throw new SafetyStopException("an engine is running");
        foreach (var v in SafetyVars.Power)
            if (Lvar(v) < 0.5) throw new SafetyStopException($"{v} is off: aircraft not on external power");
    }

    void LinkCheck() => WarnIfNoData(Recorder, output);

    internal static void WarnIfNoData(ObserveRecorder rec, TextWriter output)
    {
        var age = ObserveClock.NowMs - rec.LastMs;
        if (age > 3000) output.WriteLine($"   warning: no data from the sim for {age / 1000:0} s; this step may be incomplete (r = redo)");
    }

    void WriteResults()
    {
        if (!Recorder.Steps.Any(st => st.Control is not null))
        {
            output.WriteLine("no step was started; nothing written");
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(settings.CsvPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(settings.SummaryPath)!);
        var csv = Recorder.Csv();
        File.WriteAllText(settings.CsvPath, csv);
        if (settings.RawDir is { } raw)
        {
            Directory.CreateDirectory(raw);
            File.WriteAllText(Path.Combine(raw, $"observe-{DateTime.Now:yyyyMMdd-HHmmss}.csv"), csv);
        }
        var report = ObserveAnalysis.Report(Recorder);
        File.WriteAllText(settings.SummaryPath, $"observe run {DateTime.Now:yyyy-MM-dd HH:mm:ss}, title {simvars.Title ?? "?"}\n{report}");
        output.WriteLine($"\n{report}");
        output.WriteLine($"written: {settings.CsvPath}");
        output.WriteLine($"written: {settings.SummaryPath}");
    }
}
