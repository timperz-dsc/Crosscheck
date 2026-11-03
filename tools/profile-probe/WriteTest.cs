// The supervised write test (gate test 3 prep, owner approved 2026-11-03): does a write of the var Fenix
// shows stick, and does the cockpit follow? One control at a time, only values seen in the owner's cockpit
// (scan, gate test logs, positions runs). Before each write the tool says what it writes and what to watch,
// and waits for Enter; after it, it reads back for 2.5 s. Every control ends where it started (the probe's
// restore runs after each control, also on q or Ctrl+C). It stops at once on an unexpected value, when
// Fenix stops answering, or when the safety check sees a change (the same check as every probe phase).

namespace ProfileProbe;

/// <param name="Value">What to write.</param>
/// <param name="Means">What the value is in the cockpit.</param>
/// <param name="Watch">What the pilot should look at.</param>
/// <param name="Expect">Vars that should follow, with the value they should end at.</param>
sealed record WriteStep(double Value, string Means, string Watch, IReadOnlyDictionary<string, double>? Expect = null);

/// <param name="Start">The value the control must rest at before the test (null: any value the plan accepts).</param>
/// <param name="Allowed">Every value this control may get: seen in the owner's cockpit. The guard refuses the rest.</param>
/// <param name="Plan">The steps, from the value the control rests at now; null if that value is not a start this test accepts.</param>
/// <param name="StopWhenModelFails">A step that does not stick or whose expected vars do not follow ends the control:
/// no other values are tried (FCU SPD).</param>
sealed record WriteControl(
    string Id, string Label, string Var, double[] Allowed, Func<double, IReadOnlyList<WriteStep>?> Plan,
    string[] Effects, string Evidence, string? Warning = null, bool DryHydraulics = false, string? Before = null,
    bool StopWhenModelFails = false);

static class WriteTest
{
    public const int ReadBackMs = 2500;
    const int SampleMs = 50;

    /// <summary>Speed brake at half: sent exactly like this twice in gate test 2 (the positions run saw 2 too).</summary>
    public const double SpeedBrakeHalf = 2.0234375;

    static Func<double, IReadOnlyList<WriteStep>?> From(double start, params WriteStep[] steps) =>
        current => Math.Abs(current - start) < 1e-6 ? steps : null;

    static Dictionary<string, double> E(params (string Var, double Value)[] e) => e.ToDictionary(x => x.Var, x => x.Value, StringComparer.Ordinal);

    static readonly WriteStep Open = new(1, "open", "the cockpit door opens and stays open; the OPEN light on the door panel", E(("I_PED_COCKPIT_DOOR_U", 1)));
    static readonly WriteStep Closed = new(0, "closed", "the cockpit door closes and stays closed; the OPEN light goes off", E(("I_PED_COCKPIT_DOOR_U", 0)));

