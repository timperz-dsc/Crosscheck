// The tests per brief item. Each runs inside ProbeSession.RunVar, which restores the var after it.

using System.Diagnostics;
using System.Globalization;

namespace ProfileProbe;

static class Probes
{
    static string F(double? d) => d is { } v ? v.ToString("0.###", CultureInfo.InvariantCulture) : "-";
    static string List(IEnumerable<string> names, int max = 8)
    {
        var l = names.ToList();
        return l.Count == 0 ? "none" : string.Join(" ", l.Take(max)) + (l.Count > max ? $" (+{l.Count - max})" : "");
    }

    public static string SwitchesPath(string name) => "system.switches." + name;

    /// <summary>Lamps, animation and latch vars that belong to a switch by name, if they exist.</summary>
    public static List<string> Related(ProbeSession s, string name, string? profileLamp = null)
    {
        var stem = name.StartsWith("S_", StringComparison.Ordinal) ? name[2..] : name;
        var names = new List<string> { "I_" + stem, $"I_{stem}_L", $"I_{stem}_U", name + "_Anim", name + "_LATCH" };
        if (name.EndsWith("_LATCH", StringComparison.Ordinal)) names.Add(name[..^6]);
        if (profileLamp is not null) names.Add(profileLamp);
        var watched = s.Watched.ToHashSet(StringComparer.Ordinal);
        return names.Distinct().Where(n => n != name && watched.Contains(n)).ToList();
    }

    static void TrackAt(VarRun run, Dictionary<string, double> start, IEnumerable<string> names)
    {
        foreach (var n in names)
            if (start.TryGetValue(n, out var v)) run.EffectStart[n] = v;
        run.SetKind(run.Kind); // saves the checkpoint
    }

    static ProbeResult Base(VarRun run, string test, string result) => new()
    {
        Var = run.Target.Name, Phase = run.Target.Phase, Test = test, Result = result, Original = run.Original,
    };

    // ------------------------------------------------------------------ 1 + 4: verified [0,1] guesses, lamp halves

