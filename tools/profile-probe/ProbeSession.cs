// One var at a time: safety check, record the original, checkpoint, probe, restore, verify the
// restore. On Ctrl+C, a safety stop or any error the current var is restored first, then the
// run stops.

using System.Diagnostics;
using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Sim;

namespace ProfileProbe;

/// <summary>Waits used by the tests. Scaled down in unit tests.</summary>
sealed record ProbeTiming
{
    public int ReadBackMs { get; init; } = 1000;
    public int SettleMs { get; init; } = 600;
    public int StepMs { get; init; } = 300;
    public int PressMs { get; init; } = 150;
    public int HoldMs { get; init; } = 500;
    public int BreakerSettleMs { get; init; } = 1500;
    public int EffectRestoreMs { get; init; } = 5000;
    public int NoiseSeconds { get; init; } = 30;
    public int PollMs { get; init; } = 5;
}

/// <summary>The probe could not put a var back. The run stops; this needs a human.</summary>
sealed class NotRestoredException(string message) : Exception(message);

sealed class ProbeSession(
    ISimAdapter sim,
    IFenixGraphQl gql,
    SafetyMonitor safety,
    Checkpoint checkpoint,
    ResultLog log,
    IReadOnlyList<string> watched,
    IReadOnlySet<string> noisy,
    ProbeTiming timing,
    TextWriter output)
{
    public ISimAdapter Sim => sim;
    public IFenixGraphQl Gql => gql;
    public SafetyMonitor Safety => safety;
    public ProbeTiming Timing => timing;
    public IReadOnlyList<string> Watched => watched;
    public IReadOnlySet<string> Noisy => noisy;
    public TextWriter Out => output;
    public Checkpoint Checkpoint => checkpoint;
    public ResultLog Log => log;
    /// <summary>The repo inputs (profile, review list), for tests that look up profile entries.</summary>
    public Inputs? Inputs { get; init; }

    /// <summary>Probes one var. Returns null if it was already done in an earlier run.</summary>
    public async Task<ProbeResult?> RunVar(Target target, Func<VarRun, CancellationToken, Task<ProbeResult>> test, CancellationToken ct)
    {
        if (checkpoint.IsDone(target.Phase, target.Name)) return null;
        ct.ThrowIfCancellationRequested();
        safety.Check(null);

        if (!await WaitReadable(target.Name))
            return Record(target, new ProbeResult { Var = target.Name, Phase = target.Phase, Test = target.Phase.ToString().ToLowerInvariant(), Result = "unresolved", Notes = "cannot read the var" });
        sim.TryGet(target.Name, out var original);
        if (!sim.Guard.IsAllowedValue(target.Name, original))
            return Record(target, new ProbeResult
            {
                Var = target.Name, Phase = target.Phase, Test = target.Phase.ToString().ToLowerInvariant(), Result = "skipped",
                Original = original, Notes = $"original value {original} is outside the values this test may write, nothing written",
            });

        var run = new VarRun(this, target, original);
        checkpoint.SetPending(run.ToPending());

        ProbeResult? result = null;
        Exception? failure = null;
        try
        {
            result = await test(run, ct);
        }
        catch (Exception e)
        {
            failure = e;
        }

        // Restore with no cancellation token: Ctrl+C must not stop the restore.
        var (restored, why) = await Restore(run);
        result ??= new ProbeResult
        {
            Var = target.Name, Phase = target.Phase, Test = target.Phase.ToString().ToLowerInvariant(), Original = original,
            Result = failure is OperationCanceledException ? "cancelled" : "error",
            Notes = failure is OperationCanceledException ? "stopped by Ctrl+C" : failure?.Message ?? "",
        };
        result.Restored = restored;
        if (run.RestoreNote is { } rn)
            result = result with
            {
                Result = "counter-suspected", ProposedClass = null, ProposedValues = null,
                ProposedVerifyLvar = null, ProposedVerifyGraphql = null, Notes = rn + "; " + result.Notes,
            };
        if (!restored) result = result with { Notes = (result.Notes + "; NOT RESTORED: " + why).TrimStart(';', ' ') };
        log.Add(result);

        // Not restored: the pending entry stays, so the next start (or `restore`) tries again.
        if (!restored) checkpoint.KeepPending(target.Name);
        else if (failure is null) checkpoint.Finish(target.Phase, target.Name, true);
        else checkpoint.ClearPending(true, target.Name); // stopped or failed: probe it again on resume

        output.WriteLine($"  {target.Name,-48} {result.Result,-12} {(restored ? "restored" : "NOT RESTORED")}  {Short(result.Notes)}");

        if (!restored) throw new NotRestoredException($"{target.Name} could not be restored: {why}");
        if (failure is not null) throw failure;
        return result;
    }

    ProbeResult Record(Target target, ProbeResult r)
    {
        log.Add(r);
        checkpoint.Finish(target.Phase, target.Name, true);
        output.WriteLine($"  {target.Name,-48} {r.Result,-12} {Short(r.Notes)}");
        return r;
    }

    static string Short(string s) => s.Length > 90 ? s[..87] + "..." : s;

    async Task<bool> WaitReadable(string name)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 2000)
        {
            if (sim.TryGet(name, out _)) return true;
            await Task.Delay(20);
        }
        return false;
    }

    /// <summary>Puts the var back and checks that it and its effect vars are back as they were.</summary>
    public async Task<(bool Ok, string Why)> Restore(VarRun run)
    {
        try
        {
            var name = run.Target.Name;
            if (run.Kind == RestoreKind.Counter)
            {
                // Press counters: never write the old number back (that is another change). Press
                // once more (+1, +1) if the effect is still toggled.
                if (!await run.EffectsBack(timing.SettleMs) && sim.TryGet(name, out var now))
                {
                    await run.WriteRaw(now + 1);
                    await Task.Delay(timing.PressMs);
                    await run.WriteRaw(now + 2);
                }
                var back = await run.EffectsBack(timing.EffectRestoreMs);
                sim.TryGet(name, out var v);
                var parity = (long)v % 2 == (long)run.Original % 2;
                return back && parity ? (true, "") : (false, back ? "press counter parity differs" : "effect vars differ: " + run.EffectDiff());
            }

            if (!Same(Value(name), run.Original))
            {
                await run.WriteRaw(run.Original);
                await run.WaitValue(run.Original, timing.ReadBackMs);
            }
            if (run.Kind == RestoreKind.PressAgain && !await run.EffectsBack(timing.SettleMs) && run.Original == 0)
            {
                // Edge-triggered button: the effect toggled, press once more.
                await run.WriteRaw(1);
                await Task.Delay(timing.PressMs);
                await run.WriteRaw(0);
                await run.WaitValue(0, timing.ReadBackMs);
            }
            var effectsBack = await run.EffectsBack(timing.EffectRestoreMs);
            // If aircraft power changed, an extra press cannot help and only adds writes.
            var powerChanged = SafetyVars.Power.Any(p => run.EffectStart.TryGetValue(p, out var was) && !Same(Value(p), was));
            if (!effectsBack && run.Kind == RestoreKind.Absolute
                && !powerChanged
                && sim.Guard.IsAllowedValue(name, run.Original + 1) && sim.Guard.IsAllowedValue(name, run.Original + 2))
            {
                // Found in the first run (yellow ELEC PUMP): some switches are press counters
                // (+1 press, +1 release). Writing the old number back is no press, so the effect
                // stays. One more press (+1, +1) puts it back with the same parity.
                await run.WriteRaw(run.Original + 1);
                await Task.Delay(timing.PressMs);
                await run.WriteRaw(run.Original + 2);
                await run.WaitValue(run.Original + 2, timing.ReadBackMs);
                if (await run.EffectsBack(timing.EffectRestoreMs))
                {
                    await Task.Delay(timing.StepMs);
                    if (run.EffectsBackNow())
                    {
                        run.RestoreNote = $"press counter: writing {run.Original} back did not undo the effect, one more press (+1, +1) did; the var now reads {Value(name)}";
                        return (true, "");
                    }
                }
                // The extra press did not help: put the original number back, then report.
                await run.WriteRaw(run.Original);
                await run.WaitValue(run.Original, timing.ReadBackMs);
                return (false, "effect vars differ (also after one more press): " + run.EffectDiff());
            }
            // Check again a moment later: the aircraft may flip a value back after the restore.
            await Task.Delay(timing.StepMs);
            if (!Same(Value(name), run.Original)) return (false, $"value is {Value(name)}, was {run.Original}");
            if (!effectsBack) return (false, "effect vars differ: " + run.EffectDiff());
            return (true, "");
        }
        catch (Exception e)
        {
            return (false, e.Message);
        }
    }

    public double? Value(string name) => sim.TryGet(name, out var v) ? v : null;

    public static bool Same(double? a, double? b) => a is { } x && b is { } y && Math.Abs(x - y) < 1e-6;

    public Dictionary<string, double> Snapshot()
    {
        var s = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var n in watched)
            if (sim.TryGet(n, out var v)) s[n] = v;
        return s;
    }

    /// <summary>
    /// Vars that are not Fenix-named (A320_*, A32X_*, LOD_*: model sound, animation, render state,
    /// e.g. the engine blades turning in the wind). They
    /// move without input and say nothing about a switch, so they never count as an effect.
    /// </summary>
    public static bool NotEvidence(string name) => !ProfileGen.Rules.IsFenix(name);

    /// <summary>Vars that differ between two snapshots, without vars that change on their own.</summary>
    public List<string> Diff(Dictionary<string, double> a, Dictionary<string, double> b, string self) =>
        b.Where(kv => kv.Key != self && !noisy.Contains(kv.Key) && !NotEvidence(kv.Key) && (!a.TryGetValue(kv.Key, out var old) || Math.Abs(old - kv.Value) > 1e-6))
            .Select(kv => kv.Key).OrderBy(n => n, StringComparer.Ordinal).ToList();
}