    public static readonly WriteControl[] Controls =
    [
        new("FLAPS", "flaps lever", "S_FC_FLAPS", [0, 1],
            From(0,
                new WriteStep(1, "lever to 1", "the flaps lever moves to 1 and stays there; the ECAM flap indication"),
                new WriteStep(0, "lever back to 0", "the lever goes back to 0")),
            ["S_FC_FLAPS_LATCH", "FLAPS_INTERNAL_CONFIG"],
            "scan 0; positions runs 2026-11-03: 0 … 4", DryHydraulics: true),
        new("SPEEDBRAKE", "speed brake lever", "A_FC_SPEEDBRAKE", [0, 1, SpeedBrakeHalf],
            From(1,
                new WriteStep(SpeedBrakeHalf, "half", "the speed brake lever moves to half and stays there"),
                new WriteStep(1, "RET", "the lever goes back to RET"),
                new WriteStep(0, "ARM", "the lever is pulled up to ARM and stays armed"),
                new WriteStep(1, "RET", "the lever goes back down to RET")),
            ["B_FC_SPEEDBRAKE"],
            "scan 1 (RET); gate test 2: 2.0234375 sent twice; positions runs 2026-11-03: 0 = ARM"),
        new("ANTI_SKID", "A/SKID & N/W STRG switch", "S_FC_MIP_ANTI_SKID", [0, 1],
            From(1,
                new WriteStep(0, "OFF", "the A/SKID & N/W STRG switch goes to OFF and stays there; the BRAKE PRESS triple indicator"),
                new WriteStep(1, "ON", "the switch goes back to ON")),
            [],
            "scan 1; positions runs 2026-11-03: 1 -> 0 -> 1"),
        new("DOOR", "cockpit door", "S_COCKPIT_DOOR", [0, 1],
            // 0 = closed, 1 = open (the owner saw it in the write test 2026-11-03; the first version had them the
            // other way round). From either resting position: to the other one and back.
            current => current switch
            {
                0 => [Open, Closed],
                1 => [Closed, Open],
                _ => null,
            },
            ["I_PED_COCKPIT_DOOR_U", "I_PED_COCKPIT_DOOR_L", "B_DOORS_COCKPIT_LOCKED"],
            "write test 2026-11-03: 0 closed, 1 open, the OPEN light I_PED_COCKPIT_DOOR_U follows; scan 1 (open at the gate)",
            Warning: "In the first probe run (2026-11-01) the door panel lights did not come back after this var was restored, " +
                     "and stepping it through positions that do not exist most likely crashed the Fenix WASM. Only 1 and 0 are written here."),
        new("FCU_SPD", "FCU SPD knob", "S_FCU_SPEED", [-1, 0, 1],
            current =>
                // The owner's model: a counter, pull +1 and push -1 from the current value. Only from a resting
                // value whose +1 and -1 were both seen (-1, 0, 1).
                Math.Abs(current) < 1e-6
                    ? [
                        new WriteStep(current + 1, "own + 1 (pull: selected speed)",
                            "the SPD window shows a speed (no dashes) and the managed dot goes off",
                            E(("I_FCU_SPEED_MANAGED", 0), ("B_FCU_SPEED_DASHED", 0))),
                        new WriteStep(current, "own - 1 (push: managed speed)",
                            "the SPD window shows dashes again and the managed dot comes on",
                            E(("I_FCU_SPEED_MANAGED", 1), ("B_FCU_SPEED_DASHED", 1))),
                    ]
                    : null,
            ["I_FCU_SPEED_MANAGED", "B_FCU_SPEED_DASHED", "N_FCU_SPEED", "I_FCU_SPEED_MODE"],
            "positions runs: gate test 2 prep -1 (push), 2026-11-03 0 -> 1 -> 0 with the managed lamp and dashes",
            Before: "Set up the FMGS first so managed speed is available (INIT A, a departure in F-PLN, PERF TAKE OFF). " +
                    "The SPD window must show dashes with the managed dot. Press Enter when done.",
            StopWhenModelFails: true),
        // CRO-40 (gate test 3 cause A): late in the profile, so its writes never landed with one SimConnect definition
        // per var. Run with --bridge-profile to write it through the bridge's own definitions (the write-slot pool).
        new("TRAY", "captain's tray table", "S_TRAY_TABLE_CAPT", [0, 1],
            current => current switch
            {
                0 => [new WriteStep(1, "position 1", "the captain's tray table moves and stays there"), new WriteStep(0, "position 0", "the tray table goes back")],
                1 => [new WriteStep(0, "position 0", "the captain's tray table moves and stays there"), new WriteStep(1, "position 1", "the tray table goes back")],
                _ => null,
            },
            [],
            "scan 0; positions run 2026-11-03: 0, 1; gate test 3: refused past the definition limit; cosmetic, nothing in the aircraft depends on it"),
    ];

    public static IReadOnlyList<WriteControl> Selected(string? only) =>
        only is null ? Controls : Controls.Where(c => string.Equals(c.Id, only, StringComparison.OrdinalIgnoreCase) || c.Var == only).ToList();