    public static async Task<ProbeResult> Verified(VarRun run, CancellationToken ct)
    {
        var s = run.Session;
        var name = run.Target.Name;
        var t = run.Timing;
        var entry = s.Inputs?.Entry(name);
        var profileLamp = entry?.Verify?.Lvar;
        var related = Related(s, name, profileLamp);
        var lamps = related.Where(n => n.StartsWith("I_", StringComparison.Ordinal)).ToList();
        run.TrackEffects(related);

        var s0 = s.Snapshot();
        var g0 = await run.GqlNumber(SwitchesPath(name), ct);
        var other = run.Original == 1 ? 0 : 1;
        var sw = Stopwatch.StartNew();
        var latency = await run.Write(other, ct);
        var effectLatency = await run.WaitChange(lamps, s0, t.SettleMs, sw);
        var rest = t.SettleMs - (int)sw.ElapsedMilliseconds;
        if (rest > 0) await run.Delay(rest, ct);
        var s1 = s.Snapshot();
        var g1 = await run.GqlNumber(SwitchesPath(name), ct);
        var diff1 = s.Diff(s0, s1, name);

        // Is 2 a third position?
        var latency2 = await run.Write(2, ct);
        await run.Delay(t.SettleMs, ct);
        var s2 = s.Snapshot();
        var g2 = await run.GqlNumber(SwitchesPath(name), ct);
        var stored2 = ProbeSession.Same(s.Value(name), 2);
        var newIn2 = s.Diff(s1, s2, name).Where(v => s0.TryGetValue(v, out var a) && s1.TryGetValue(v, out var b) && s2.TryGetValue(v, out var c)
                                                     && Math.Abs(c - a) > 1e-6 && Math.Abs(c - b) > 1e-6).ToList();
        TrackAt(run, s0, diff1.Union(s.Diff(s0, s2, name)));

        var changedLamps = lamps.Where(l => diff1.Contains(l)).ToList();
        var gqlFollows = g0 is not null && g1 is not null && Math.Abs(g1.Value - g0.Value) > 1e-6;
        var data = new Dictionary<string, string>
        {
            ["gql"] = $"{F(g0)} -> {F(g1)} -> {F(g2)} (for {F(run.Original)} -> {other} -> 2)",
            ["changed"] = List(diff1, 12),
            ["two_stored"] = stored2 ? "yes" : $"no (Fenix set {F(s.Value(name))})",
            ["two_new_state"] = List(newIn2),
        };
        foreach (var l in lamps) data["lamp " + l] = diff1.Contains(l) ? "changed" : "unchanged";

        string? proposedLamp = null, proposedGql = null;
        if (profileLamp is not null && changedLamps.Contains(profileLamp)) proposedLamp = profileLamp;
        else if (changedLamps.FirstOrDefault(l => l.EndsWith("_L", StringComparison.Ordinal)) is { } lower) proposedLamp = lower;
        else if (changedLamps.FirstOrDefault() is { } anyLamp) proposedLamp = anyLamp;
        else if (gqlFollows && ProbeSession.Same(g1, other) && ProbeSession.Same(g0, run.Original)) proposedGql = SwitchesPath(name);

        // 4: lower-half rule. Which half actually changed?
        var stem = name[2..];
        if (profileLamp is not null && profileLamp == $"I_{stem}_L" && s.Watched.Contains($"I_{stem}_U"))
        {
            var l = diff1.Contains($"I_{stem}_L");
            var u = diff1.Contains($"I_{stem}_U");
            s.Log.Add(new ProbeResult
            {
                Var = name, Phase = run.Target.Phase, Test = "lamp-half", Original = run.Original,
                Result = l && !u ? "lower" : u && !l ? "upper" : l && u ? "both" : "neither",
                EffectOk = l, LatencyMs = effectLatency,
                ProposedVerifyLvar = l ? $"I_{stem}_L" : u ? $"I_{stem}_U" : null,
                Notes = $"I_{stem}_L {(l ? "changed" : "unchanged")}, I_{stem}_U {(u ? "changed" : "unchanged")}",
            });
        }

        var effectSeen = changedLamps.Count > 0 || gqlFollows || diff1.Count > 0;
        if (latency is null)
            return Base(run, "verified-binary", "unresolved") with { StoredOk = false, Data = data, Notes = "the L: var did not take the other value" };
        // A third position needs evidence from the switch itself: GraphQL showing a new number,
        // or its own lamp/animation in a new state. Other vars still settling after the first
        // write (a power-down cascade, for example) are not evidence.
        // GraphQL echoing exactly 2 is not evidence (it may mirror any number); a new mapped number is.
        var gqlNew = g2 is not null && !ProbeSession.Same(g2, g0) && !ProbeSession.Same(g2, g1) && !ProbeSession.Same(g2, 2);
        var gqlEcho = ProbeSession.Same(g2, 2);
        var gqlSaysNo = g2 is not null && !gqlNew && !gqlEcho;
        var ownNew = newIn2.Intersect(related).ToList();
        if (stored2 && !gqlSaysNo && (gqlNew || ownNew.Count > 0))
            return Base(run, "verified-binary", "rejected") with
            {
                StoredOk = true, EffectOk = effectSeen, LatencyMs = latency, Data = data,
                Notes = $"2 is a distinct state ({(gqlNew ? $"GraphQL {F(g2)}" : List(ownNew, 4))}); goes to the position test",
            };
        if (!effectSeen)
            return Base(run, "verified-binary", "unresolved") with
            {
                StoredOk = true, EffectOk = false, LatencyMs = latency, Data = data,
                Notes = "no effect visible at the gate, only the L: echo; GraphQL " + (g0 is null ? "has no mirror" : "did not change"),
            };
        var how = changedLamps.Count > 0 ? $"lamp {List(changedLamps, 3)} in {F(effectLatency)} ms" : gqlFollows ? "GraphQL followed" : $"other vars changed ({List(diff1, 3)})";
        var twoNote = stored2 ? (g2 is not null && ProbeSession.Same(g2, 2) ? "2 is stored and echoed by GraphQL but changes nothing else" : "2 is stored but changes nothing else") : "Fenix does not keep 2";
        return Base(run, "verified-binary", "confirmed") with
        {
            StoredOk = true, EffectOk = true, LatencyMs = latency, Data = data,
            ProposedClass = "verified", ProposedValues = [0, 1], ProposedVerifyLvar = proposedLamp, ProposedVerifyGraphql = proposedGql,
            Notes = $"{how}; {twoNote}",
        };
    }

