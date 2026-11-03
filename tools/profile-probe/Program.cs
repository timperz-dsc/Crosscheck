// Crosscheck profile probe (CRO-8 step 2). See README.md.

using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.SimConnect;
using Microsoft.Extensions.Logging;

namespace ProfileProbe;

static class Program
{
    const int ExitError = 1, ExitSafety = 2, ExitNotRestored = 3, ExitCancelled = 4;

    static readonly Phase[] AutoPhases = [Phase.Verified, Phase.Buttons, Phase.Encoders, Phase.Breakers];

    static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { Usage(); return 0; }
        var paths = RepoPaths.Find();
        var opts = Options.Parse(args.Skip(1).ToArray());
        using var cts = new CancellationTokenSource();
        var presses = 0;
        Console.CancelKeyPress += (_, e) =>
        {
            // First Ctrl+C: stop after restoring the current var. A second one kills the process;
            // the checkpoint then restores the var on the next start.
            if (Interlocked.Increment(ref presses) == 1) { e.Cancel = true; cts.Cancel(); Console.WriteLine(args[0] is "observe" or "positions" ? "Ctrl+C: saving what was recorded, then stopping..." : "Ctrl+C: restoring the current var, then stopping..."); }
        };

        try
        {
            switch (args[0])
            {
                case "plan": return PlanCommand(paths);
                case "propose": return Proposer.Run(paths, Console.Out);
                case "release":
                    Directory.CreateDirectory(paths.WorkDir);
                    File.WriteAllText(paths.Release, DateTime.UtcNow.ToString("O"));
                    Console.WriteLine("release requested");
                    return 0;
                case "label": return LabelCommand(paths, opts);
                case "info":
                    if (opts.Var is null || opts.Text is null) { Console.WriteLine("info needs --var and --text"); return ExitError; }
                    new ResultLog(paths.Details).Add(new ProbeResult { Var = opts.Var, Phase = Phase.Readonly, Test = "manual", Result = "info", Notes = opts.Text });
                    return 0;
                case "restore": return await SimCommand(paths, null, opts, cts.Token);
                case "undo": return await UndoCommand(paths, opts, cts.Token);
                case "peek":return await PeekCommand(paths, args.Skip(1).Where(a => !a.StartsWith("--")).ToArray(), args.Contains("--clear"), args.Contains("--pilot-ok"), cts.Token);
                case "observe": return await ObserveCommand(paths, opts, cts.Token);
                case "readonly": return await SimCommand(paths, Phase.Readonly, opts, cts.Token);
                case "auto":
                    foreach (var p in AutoPhases)
                    {
                        var code = await SimCommand(paths, p, opts, cts.Token);
                        if (code != 0) return code;
                    }
                    return 0;
                case "takeover": return await SimCommand(paths, Phase.Takeover, opts, cts.Token);
                case "hold": return await SimCommand(paths, Phase.Hold, opts, cts.Token);
                // Supervised write test (gate test 3 prep, owner approved): one control at a time, Enter per write.
                case "writetest": return await SimCommand(paths, Phase.WriteTest, opts, cts.Token);
                // CRO-40: registration only (no writes, no data requests), owner approved; finds the definition limit.
                case "deflimit": return await DefLimit.Run(Path.Combine(paths.ProfileDir, "deflimit-results.md"), Console.Out, cts.Token);
                // Read only: the pilot moves the controls (CRO-24). The old write test of this name stays off:
                // writing positions that do not exist most likely crashed the Fenix WASM.
                case "positions": return await PositionsCommand(paths, opts, cts.Token);
                default:
                    if (Enum.TryParse<Phase>(args[0], true, out var phase) && AutoPhases.Contains(phase))
                        return await SimCommand(paths, phase, opts, cts.Token);
                    Usage();
                    return ExitError;
            }
        }
        catch (SafetyStopException e) { Console.WriteLine($"SAFETY STOP: {e.Message}"); return ExitSafety; }
        catch (NotRestoredException e) { Console.WriteLine($"NOT RESTORED: {e.Message}"); return ExitNotRestored; }
        catch (OperationCanceledException) { Console.WriteLine("stopped"); return ExitCancelled; }
    }

    static void Usage() => Console.WriteLine("""
        profile-probe <command> [options]
          plan                      dry run: what each phase would write (no sim)
          observe [--only ID]       CRO-29: read only; you click push buttons step by step, it records every change
          readonly                  item 7 + d/e: 30 s noise sample, TITLE, fuel (no writes)
          verified | buttons | encoders | breakers   items 1+4, 2, 5, 6
          auto                      verified, buttons, encoders, breakers back to back
          positions [--run gate3|gate3b|gate2] [--only ID]
                                    CRO-24: read only; you move each control through its positions, it records and classifies every value
                                    (gate3, the default: the gate test 3 prep run; gate2: the run before gate test 2)
          takeover --side capt|fo   supervised a: press 1 s, release
          hold --var NAME --value N [--seconds 60]   supervised b: hold a known ND value, then restore
          writetest [--only ID] [--bridge-profile]
                                    supervised: flaps, speed brake, A/SKID, cockpit door, FCU SPD, tray table; Enter before each write,
                                    read back 2.5 s, put back. --bridge-profile: through the bridge's own SimConnect definitions
                                    (the real profile's watch list and write-slot pool, CRO-40); switches only
          deflimit                  CRO-40: registration only (no writes, no data requests): finds how many SimConnect
                                    data definitions the sim accepts from one client
          release                   ends a running hold
          label --var NAME --value N --text LABEL    record what the ND showed
          info --var NAME --text TEXT                record a manual finding (smoke test, surprise, crash-start/crash-end...)
          restore                   restore a var left pending by an interrupted run
          propose                   write probe-results.csv, probe-report.md, profile.proposed.yaml (no sim)
        options: --only NAME (one var), --redo (probe the phase again from the start)
        range rule: only values known to exist for a var are ever written (KnownValues.cs)
        exit codes: 0 ok, 1 error, 2 safety stop, 3 a var could not be restored, 4 Ctrl+C
        """);

    sealed record Options(string? Only, bool Redo, string? Side, string? Var, double? Value, int Seconds, string? Text, string? Run = null, bool BridgeProfile = false)
    {
        public static Options Parse(string[] a)
        {
            string? Get(string key) { var i = Array.IndexOf(a, key); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
            double? Num(string key) => Get(key) is { } s && double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
            return new Options(Get("--only"), a.Contains("--redo"), Get("--side"), Get("--var"), Num("--value"), (int)(Num("--seconds") ?? 60), Get("--text"), Get("--run"),
                a.Contains("--bridge-profile"));
        }
    }

    static int PlanCommand(RepoPaths paths)
    {
        var inputs = Inputs.Load(paths);
        var log = new ResultLog(paths.Details);
        var rejects = Rejects(log);
        var known = KnownValues.Build(inputs, log.ReadLatest());
        Console.WriteLine($"watched vars: {ProbeProfile.Watched(inputs).Count}");
        var deniedInProfile = inputs.Profile.Vars.Count(v => inputs.Deny.Denies(v.Name) is not null);
        Console.WriteLine($"profile vars on the deny list: {deniedInProfile}; breakers in the sample: {inputs.Deny.Breakers.Count}");
        foreach (var phase in AutoPhases.Append(Phase.Positions).Append(Phase.Takeover).Append(Phase.Hold))
        {
            var (targets, skipped) = Targets.For(phase, inputs, rejects, known);
            Console.WriteLine($"\n{phase}: {targets.Count} to probe, {skipped.Count} skipped");
            foreach (var t in targets)
                Console.WriteLine($"  {t.Name}: may write {(t.Values is { } v ? string.Join(",", v) : t.Delta ? "+1/-1 (counter)" : "+1 presses (counter)")}");
            foreach (var g in skipped.GroupBy(s => s.Why))
                Console.WriteLine($"  skipped ({g.Key}): {string.Join(" ", g.Select(s => s.Name))}");
        }
        return 0;
    }

    static List<string> Rejects(ResultLog log) =>
        log.ReadLatest().Where(r => r.Phase == Phase.Verified && r.Test == "verified-binary" && r.Result == "rejected").Select(r => r.Var).ToList();

    static int LabelCommand(RepoPaths paths, Options o)
    {
        if (o.Var is null || o.Value is null || o.Text is null) { Console.WriteLine("label needs --var, --value and --text"); return ExitError; }
        new ResultLog(paths.Details).Add(new ProbeResult
        {
            Var = o.Var, Phase = Phase.Hold, Test = $"label-{o.Value}", Result = "label",
            ProposedValueLabels = new() { [o.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)] = o.Text },
            Notes = $"pilot read the ND: {o.Value} = {o.Text}",
        });
        Console.WriteLine($"{o.Var} {o.Value} = {o.Text}");
        return 0;
    }

    /// <param name="phase">null = only restore a pending var.</param>
    static async Task<int> SimCommand(RepoPaths paths, Phase? phase, Options opts, CancellationToken ct)
    {
        // Only one probe may talk to the sim at a time (two would restore each other's vars).
        Directory.CreateDirectory(paths.WorkDir);
        FileStream runLock;
        try { runLock = new FileStream(Path.Combine(paths.WorkDir, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { Console.WriteLine("another profile-probe run is active; not starting"); return ExitError; }
        using var heldLock = runLock;

        var inputs = Inputs.Load(paths);
        var checkpoint = new Checkpoint(paths.Checkpoint);
        var log = new ResultLog(paths.Details);
        if (phase is Phase.Takeover or Phase.Hold or Phase.WriteTest || opts.Redo && phase is { } rp) checkpoint.ResetPhase(phase!.Value);

        var known = KnownValues.Build(inputs, log.ReadLatest());
        var writeControls = WriteTest.Selected(opts.Only);
        var (targets, skipped) = phase == Phase.WriteTest ? (WriteTest.Targets(writeControls), [])
            : phase is { } p ? Targets.For(p, inputs, Rejects(log), known) : ([], []);
        if (phase == Phase.Takeover)
        {
            var side = opts.Side?.ToLowerInvariant() switch { "capt" => "S_FC_CAPT_INST_DISCONNECT", "fo" => "S_FC_FO_INST_DISCONNECT", _ => null };
            if (side is null) { Console.WriteLine("takeover needs --side capt|fo"); return ExitError; }
            targets = targets.Where(t => t.Name == side).ToList();
        }
        if (phase == Phase.Hold)
        {
            if (opts.Var is null || opts.Value is not { } hv) { Console.WriteLine("hold needs --var (an ND MODE/ZOOM var) and --value"); return ExitError; }
            if (!known.IsKnown(opts.Var, hv)) { Console.WriteLine($"range rule: {hv} is not known to exist for {opts.Var} (known: {string.Join(",", known.For(opts.Var))}); nothing written"); return ExitError; }
            targets = targets.Where(t => t.Name == opts.Var).ToList();
            if (targets.Count == 0) { Console.WriteLine($"{opts.Var} is not an ND MODE/ZOOM var"); return ExitError; }
        }
        if (opts.Only is { } only && phase != Phase.WriteTest) targets = targets.Where(t => t.Name == only).ToList();
        foreach (var s in skipped) log.Add(new ProbeResult { Var = s.Name, Phase = s.Phase, Test = s.Phase.ToString().ToLowerInvariant(), Result = "skipped", Notes = s.Why });

        var pending = checkpoint.Data.Pending;
        var writable = targets.ToList();
        if (pending is not null)
            writable.Add(new Target(pending.Var, pending.Phase, [pending.Original, 0, 1], Delta: pending.Phase == Phase.Encoders, Counter: pending.Kind == RestoreKind.Counter));
        writable = Merge(writable);

        var watched = ProbeProfile.Watched(inputs);
        var profile = ProbeProfile.Build(inputs, writable, watched);
        string? mode = null;
        if (opts.BridgeProfile)
        {
            if (phase != Phase.WriteTest) { Console.WriteLine("--bridge-profile is only for writetest"); return ExitError; }
            if (pending is not null) { Console.WriteLine($"{pending.Var} is still pending from an earlier run: run `restore` first, then --bridge-profile"); return ExitError; }
            try
            {
                (profile, watched) = BridgePath.For(inputs.Profile, Crosscheck.Bridge.UnsyncedList.LoadNextTo(paths.Profile, inputs.Profile), writeControls);
            }
            catch (InvalidOperationException e) { Console.WriteLine(e.Message); return ExitError; }
            mode = BridgePath.Describe(watched.Count);
            Console.WriteLine(mode);
        }
        using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Warning));
        await using var sim = new SimConnectAdapter(profile, loggerFactory.CreateLogger("sim"));
        await using var gql = new FenixGraphQlClient(1, loggerFactory.CreateLogger("gql"));
        await using var simvars = new SimvarReader();
        await sim.StartAsync(new SimPlan(watched), ct);
        await simvars.StartAsync(ct);

        Console.WriteLine($"{(phase?.ToString() ?? "restore")}: connecting...");
        await WaitFor(() => sim.State == SimState.Connected, 60, "the sim (SimConnect)", ct);
        await WaitFor(() => SafetyVars.Lvars.All(n => sim.TryGet(n, out _)) && simvars.Latest is not null, 15, "first values from the sim", ct);
        await Task.Delay(1000, ct); // let every chunk deliver its first values
        if (await gql.SoftwareVersionAsync(ct) is not { } version) throw new SafetyStopException("Fenix GraphQL (localhost:8083) does not answer");
        Console.WriteLine($"connected; Fenix {version}; title {simvars.Title ?? "?"}");

        var safety = new SafetyMonitor(sim, simvars, gql);
        safety.TakeBaseline();
        var noisy = File.Exists(paths.Noisy)
            ? System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(paths.Noisy))!.ToHashSet(StringComparer.Ordinal)
            : [];
        if (phase is not (null or Phase.Readonly) && noisy.Count == 0)
            Console.WriteLine("note: no noise sample yet (run `readonly` first); effect diffs may include vars that move on their own");
        var session = new ProbeSession(sim, gql, safety, checkpoint, log, watched, noisy, new ProbeTiming(), Console.Out) { Inputs = inputs };

        if (pending is not null)
        {
            Console.WriteLine($"restoring {pending.Var} left pending by an earlier run...");
            if (!await RestorePending(session, pending)) throw new NotRestoredException($"{pending.Var} (pending from an earlier run)");
            Console.WriteLine($"  {pending.Var} restored");
        }
        if (phase is null) return Summary(checkpoint);

        if (phase == Phase.WriteTest)
        {
            if (writeControls.Count == 0) { Console.WriteLine($"no control matches --only {opts.Only}; ids: {string.Join(" ", WriteTest.Controls.Select(c => c.Id))}"); return ExitError; }
            var lines = new ConsoleLines();
            return await WriteTest.Run(session, writeControls, lines.ReadAsync, Path.Combine(paths.ProfileDir, "writetest-results.md"), ct, mode);
        }

        if (phase == Phase.Readonly)
        {
            await ReadonlyPhase.Run(session, inputs, simvars, paths.Noisy, ct);
            return 0;
        }

        var test = phase switch
        {
            Phase.Verified => Probes.Verified,
            Phase.Buttons => Probes.Button,
            Phase.Encoders => Probes.Encoder,
            Phase.Breakers => Probes.Breaker,
            Phase.Takeover => Probes.Takeover,
            Phase.Hold => Probes.Hold(opts.Value!.Value, Math.Clamp(opts.Seconds, 5, 120), paths.Release),
            _ => throw new InvalidOperationException(),
        };
        var todo = targets.Count(t => !checkpoint.IsDone(t.Phase, t.Name));
        Console.WriteLine($"{phase}: {targets.Count} vars ({todo} not done yet), {skipped.Count} skipped");
        try
        {
            foreach (var t in targets)
                await session.RunVar(t, test, ct);
        }
        finally
        {
            Summary(checkpoint);
        }
        return 0;
    }

    /// <summary>
    /// Read only (CRO-29 part 1). No SimConnectAdapter here: only ObserveReader and SimvarReader,
    /// which can define and request data and nothing else.
    /// </summary>
    static async Task<int> ObserveCommand(RepoPaths paths, Options opts, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.WorkDir);
        FileStream runLock;
        try { runLock = new FileStream(Path.Combine(paths.WorkDir, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { Console.WriteLine("another profile-probe run is active; not starting"); return ExitError; }
        using var heldLock = runLock;

        await using var source = new ObserveReader(ObserveVars.All);
        await using var simvars = new SimvarReader();
        await using var gql = new FenixGraphQlClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var suffix = opts.Only is { } o ? "-" + o.ToLowerInvariant() : "";
        var settings = new ObserveSettings
        {
            CsvPath = Path.Combine(paths.ProfileDir, $"observe-results{suffix}.csv"),
            SummaryPath = Path.Combine(paths.WorkDir, $"observe-summary{suffix}.txt"),
            RawDir = paths.WorkDir,
            Only = opts.Only,
        };
        var lines = new ConsoleLines();
        return await new ObservePhase(source, simvars, gql, Console.Out, lines.ReadAsync, settings).Run(ct);
    }

    /// <summary>
    /// Read only (CRO-24), like observe: only ObserveReader and SimvarReader, no SimConnectAdapter.
    /// </summary>
    static async Task<int> PositionsCommand(RepoPaths paths, Options opts, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.WorkDir);
        FileStream runLock;
        try { runLock = new FileStream(Path.Combine(paths.WorkDir, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { Console.WriteLine("another profile-probe run is active; not starting"); return ExitError; }
        using var heldLock = runLock;

        var run = opts.Run ?? PositionsVars.DefaultRun;
        if (!PositionsVars.Runs.TryGetValue(run, out var controls))
        {
            Console.WriteLine($"unknown run {run}; runs: {string.Join(" ", PositionsVars.Runs.Keys)}");
            return ExitError;
        }
        await using var source = new ObserveReader(PositionsVars.AllOf(controls));
        await using var simvars = new SimvarReader();
        // The gate test 2 run kept the plain file names; later runs never overwrite them.
        var suffix = (run == "gate2" ? "" : "-" + run) + (opts.Only is { } o ? "-" + o.ToLowerInvariant() : "");
        var settings = new PositionsSettings
        {
            Controls = controls,
            MissingSimVars = () => source.MissingSimVars,
            ResultsPath = Path.Combine(paths.ProfileDir, $"positions-results{suffix}.csv"),
            ChangesPath = Path.Combine(paths.ProfileDir, $"positions-changes{suffix}.csv"),
            SummaryPath = Path.Combine(paths.WorkDir, $"positions-summary{suffix}.txt"),
            RawDir = paths.WorkDir,
            Only = opts.Only,
        };
        var lines = new ConsoleLines();
        return await new PositionsPhase(source, simvars, Console.Out, lines.ReadAsync, settings).Run(ct);
    }

    /// <summary>
    /// Writes one var back to the original value the probe recorded for it (latest result),
    /// and nothing else. For a var a probe left changed without noticing.
    /// </summary>
    static async Task<int> UndoCommand(RepoPaths paths, Options opts, CancellationToken ct)
    {
        if (opts.Var is null) { Console.WriteLine("undo needs --var"); return ExitError; }
        var last = new ResultLog(paths.Details).ReadLatest().Where(r => r.Var == opts.Var && r.Original is not null).OrderBy(r => r.At).LastOrDefault();
        if (last?.Original is not { } original) { Console.WriteLine($"no recorded original value for {opts.Var}"); return ExitError; }
        Directory.CreateDirectory(paths.WorkDir);
        using var heldLock = new FileStream(Path.Combine(paths.WorkDir, "run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var inputs = Inputs.Load(paths);
        var watched = ProbeProfile.Watched(inputs);
        var target = new Target(opts.Var, last.Phase, [original]);
        await using var sim = new SimConnectAdapter(ProbeProfile.Build(inputs, [target], watched), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await using var simvars = new SimvarReader();
        await sim.StartAsync(new SimPlan(watched), ct);
        await simvars.StartAsync(ct);
        await WaitFor(() => sim.State == SimState.Connected, 60, "the sim (SimConnect)", ct);
        await WaitFor(() => SafetyVars.Lvars.All(n => sim.TryGet(n, out _)) && simvars.Latest is not null, 15, "first values", ct);
        await Task.Delay(1000, ct);
        new SafetyMonitor(sim, simvars, new FenixGraphQlClient(1, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)).TakeBaseline();
        sim.TryGet(opts.Var, out var now);
        Console.WriteLine($"{opts.Var}: now {now}, recorded original {original} ({last.Phase}, {last.At:u})");
        if (Math.Abs(now - original) < 1e-9) { Console.WriteLine("already at the original value, nothing written"); return 0; }
        await sim.WriteAsync(opts.Var, original);
        var until = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < until && !(sim.TryGet(opts.Var, out var v) && Math.Abs(v - original) < 1e-9)) await Task.Delay(20, ct);
        sim.TryGet(opts.Var, out var after);
        Console.WriteLine(Math.Abs(after - original) < 1e-9 ? $"{opts.Var} written back to {original}" : $"NOT RESTORED: {opts.Var} reads {after}");
        return Math.Abs(after - original) < 1e-9 ? 0 : ExitNotRestored;
    }

    /// <summary>Read only: no var is writable in this profile. Prints the vars and the pending var's effect check.</summary>
    /// <param name="pilotOk">The pilot checked the cockpit and says it is as before: clear the pending
    /// entry if the var itself is back at its original value, even if effect vars differ.</param>
    static async Task<int> PeekCommand(RepoPaths paths, string[] names, bool clear, bool pilotOk, CancellationToken ct)
    {
        var inputs = Inputs.Load(paths);
        var checkpoint = new Checkpoint(paths.Checkpoint);
        var watched = ProbeProfile.Watched(inputs);
        await using var sim = new SimConnectAdapter(ProbeProfile.Build(inputs, [], watched), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        await sim.StartAsync(new SimPlan(watched), ct);
        await WaitFor(() => sim.State == SimState.Connected, 60, "the sim (SimConnect)", ct);
        await WaitFor(() => SafetyVars.Lvars.All(n => sim.TryGet(n, out _)), 15, "first values", ct);
        await Task.Delay(1000, ct);
        string V(string n) => sim.TryGet(n, out var v) ? v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "?";
        foreach (var n in names.Concat(SafetyVars.Lvars)) Console.WriteLine($"  {n,-48} {V(n)}");
        if (checkpoint.Data.Pending is { } p)
        {
            Console.WriteLine($"pending: {p.Var} original {p.Original}, now {V(p.Var)}");
            foreach (var (k, v) in p.EffectStart.Where(kv => !(sim.TryGet(kv.Key, out var now) && Math.Abs(now - kv.Value) < 1e-6)))
                Console.WriteLine($"  differs: {k} was {v}, now {V(k)}{(VarRun.Settling(k) ? " (settling value)" : "")}");
            var allBack = p.EffectStart.All(kv => VarRun.Settling(kv.Key) || sim.TryGet(kv.Key, out var now) && Math.Abs(now - kv.Value) < 1e-6);
            if (clear && allBack)
            {
                // The pilot put it back by hand; nothing is written here.
                checkpoint.Finish(p.Phase, p.Var, true);
                Console.WriteLine($"pending {p.Var} cleared: every effect var is back (var reads {V(p.Var)})");
            }
            else if (clear && pilotOk && sim.TryGet(p.Var, out var cur) && Math.Abs(cur - p.Original) < 1e-9)
            {
                // Nothing is written: the var is back, and the pilot confirmed the cockpit.
                checkpoint.Finish(p.Phase, p.Var, true);
                var differs = string.Join(", ", p.EffectStart.Where(kv => !(sim.TryGet(kv.Key, out var now) && Math.Abs(now - kv.Value) < 1e-6)).Select(kv => $"{kv.Key} {kv.Value} -> {V(kv.Key)}"));
                new ResultLog(paths.Details).Add(new ProbeResult
                {
                    Var = "pilot-ok-" + p.Var, Phase = Phase.Readonly, Test = "manual", Result = "info",
                    Notes = $"{p.Var} is back at {p.Original}; effect vars still differ ({differs}); the pilot checked the cockpit and confirmed it is as before",
                });
                Console.WriteLine($"pending {p.Var} cleared on the pilot's confirmation (var reads {V(p.Var)}; still differs: {differs})");
            }
            else if (clear) Console.WriteLine("not cleared: effect vars still differ" + (pilotOk ? " and the var is not at its original value" : ""));
        }
        return 0;
    }

    internal static async Task<bool> RestorePending(ProbeSession session, Pending p)
    {
        var run = new VarRun(session, new Target(p.Var, p.Phase, null), p.Original) { Kind = p.Kind };
        foreach (var (k, v) in p.EffectStart) run.EffectStart[k] = v;
        var (ok, why) = await session.Restore(run);
        if (ok) session.Checkpoint.ClearPending(true, p.Var);
        else { session.Checkpoint.KeepPending(p.Var); Console.WriteLine($"  {p.Var}: {why}"); }
        return ok;
    }

    static List<Target> Merge(List<Target> targets) =>
        targets.GroupBy(t => t.Name).Select(g =>
        {
            var first = g.First();
            var values = g.Any(t => t.Values is null) ? null : g.SelectMany(t => t.Values!).Distinct().Order().ToList();
            return first with { Values = values, Delta = g.Any(t => t.Delta), Counter = g.Any(t => t.Counter) };
        }).ToList();

    static int Summary(Checkpoint checkpoint)
    {
        var bad = checkpoint.Data.NotRestored;
        Console.WriteLine(bad.Count == 0 ? "not restored: none" : $"NOT RESTORED: {string.Join(", ", bad)}");
        return bad.Count == 0 ? 0 : ExitNotRestored;
    }

    static async Task WaitFor(Func<bool> condition, int seconds, string what, CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new SafetyStopException($"no connection to {what} after {seconds} s");
            await Task.Delay(100, ct);
        }
    }
}

/// <summary>Console lines read on a background thread, so waiting for Enter can be cancelled with Ctrl+C.</summary>
sealed class ConsoleLines
{
    readonly System.Threading.Channels.Channel<string?> _lines = System.Threading.Channels.Channel.CreateUnbounded<string?>();

    public ConsoleLines()
    {
        new Thread(() =>
        {
            while (true)
            {
                var line = Console.ReadLine();
                _lines.Writer.TryWrite(line);
                if (line is null) return;
            }
        }) { IsBackground = true, Name = "console input" }.Start();
    }

    public async Task<string?> ReadAsync(CancellationToken ct) => await _lines.Reader.ReadAsync(ct);
}