    /// <summary>The writable targets: each control's var with exactly its allowed values.</summary>
    public static List<Target> Targets(IEnumerable<WriteControl> controls) =>
        controls.Select(c => new Target(c.Var, Phase.WriteTest, c.Allowed)).ToList();

    /// <param name="mode">A line for the report on how the writes are made (--bridge-profile), or null.</param>
    public static async Task<int> Run(ProbeSession session, IReadOnlyList<WriteControl> controls, Func<CancellationToken, Task<string?>> readLine,
        string resultsPath, CancellationToken ct, string? mode = null)
    {
        var report = new List<string> { $"# Supervised write test, {DateTime.Now:yyyy-MM-dd HH:mm}", "" };
        if (mode is not null) report.AddRange([mode, ""]);
        void Say(string line) { session.Out.WriteLine(line); report.Add(line); }
        session.Out.WriteLine($"""

            Supervised write test: {controls.Count} controls, one at a time. Before each write you see what is written
            and what to watch; Enter = write, s = skip the rest of this control, q = stop. Every control is put back
            where it started. The tool stops at once on anything unexpected or when Fenix stops answering.
            """);
        try
        {
            foreach (var c in controls)
            {
                Say($"\n== {c.Label} ({c.Var})");
                if (c.Warning is not null) session.Out.WriteLine($"   note: {c.Warning}");
                if (c.Before is not null)
                {
                    session.Out.Write($"   {c.Before}\n   > ");
                    if ((await readLine(ct))?.Trim().ToLowerInvariant() is null or "q") { Say("   stopped before this control"); return 0; }
                }
                if (c.DryHydraulics && await session.Safety.HydraulicsDry(ct) is not true)
                {
                    Say("   skipped: the hydraulics are pressurised (or unknown); flaps are only moved with no hydraulic pressure");
                    continue;
                }
                if (session.Value(c.Var) is not { } current) { Say("   skipped: cannot read the var"); continue; }
                if (c.Plan(current) is not { } steps)
                {
                    Say($"   skipped: it rests at {F(current)}, not at a start this test accepts; put it back by hand and run --only {c.Id}");
                    continue;
                }
                Say($"   starts at {F(current)}; values this test may write: {string.Join(", ", c.Allowed.Select(F))} ({c.Evidence})");
                var target = new Target(c.Var, Phase.WriteTest, c.Allowed);
                session.Checkpoint.ResetPhase(Phase.WriteTest);
                try
                {
                    await session.RunVar(target, (run, token) => Steps(run, c, steps, readLine, Say, token), ct);
                }
                catch (Exception e) when (e is not NotRestoredException)
                {
                    // The probe restore has run (RunVar restores before it rethrows): say where the control ended.
                    var at = session.Value(c.Var);
                    Say($"   stopped ({(e is OperationCanceledException ? "by the pilot" : e.Message)}); back at {F(at)} " +
                        $"{(ProbeSession.Same(at, current) ? "(where it started)" : "(NOT where it started)")}");
                    throw;
                }
                var end = session.Value(c.Var);
                Say($"   back at {F(end)} {(ProbeSession.Same(end, current) ? "(where it started)" : "(NOT where it started)")}");
            }
            Say("\nall controls done");
            return 0;
        }
        finally
        {
            Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
            File.AppendAllText(resultsPath, string.Join("\n", report) + "\n\n");
            session.Out.WriteLine($"\nwritten: {resultsPath}");
        }
    }