    // ------------------------------------------------------------------ 2: momentary vs latching

    public static async Task<ProbeResult> Button(VarRun run, CancellationToken ct)
    {
        var s = run.Session;
        var name = run.Target.Name;
        var t = run.Timing;
        var related = Related(s, name);
        run.TrackEffects(related);
        var o = run.Original;
        var path = SwitchesPath(name);

        if (o >= 2 || (o != 0 && o != 1))
        {
            // Found in the first run: S_FCU_EFIS2_ND_MODE rests at 3 and is a rotary. Treating
            // every rest value >= 2 as a press counter moved it to 5. Only the known counters
            // (MASTER CAUTION/WARNING) get the counter test; anything else is not a 0/1 button.
            if (!name.Contains("MASTER_CAUTION") && !name.Contains("MASTER_WARNING"))
                return Base(run, "momentary-or-latching", "unresolved") with
                {
                    StoredOk = null, EffectOk = null,
                    Notes = $"rest value {F(o)}: not a 0/1 button (multi-position or counter); nothing written",
                };
            // A press counter (like MASTER CAUTION): +1 press, +1 release.
            run.SetKind(RestoreKind.Counter);
            var c0 = s.Snapshot();
            var cg0 = await run.GqlNumber(path, ct);
            var lat = await run.Write(o + 1, ct);
            await run.Delay(t.HoldMs, ct);
            var c1 = s.Snapshot();
            await run.Write(o + 2, ct);
            await run.Delay(t.HoldMs, ct);
            var c2 = s.Snapshot();
            var cg2 = await run.GqlNumber(path, ct);
            var cd = s.Diff(c0, c1, name).Union(s.Diff(c0, c2, name)).ToList();
            TrackAt(run, c0, cd);
            return Base(run, "momentary-or-latching", "counter") with
            {
                StoredOk = lat is not null, EffectOk = cd.Count > 0, LatencyMs = lat,
                ProposedClass = "pulse", ProposedCounter = true,
                Notes = $"rest value {F(o)}: press counter (+1 press, +1 release); GraphQL {F(cg0)} -> {F(cg2)}; changed: {List(cd, 5)}",
            };
        }

        var restValue = o;
        var pressValue = o == 0 ? 1 : 0;
        var s0 = s.Snapshot();
        var g0 = await run.GqlNumber(path, ct);
        var sw = Stopwatch.StartNew();
        var latency = await run.Write(pressValue, ct);
        double? selfReset = null;
        while (sw.ElapsedMilliseconds < t.HoldMs)
        {
            if (selfReset is null && latency is not null && ProbeSession.Same(s.Value(name), restValue)) selfReset = sw.Elapsed.TotalMilliseconds;
            await run.Delay(20, ct);
        }
        var s1 = s.Snapshot();
        var g1 = await run.GqlNumber(path, ct);
        if (selfReset is null) await run.Write(restValue, ct);
        await run.Delay(t.HoldMs, ct);
        var s2 = s.Snapshot();
        var g2 = await run.GqlNumber(path, ct);

        var onPress = s.Diff(s0, s1, name);
        var persisted = onPress.Where(v => s2.TryGetValue(v, out var b) && s0.TryGetValue(v, out var a) && Math.Abs(a - b) > 1e-6).ToList();
        var reverted = onPress.Except(persisted).ToList();
        var onRelease = s.Diff(s0, s2, name).Except(onPress).ToList();
        TrackAt(run, s0, onPress.Union(onRelease));
        var lamp = related.FirstOrDefault(r => persisted.Contains(r) || reverted.Contains(r));
        var data = new Dictionary<string, string>
        {
            ["gql"] = $"{F(g0)} -> {F(g1)} -> {F(g2)}",
            ["on_press"] = List(onPress, 12),
            ["persisted"] = List(persisted, 12),
            ["on_release"] = List(onRelease, 12),
        };
        var gqlNote = g0 is null ? "no GraphQL mirror" : $"GraphQL {F(g0)} -> {F(g1)} -> {F(g2)}";

        if (latency is null)
            return Base(run, "momentary-or-latching", "unresolved") with { StoredOk = false, Data = data, Notes = "the L: var did not take the value" };
        if (selfReset is not null)
        {
            return Base(run, "momentary-or-latching", "pulse") with
            {
                StoredOk = true, EffectOk = onPress.Count > 0, LatencyMs = latency, Data = data,
                ProposedClass = "pulse", ProposedVerifyLvar = lamp is not null && lamp.StartsWith("I_") ? lamp : null,
                Notes = $"Fenix reset it to {F(restValue)} after {F(selfReset)} ms (self-resetting); {gqlNote}",
            };
        }
        if (o == 0 && persisted.Count > 0 && reverted.Count == 0)
        {
            run.SetKind(RestoreKind.PressAgain);
            return Base(run, "momentary-or-latching", "pulse") with
            {
                StoredOk = true, EffectOk = true, LatencyMs = latency, Data = data,
                ProposedClass = "pulse", ProposedVerifyLvar = lamp is not null && lamp.StartsWith("I_") ? lamp : null,
                Notes = $"edge-triggered: the press toggled {List(persisted, 4)} and it stayed after release (Fenix never resets the var); {gqlNote}",
            };
        }
        if (reverted.Count > 0 && persisted.Count == 0)
        {
            return Base(run, "momentary-or-latching", "follows-value") with
            {
                StoredOk = true, EffectOk = true, LatencyMs = latency, Data = data,
                ProposedClass = "verified", ProposedValues = [0, 1], ProposedVerifyLvar = lamp is not null && lamp.StartsWith("I_") ? lamp : null,
                Notes = $"effect follows the value ({List(reverted, 4)}): latching, or momentary with an effect only while held; {gqlNote}",
            };
        }
        if (onPress.Count == 0 && onRelease.Count > 0)
            return Base(run, "momentary-or-latching", "unresolved") with
            {
                StoredOk = true, EffectOk = true, LatencyMs = latency, Data = data,
                Notes = $"effect only on release ({List(onRelease, 4)}); needs a look",
            };
        if (onPress.Count > 0)
            return Base(run, "momentary-or-latching", "unresolved") with
            {
                StoredOk = true, EffectOk = true, LatencyMs = latency, Data = data,
                Notes = $"mixed: {List(persisted, 3)} stayed, {List(reverted, 3)} went back",
            };
        return Base(run, "momentary-or-latching", "unresolved") with
        {
            StoredOk = true, EffectOk = false, LatencyMs = latency, Data = data,
            Notes = $"no visible effect at the gate; {gqlNote}; Fenix kept the value (not self-resetting)",
        };
    }