/// <summary>State of the var being probed, with helpers for the tests.</summary>
sealed class VarRun(ProbeSession session, Target target, double original)
{
    public Target Target => target;
    public double Original => original;
    public RestoreKind Kind { get; set; } = RestoreKind.Absolute;
    /// <summary>Set when the restore needed the press-counter fallback.</summary>
    public string? RestoreNote { get; set; }
    /// <summary>Vars that must be back at these values after the restore (paired lamps, latch vars).</summary>
    public Dictionary<string, double> EffectStart { get; } = new(StringComparer.Ordinal);
    public ProbeSession Session => session;
    public ProbeTiming Timing => session.Timing;

    public Pending ToPending() => new(target.Name, target.Phase, original, Kind, new(EffectStart));

    /// <summary>Starts watching effect vars for the restore check, and updates the checkpoint.</summary>
    public void TrackEffects(IEnumerable<string> names)
    {
        foreach (var n in names)
            if (session.Value(n) is { } v) EffectStart[n] = v;
        session.Checkpoint.SetPending(ToPending());
    }

    public void SetKind(RestoreKind kind)
    {
        Kind = kind;
        session.Checkpoint.SetPending(ToPending());
    }

    /// <summary>Writes, checks safety, and returns ms until the L: var showed the value (null = never within ReadBackMs).</summary>
    public async Task<double?> Write(double value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sw = Stopwatch.StartNew();
        await WriteRaw(value);
        var latency = await WaitValue(value, Timing.ReadBackMs, sw);
        session.Safety.Check(target.Name);
        return latency;
    }