    static async Task<ProbeResult> Steps(VarRun run, WriteControl c, IReadOnlyList<WriteStep> steps,
        Func<CancellationToken, Task<string?>> readLine, Action<string> say, CancellationToken ct)
    {
        var s = run.Session;
        // The restore checks these are back afterwards: only vars with an expectation (a lamp that must follow),
        // never a display that keeps its last number or a var that flickers on its own.
        run.TrackEffects(steps.SelectMany(st => st.Expect?.Keys ?? []).Distinct().Where(e => s.Value(e) is not null));
        var notes = new List<string>();
        var allStuck = true;
        foreach (var step in steps)
        {
            var prev = s.Value(c.Var);
            s.Out.WriteLine($"\n   next: write {c.Var} = {F(step.Value)} ({step.Means})");
            s.Out.WriteLine($"   watch: {step.Watch}");
            s.Out.Write("   Enter = write, s = skip the rest, q = stop > ");
            var answer = (await readLine(ct))?.Trim().ToLowerInvariant();
            if (answer is null or "q") throw new OperationCanceledException("stopped by the pilot");
            if (answer == "s") { notes.Add("rest skipped by the pilot"); say("   rest of this control skipped"); break; }

            var before = s.Snapshot();
            var latency = await run.Write(step.Value, ct);
            var reads = new List<double>();
            for (var t = 0; t < ReadBackMs; t += SampleMs)
            {
                await run.Delay(SampleMs, ct);
                s.Safety.Check(c.Var);
                if (s.Value(c.Var) is { } v) reads.Add(v);
            }
            if (!s.Sim.IsFresh(c.Var) || await s.Gql.SoftwareVersionAsync(ct) is null)
                throw new SafetyStopException("Fenix stopped answering (no fresh value, or GraphQL is silent)");

            var stuck = reads.Count > 0 && reads.All(v => ProbeSession.Same(v, step.Value));
            var odd = reads.Where(v => !ProbeSession.Same(v, step.Value) && !ProbeSession.Same(v, prev)).Distinct().ToList();
            var after = s.Snapshot();
            var effects = c.Effects.Where(e => before.ContainsKey(e) || after.ContainsKey(e))
                .Select(e => $"{e} {F(before.GetValueOrDefault(e, double.NaN))} -> {F(after.GetValueOrDefault(e, double.NaN))}").ToList();
            var expected = (step.Expect ?? new Dictionary<string, double>())
                .Select(kv => (kv.Key, Want: kv.Value, Got: s.Value(kv.Key))).ToList();
            var followed = expected.All(x => ProbeSession.Same(x.Got, x.Want));
            var others = s.Diff(before, after, c.Var).Where(n => !c.Effects.Contains(n)).Take(8).ToList();

            say($"   {F(prev)} -> {F(step.Value)} ({step.Means}): {(stuck ? $"STUCK for {F(ReadBackMs / 1000.0)} s" : $"did NOT stick: read {string.Join(" ", reads.Select(F).Distinct().Take(6))}")}" +
                (latency is { } l ? $", read back after {l:0} ms" : ", never read back"));
            if (effects.Count > 0) say($"     watched: {string.Join("; ", effects)}");
            if (expected.Count > 0) say($"     expected {string.Join(", ", expected.Select(x => $"{x.Key} {F(x.Want)}"))}: {(followed ? "yes" : "NO (" + string.Join(", ", expected.Where(x => !ProbeSession.Same(x.Got, x.Want)).Select(x => $"{x.Key} {F(x.Got)}")) + ")")}");
            if (others.Count > 0) say($"     also changed: {string.Join(", ", others)}");
            notes.Add($"{F(step.Value)}: {(stuck ? "stuck" : "not stuck")}{(expected.Count > 0 ? followed ? ", followed" : ", did not follow" : "")}");
            allStuck &= stuck;

            if (odd.Count > 0)
                throw new SafetyStopException($"unexpected: {c.Var} read {string.Join(", ", odd.Select(F))} after writing {F(step.Value)} (was {F(prev)})");
            if (c.StopWhenModelFails && (!stuck || !followed))
            {
                say("   the model does not hold: no other values are tried; the control is put back");
                break;
            }
        }
        return new ProbeResult
        {
            Var = c.Var, Phase = Phase.WriteTest, Test = "write-test", Original = run.Original,
            Result = allStuck ? "stuck" : "not-stuck", StoredOk = allStuck, Notes = string.Join("; ", notes),
        };
    }

    static string F(double v) => double.IsNaN(v) ? "?" : v.ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture);
    static string F(double? v) => v is { } x ? F(x) : "?";
}