    // ------------------------------------------------------------------ 3: position count

    public static async Task<ProbeResult> Positions(VarRun run, CancellationToken ct)
    {
        var s = run.Session;
        var name = run.Target.Name;
        var t = run.Timing;
        var path = SwitchesPath(name);
        if (Targets.NeedsDryHydraulics(name) && await s.Safety.HydraulicsDry(ct) is not true)
            return Base(run, "positions", "skipped") with { Notes = "hydraulics pressurised or unknown: lever not moved" };
        run.TrackEffects(Related(s, name));

        var baseSnap = s.Snapshot();
        var snaps = new Dictionary<int, Dictionary<string, double>>();
        var gq = new Dictionary<int, double?>();
        var stored = new Dictionary<int, bool>();
        var kept = new Dictionary<int, double?>();
        double? latency = null;
        var quiet = 0;
        foreach (var v in Targets.Steps.Select(x => (int)x))
        {
            var lat = await run.Write(v, ct);
            latency ??= lat;
            await run.Delay(t.StepMs, ct);
            snaps[v] = s.Snapshot();
            gq[v] = await run.GqlNumber(path, ct);
            kept[v] = s.Value(name);
            stored[v] = ProbeSession.Same(kept[v], v);
            if (v > 0)
            {
                var changed = s.Diff(snaps[v - 1], snaps[v], name).Count > 0 || !ProbeSession.Same(gq[v], gq[v - 1]) && gq[v] is not null;
                quiet = changed || !stored[v] ? 0 : quiet + 1;
                if (quiet >= 2 && v > Math.Max(run.Original, 1)) break;
            }
        }
        var steps = snaps.Keys.OrderBy(k => k).ToList();
        var changedAny = steps.SelectMany(v => s.Diff(baseSnap, snaps[v], name)).Distinct().ToList();
        TrackAt(run, baseSnap, changedAny);
        string Sig(int v) => string.Join("|", changedAny.Select(n => snaps[v].TryGetValue(n, out var x) ? F(x) : "?"));

        var data = new Dictionary<string, string>
        {
            ["stored"] = string.Join(" ", steps.Select(v => $"{v}:{(stored[v] ? "yes" : "kept " + F(kept[v]))}")),
            ["gql"] = string.Join(" ", steps.Select(v => $"{v}:{F(gq[v])}")),
            ["effect_vars"] = List(changedAny, 12),
        };

        int[] positions;
        string source;
        var clamped = steps.Where(v => !stored[v]).ToList();
        if (clamped.Count > 0 && steps.Any(v => stored[v]))
        {
            positions = steps.Where(v => stored[v]).ToArray();
            source = $"Fenix does not keep {string.Join(",", clamped)} (sets {string.Join(",", clamped.Select(v => F(kept[v])).Distinct())})";
        }
        else if (changedAny.Count > 0 && LastDistinct(steps, Sig) is { } k && k > 0)
        {
            positions = Enumerable.Range(0, k + 1).ToArray();
            source = $"effect/animation vars ({List(changedAny, 4)}) stop changing after {k}";
        }
        else if (steps.All(v => gq[v] is null))
            return Base(run, "positions", "unresolved") with
            {
                StoredOk = true, LatencyMs = latency, Data = data,
                Notes = "no source of truth: no GraphQL mirror, no effect or animation var changed, Fenix keeps every value",
            };
        else if (steps.All(v => ProbeSession.Same(gq[v], v)))
            return Base(run, "positions", "unresolved") with
            {
                StoredOk = true, LatencyMs = latency, Data = data,
                Notes = "no source of truth: GraphQL echoes every value 0-7, nothing else changed",
            };
        else if (LastDistinct(steps, v => F(gq[v])) is { } kg && kg > 0)
        {
            positions = Enumerable.Range(0, kg + 1).ToArray();
            source = $"GraphQL stops changing after {kg}";
        }
        else
            return Base(run, "positions", "unresolved") with { StoredOk = true, LatencyMs = latency, Data = data, Notes = "no position changed anything visible" };

        var top = positions.Max();
        var gqlSame = positions.All(v => gq.TryGetValue(v, out var g) && ProbeSession.Same(g, v)) && gq.Keys.Where(v => v > top).All(v => !ProbeSession.Same(gq[v], v));
        var gqlMapped = positions.All(v => gq.TryGetValue(v, out var g) && g is not null) && !positions.All(v => ProbeSession.Same(gq[v], v));
        var notes = $"positions {string.Join(",", positions)}; source: {source}";
        if (gqlMapped) notes += $"; GraphQL uses other numbers ({string.Join(" ", positions.Select(v => $"{v}->{F(gq[v])}"))})";
        return Base(run, "positions", "positions") with
        {
            StoredOk = true, EffectOk = true, LatencyMs = latency, Data = data,
            ProposedClass = "verified", ProposedValues = positions.Select(p => (double)p).ToArray(),
            ProposedVerifyGraphql = gqlSame ? path : null,
            Notes = notes,
        };
    }