    public Task WriteRaw(double value) => session.Sim.WriteAsync(target.Name, value);

    public async Task<double?> WaitValue(double value, int timeoutMs, Stopwatch? sw = null)
    {
        sw ??= Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (ProbeSession.Same(session.Value(target.Name), value)) return sw.Elapsed.TotalMilliseconds;
            await Task.Delay(Timing.PollMs);
        }
        return ProbeSession.Same(session.Value(target.Name), value) ? sw.Elapsed.TotalMilliseconds : null;
    }

    /// <summary>ms until any of the vars differs from its value in <paramref name="before"/>, or null.</summary>
    public async Task<double?> WaitChange(IReadOnlyCollection<string> vars, Dictionary<string, double> before, int timeoutMs, Stopwatch sw)
    {
        if (vars.Count == 0) return null;
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            foreach (var v in vars)
                if (session.Value(v) is { } now && before.TryGetValue(v, out var b) && Math.Abs(now - b) > 1e-6) return sw.Elapsed.TotalMilliseconds;
            await Task.Delay(Timing.PollMs);
        }
        return null;
    }

    /// <summary>
    /// Measured values that follow a change slowly (battery voltage recharging, hydraulic pressure
    /// decaying, animations). Recorded, but not required to be back for a restore to count.
    /// </summary>
    public static bool Settling(string name) =>
        ProbeSession.NotEvidence(name) || System.Text.RegularExpressions.Regex.IsMatch(name, @"^N_.*(VOLT|PRESS|QUANT|QTY|TEMP|FLOW|AMP|LOAD|BRIGHTNESS)|_Anim$|^A320_");

    public async Task<bool> EffectsBack(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (EffectStart.All(kv => Settling(kv.Key) || ProbeSession.Same(session.Value(kv.Key), kv.Value))) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            await Task.Delay(20);
        }
    }

    public bool EffectsBackNow() => EffectStart.All(kv => Settling(kv.Key) || ProbeSession.Same(session.Value(kv.Key), kv.Value));

    public string EffectDiff() =>
        string.Join(", ", EffectStart.Where(kv => !Settling(kv.Key) && !ProbeSession.Same(session.Value(kv.Key), kv.Value)).Select(kv => $"{kv.Key} {kv.Value} -> {session.Value(kv.Key)}"));

    public async Task<double?> GqlNumber(string path, CancellationToken ct) => await session.Gql.GetAsync(path, ct) as double?;

    /// <summary>Waits, checking safety every 100 ms, so a power loss is caught right away (not only after the next write).</summary>
    public async Task Delay(int ms, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            await Task.Delay((int)Math.Min(100, Math.Max(1, ms - sw.ElapsedMilliseconds)), ct);
            session.Safety.Check(target.Name);
        }
    }
}