    /// <summary>Highest step whose signature differs from the step before, if every step up to it differs.</summary>
    static int? LastDistinct(List<int> steps, Func<int, string> sig)
    {
        int? last = null;
        for (var i = 1; i < steps.Count; i++)
        {
            if (sig(steps[i]) != sig(steps[i - 1])) { if (last is null ? i == 1 : last == steps[i - 1]) last = steps[i]; else return null; }
        }
        return last;
    }

    // ------------------------------------------------------------------ 5: encoders

    public static async Task<ProbeResult> Encoder(VarRun run, CancellationToken ct)
    {
        var s = run.Session;
        var name = run.Target.Name;
        var t = run.Timing;
        var o = run.Original;
        static bool IsN(string n) => n.StartsWith("N_", StringComparison.Ordinal);

        var s0 = s.Snapshot();
        var lat = await run.Write(o + 1, ct);
        await run.Delay(t.SettleMs, ct);
        var s1 = s.Snapshot();
        await run.Write(o, ct);
        await run.Delay(t.SettleMs, ct);
        var s2 = s.Snapshot();
        await run.Write(o - 1, ct);
        await run.Delay(t.SettleMs, ct);
        var s3 = s.Snapshot();
        await run.Write(o, ct);
        await run.Delay(t.SettleMs, ct);
        var s4 = s.Snapshot();

        var plus = s.Diff(s0, s1, name).Where(IsN).ToList();
        var minus = s.Diff(s0, s3, name).Where(IsN).ToList();
        var back = s.Diff(s0, s4, name);
        TrackAt(run, s0, plus.Union(minus));
        var candidates = plus.Intersect(minus).Where(n => !back.Contains(n)).ToList();
        var data = new Dictionary<string, string>
        {
            ["plus"] = string.Join(" ", plus.Select(n => $"{n} {F(s0.GetValueOrDefault(n))}->{F(s1.GetValueOrDefault(n))}")),
            ["minus"] = string.Join(" ", minus.Select(n => $"{n} {F(s0.GetValueOrDefault(n))}->{F(s3.GetValueOrDefault(n))}")),
            ["not_back"] = List(back.Where(IsN)),
        };
        if (candidates.Count == 0)
            return Base(run, "encoder-display", "unresolved") with
            {
                StoredOk = lat is not null, EffectOk = false, LatencyMs = lat, Data = data,
                Notes = "no N_ var followed +1 and -1 (display off, STD baro, or no display var)",
            };
        var review = s.Inputs?.Review.FirstOrDefault(r => r.Name == name)?.Reason ?? "";
        var pick = candidates.FirstOrDefault(c => review.Contains(c) && c.EndsWith("_HPA", StringComparison.Ordinal))
                   ?? candidates.FirstOrDefault(c => review.Contains(c)) ?? candidates[0];
        return Base(run, "encoder-display", "display") with
        {
            StoredOk = lat is not null, EffectOk = true, LatencyMs = lat, Data = data,
            ProposedClass = "delta", ProposedDisplay = pick,
            Notes = candidates.Count == 1 ? $"{pick} follows +1/-1" : $"{pick} chosen; also changed: {List(candidates.Where(c => c != pick))}",
        };
    }

    // ------------------------------------------------------------------ 6: breakers

    public static readonly string[] BreakerGqlKinds = ["switches", "gates", "numerical", "breakers", "cb", "circuitBreakers"];

    public static async Task<ProbeResult> Breaker(VarRun run, CancellationToken ct)
    {
        var s = run.Session;
        var name = run.Target.Name;
        var t = run.Timing;
        var o = run.Original;

        async Task<string> Gql() =>
            string.Join(" ", await Task.WhenAll(BreakerGqlKinds.Select(async k => $"{k}:{F(await run.GqlNumber($"system.{k}.{name}", ct))}")));

        var s0 = s.Snapshot();
        var gq0 = await Gql();
        var pulled = o >= 1 ? 0 : 1;
        var lat = await run.Write(pulled, ct);
        await run.Delay(t.BreakerSettleMs, ct);
        var s1 = s.Snapshot();
        var gq1 = await Gql();
        await run.Write(o, ct);
        await run.Delay(t.BreakerSettleMs, ct);
        var s2 = s.Snapshot();
        var effects = s.Diff(s0, s1, name);
        var notBack = s.Diff(s0, s2, name);
        var data = new Dictionary<string, string> { ["gql_before"] = gq0, ["gql_pulled"] = gq1, ["effects"] = List(effects, 20) };

        if (effects.Count > 0)
        {
            TrackAt(run, s0, effects.Union(notBack));
            return Base(run, "breaker", "breaker-01") with
            {
                StoredOk = lat is not null, EffectOk = true, LatencyMs = lat, Data = data,
                Notes = $"{F(pulled)} = pulled: {effects.Count} vars reacted ({List(effects, 6)}), back after {F(o)}",
            };
        }

        // Nothing reacted to an absolute 0/1: try it as a counter (+1).
        await run.Write(o + 1, ct);
        await run.Delay(t.BreakerSettleMs, ct);
        var s3 = s.Snapshot();
        await run.Write(o, ct);
        await run.Delay(t.BreakerSettleMs, ct);
        var counterEffects = s.Diff(s0, s3, name);
        TrackAt(run, s0, counterEffects);
        data["counter_effects"] = List(counterEffects, 20);
        if (counterEffects.Count > 0)
            return Base(run, "breaker", "breaker-counter") with
            {
                StoredOk = lat is not null, EffectOk = true, LatencyMs = lat, Data = data,
                Notes = $"0/1 changed nothing, +1 did ({List(counterEffects, 6)}): counter semantics",
            };
        return Base(run, "breaker", "no-effect") with
        {
            StoredOk = lat is not null, EffectOk = false, LatencyMs = lat, Data = data,
            Notes = $"neither {F(pulled)} nor +1 changed any watched var; GraphQL {gq0}",
        };
    }

    // ------------------------------------------------------------------ supervised a: takeover buttons

    public static async Task<ProbeResult> Takeover(VarRun run, CancellationToken ct)
    {
        var s = run.Session;
        var name = run.Target.Name;
        var s0 = s.Snapshot();
        var lat = await run.Write(1, ct);
        var held = new List<string>();
        for (var i = 0; i < 10; i++) { await run.Delay(100, ct); held.Add(F(s.Value(name))); }
        var s1 = s.Snapshot();
        await run.Write(0, ct);
        var after = new List<string>();
        for (var i = 0; i < 10; i++) { await run.Delay(100, ct); after.Add(F(s.Value(name))); }
        var s2 = s.Snapshot();
        var whileHeld = s.Diff(s0, s1, name);
        var stayed = s.Diff(s0, s2, name);
        TrackAt(run, s0, whileHeld.Union(stayed));
        var ok = held.All(v => v == "1") && after.All(v => v == "0");
        return Base(run, "takeover-hold-1s", ok ? "momentary-held" : "unexpected") with
        {
            StoredOk = lat is not null, EffectOk = ok, LatencyMs = lat,
            Data = new() { ["held"] = string.Join(" ", held), ["after"] = string.Join(" ", after), ["side_effects_held"] = List(whileHeld, 20), ["side_effects_after"] = List(stayed, 20) },
            Notes = $"read {string.Join(",", held.Distinct())} while held, {string.Join(",", after.Distinct())} after; side effects while held: {List(whileHeld, 5)}; after release: {List(stayed, 5)}",
        };
    }

    // ------------------------------------------------------------------ supervised b: hold a value while the pilot reads the ND

    public static Func<VarRun, CancellationToken, Task<ProbeResult>> Hold(double value, int seconds, string releaseFile) => async (run, ct) =>
    {
        if (File.Exists(releaseFile)) File.Delete(releaseFile);
        var lat = await run.Write(value, ct);
        var g = await run.GqlNumber(SwitchesPath(run.Target.Name), ct);
        run.Session.Out.WriteLine($"  holding {run.Target.Name} = {F(value)} (GraphQL {F(g)}) for up to {seconds} s; `release` ends it");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds && !File.Exists(releaseFile))
        {
            await run.Delay(200, ct);
            run.Session.Safety.Check(run.Target.Name);
        }
        if (File.Exists(releaseFile)) File.Delete(releaseFile);
        return Base(run, $"hold-{F(value)}", "held") with { StoredOk = lat is not null, LatencyMs = lat, Notes = $"held {sw.Elapsed.TotalSeconds:0} s; GraphQL {F(g)}" };
    };
}
