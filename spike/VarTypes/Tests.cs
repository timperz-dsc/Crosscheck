// The VarTypes measurement suite: 10 iterations per control, every value read back from up to three sources:
//   lvar    - the same L: var via SimConnect
//   effect  - a different L: var that shows the aircraft reacted (FCU value, ECAM page light)
//   gql     - Fenix's own view via GraphQL, timed both via the subscription and via HTTP polling
// Everything that is changed is restored at the end, also on Ctrl+C.

using System.Globalization;
using System.Text;

static class Tests
{
    const int Iterations = 10;
    const double TimeoutMs = 2000;
    const int SettleMs = 300;

    record Check(string Var, Func<double, bool> Ok);
    record GqlCheck(string Name, Func<double, bool> Ok);
    record Sample(string Test, string Var, int Iteration, double Written,
        double? LvarMs, double? EffectMs, double? GqlSubMs, double? GqlHttpMs, bool Ok);

    static Sim sim = null!;
    static Gql gql = null!;
    static HttpPoller poller = null!;
    static readonly List<Sample> Samples = new();

    static readonly string[] Rotaries = { "L:S_OH_PNEUMATIC_PACK_FLOW", "L:S_FCU_EFIS1_ND_MODE", "L:S_FCU_EFIS1_ND_ZOOM" };
    static readonly int[] RotaryPositions = { 3, 5, 6 };
    static readonly string[] Covers = { "L:S_OH_HYD_RAT_MAN_ON_Cover", "L:S_OH_ELEC_IDG1_Cover" };
    static readonly string[] Guarded = { "L:S_OH_HYD_RAT_MAN_ON", "L:S_OH_ELEC_IDG1" }; // must never change
    static readonly string[] Encoders = { "L:E_FCU_ALTITUDE", "L:E_FCU_HEADING", "L:E_FCU_SPEED" };
    static readonly string[] EncoderValues = { "L:N_FCU_ALTITUDE", "L:N_FCU_HEADING", "L:N_FCU_SPEED" };
    static readonly string[] Analogs = { "L:A_OH_PNEUMATIC_COCKPIT_TEMP", "L:A_DISPLAY_BRIGHTNESS_CO" };
    static readonly double[] AnalogValues = { 0, 0.25, 0.5, 0.75, 1, 0.1, 0.9, 0.33, 0.66, 0.05 };
    static readonly string[] EcamButtons = { "L:S_ECAM_ENGINE", "L:S_ECAM_HYD" };
    static readonly string[] EcamLights = { "L:I_ECAM_ENGINE", "L:I_ECAM_HYD" };
    const string MasterCaution = "L:S_MIP_MASTER_CAUTION_CAPT";

    public static async Task<int> Run(CancellationToken ct)
    {
        var vars = Rotaries.Concat(Covers).Concat(Guarded).Concat(Encoders).Concat(EncoderValues)
            .Concat(Analogs).Concat(EcamButtons).Concat(EcamLights).Append(MasterCaution).ToArray();
        sim = new Sim(vars);
        await sim.WaitForValues();
        if (sim.Received.Any(r => !r)) { Console.Error.WriteLine("Not all L: vars delivered a value; is the Fenix loaded?"); return 1; }

        var originals = vars.ToDictionary(v => v, v => sim.Values[sim.IndexOf(v)]);
        Console.WriteLine("Original values:");
        foreach (var (v, value) in originals) Console.WriteLine($"  {v,-32} {value.ToString(CultureInfo.InvariantCulture)}");

        gql = new Gql();
        poller = new HttpPoller();
        using var bg = new CancellationTokenSource();
        var sub = gql.Subscribe(vars.Select(Mirror).OfType<string>().Distinct().ToArray(), bg.Token);
        var poll = poller.Run(bg.Token);
        await Gql.Get("system.switches.S_OH_ELEC_BAT1"); // warm up HTTP
        await Task.Delay(2500); // subscription delivers its first values after ~2 s

        try
        {
            for (int i = 0; i < Rotaries.Length && !ct.IsCancellationRequested; i++) await Rotary(Rotaries[i], RotaryPositions[i], ct);
            for (int i = 0; i < Covers.Length && !ct.IsCancellationRequested; i++) await Cover(Covers[i], Guarded[i], ct);
            for (int i = 0; i < Encoders.Length && !ct.IsCancellationRequested; i++) await Encoder(Encoders[i], EncoderValues[i], ct);
            foreach (var a in Analogs) if (!ct.IsCancellationRequested) await Analog(a, ct);
            for (int i = 0; i < EcamButtons.Length && !ct.IsCancellationRequested; i++) await Ecam(EcamButtons[i], EcamLights[i], ct);
            if (!ct.IsCancellationRequested) await MasterCautionTest(ct);
        }
        catch (GuardViolation e)
        {
            Console.Error.WriteLine($"ABORTED: {e.Message}");
        }
        finally
        {
            await Restore(originals);
            bg.Cancel();
            try { await Task.WhenAll(sub, poll); } catch (OperationCanceledException) { }
            catch (Exception e) { Console.Error.WriteLine($"Background task: {e.Message}"); }
            sim.Dispose();
        }

        Report();
        return 0;
    }

    // ---- tests ---------------------------------------------------------------------------------------

    static async Task Rotary(string v, int positions, CancellationToken ct)
    {
        Console.WriteLine($"\n{v}: {positions} positions");
        int idx = sim.IndexOf(v);
        for (int it = 1; it <= Iterations && !ct.IsCancellationRequested; it++)
        {
            int start = (int)sim.Values[idx];
            for (int step = 1; step <= positions; step++)
            {
                int p = (start + step) % positions;
                var mirror = Mirror(v)!;
                await Measure("rotary", it, v, p, null, new GqlCheck(mirror, g => g == p));
            }
        }
    }

    static async Task Cover(string cover, string guarded, CancellationToken ct)
    {
        Console.WriteLine($"\n{cover}: open / close (guarded switch {guarded} must not change)");
        double guardedBefore = sim.Values[sim.IndexOf(guarded)];
        for (int it = 1; it <= Iterations && !ct.IsCancellationRequested; it++)
        {
            foreach (var value in new[] { 1.0, 0.0 })
            {
                await Measure("cover", it, cover, value, null, null);
                if (sim.Values[sim.IndexOf(guarded)] != guardedBefore)
                    throw new GuardViolation($"{guarded} changed to {sim.Values[sim.IndexOf(guarded)]} while moving {cover}");
            }
        }
    }

    static async Task Encoder(string e, string n, CancellationToken ct)
    {
        Console.WriteLine($"\n{e}: +1 / -1 detent, read back via {n}");
        int ei = sim.IndexOf(e), ni = sim.IndexOf(n);
        for (int it = 1; it <= Iterations && !ct.IsCancellationRequested; it++)
        {
            double counter = sim.Values[ei], before = sim.Values[ni];
            await Measure("encoder", it, e, counter + 1, new Check(n, x => x != before), new GqlCheck(Mirror(n)!, g => g != before));
            double after = sim.Values[ni];
            await Measure("encoder", it, e, counter, new Check(n, x => x == before), new GqlCheck(Mirror(n)!, g => g == before));
            if (it == 1) Console.WriteLine($"  one detent: {before} -> {after} -> {sim.Values[ni]}");
        }
    }

    static async Task Analog(string a, CancellationToken ct)
    {
        Console.WriteLine($"\n{a}: values 0..1, GraphQL shows value x 1024");
        for (int it = 1; it <= Iterations && !ct.IsCancellationRequested; it++)
        {
            double value = AnalogValues[it - 1];
            await Measure("analog", it, a, value, null, new GqlCheck(Mirror(a)!, g => Math.Abs(g - value * 1024) <= 1));
        }
    }

    static async Task Ecam(string button, string light, CancellationToken ct)
    {
        Console.WriteLine($"\n{button}: press (1, 0 after 150 ms) toggles {light}");
        int li = sim.IndexOf(light);
        for (int it = 1; it <= Iterations && !ct.IsCancellationRequested; it++)
        {
            for (int press = 0; press < 2; press++)
            {
                double expected = sim.Values[li] == 0 ? 1 : 0;
                await Measure("momentary", it, button, 1, new Check(light, x => x == expected),
                    new GqlCheck(Mirror(light)!, g => g == expected), releaseAfterMs: 150);
            }
        }
    }

    static async Task MasterCautionTest(CancellationToken ct)
    {
        Console.WriteLine($"\n{MasterCaution}: press counter, odd = held; +1 (hold 700 ms) then +1");
        int idx = sim.IndexOf(MasterCaution);
        for (int it = 1; it <= Iterations && !ct.IsCancellationRequested; it++)
        {
            double down = sim.Values[idx] + 1;
            await Measure("momentary", it, MasterCaution, down, null,
                new GqlCheck(Mirror(MasterCaution)!, g => g == 1), releaseAfterMs: 700, releaseValue: down + 1);
        }
    }

    // ---- measurement ---------------------------------------------------------------------------------

    static async Task Measure(string test, int it, string v, double value, Check? effect, GqlCheck? g,
        int releaseAfterMs = -1, double releaseValue = 0)
    {
        int idx = sim.IndexOf(v);
        int effectIdx = effect == null ? -1 : sim.IndexOf(effect.Var);
        if (g != null) poller.Watch(g.Name);
        await Task.Delay(50); // let the poller pick up the new name

        double t0 = await sim.Write(idx, value);
        double? lvar = null, eff = null, sub = null, http = null;
        bool released = releaseAfterMs < 0;
        while (Clock.Now - t0 < TimeoutMs)
        {
            if (!released && Clock.Now - t0 >= releaseAfterMs) { await sim.Write(idx, releaseValue); released = true; }
            lvar ??= Math.Abs(sim.Values[idx] - value) < 1e-6 ? sim.ChangedAt[idx] - t0 : null;
            if (effect != null) eff ??= effect.Ok(sim.Values[effectIdx]) ? sim.ChangedAt[effectIdx] - t0 : null;
            if (g != null)
            {
                sub ??= Matched(gql.Values, gql.ChangedAt, g, t0);
                http ??= Matched(poller.Values, poller.ChangedAt, g, t0);
            }
            if (released && lvar != null && (effect == null || eff != null) && (g == null || (sub != null && http != null))) break;
            await Task.Delay(1);
        }
        if (!released) await sim.Write(idx, releaseValue);
        poller.Watch();

        // A momentary var is released before the subscription sees it, so only the effect and HTTP count there.
        bool ok = lvar != null && (effect == null || eff != null) && (g == null || http != null || sub != null);
        var s = new Sample(test, v, it, value, lvar, eff, sub, http, ok);
        Samples.Add(s);
        Console.WriteLine($"  {it,2} {v,-30} = {value.ToString(CultureInfo.InvariantCulture),-6} lvar {Ms(lvar)}  effect {Ms(eff, effect == null)}  gql sub {Ms(sub, g == null)}  http {Ms(http, g == null)}{(ok ? "" : "  FAIL")}");
        await Task.Delay(SettleMs);
    }

    static double? Matched(IDictionary<string, string> values, IDictionary<string, double> changedAt, GqlCheck g, double t0)
    {
        var n = Gql.AsNumber(values.TryGetValue(g.Name, out var raw) ? raw : null);
        if (n == null || !g.Ok(n.Value)) return null;
        double at = changedAt.TryGetValue(g.Name, out var t) ? t : 0;
        return at >= t0 ? at - t0 : null; // matched before the write: not a read-back
    }

    static string Ms(double? ms, bool na = false) => na ? "    n/a" : ms is double d ? $"{d,7:F1}" : "   FAIL";

    /// <summary>The GraphQL dataref that mirrors an L: var, or null when Fenix does not expose it.</summary>
    static string? Mirror(string v)
    {
        var name = v[2..];
        if (name.EndsWith("_Cover") || name.StartsWith("E_")) return null;
        return name[0] switch
        {
            'S' => "system.switches." + name,
            'I' => "system.indicators." + name,
            'N' => "system.numerical." + name,
            'A' => "system.analog." + name,
            _ => null,
        };
    }

    // ---- restore and report ----------------------------------------------------------------------------

    static async Task Restore(Dictionary<string, double> originals)
    {
        Console.WriteLine("\nRestoring:");
        // ECAM page buttons toggle: press once more if the page selection differs from the start.
        for (int i = 0; i < EcamButtons.Length; i++)
        {
            int li = sim.IndexOf(EcamLights[i]);
            if (sim.Values[li] != originals[EcamLights[i]])
            {
                await sim.Write(sim.IndexOf(EcamButtons[i]), 1);
                await Task.Delay(150);
                await sim.Write(sim.IndexOf(EcamButtons[i]), 0);
                await Task.Delay(500);
            }
        }
        // Encoders are relative: writing the original counter undoes the net number of detents.
        foreach (var v in Rotaries.Concat(Covers).Concat(Encoders).Concat(Analogs).Concat(EcamButtons).Append(MasterCaution))
        {
            int idx = sim.IndexOf(v);
            if (sim.Values[idx] != originals[v]) await sim.Write(idx, originals[v]);
        }
        await Task.Delay(1500);
        foreach (var v in originals.Keys)
        {
            double now = sim.Values[sim.IndexOf(v)];
            bool same = Math.Abs(now - originals[v]) < 1e-6;
            Console.WriteLine($"  {v,-32} {originals[v].ToString(CultureInfo.InvariantCulture),8} -> {now.ToString(CultureInfo.InvariantCulture),-8} {(same ? "ok" : "NOT RESTORED")}");
        }
    }

    static void Report()
    {
        Console.WriteLine($"\n{"var",-30} {"type",-9} {"n",3} | {"lvar min/med/p95/max",-27} | {"effect med",10} | {"gql sub med",11} | {"gql http med",12} | fail");
        foreach (var group in Samples.GroupBy(s => s.Var))
        {
            var l = Sorted(group.Select(s => s.LvarMs));
            Console.WriteLine($"{group.Key,-30} {group.First().Test,-9} {group.Count(),3} | " +
                $"{Stats.F(Stats.Min(l)),5} {Stats.F(Stats.Median(l)),6} {Stats.F(Stats.P95(l)),6} {Stats.F(Stats.Max(l)),6} | " +
                $"{MedianOf(group.Select(s => s.EffectMs)),10} | {MedianOf(group.Select(s => s.GqlSubMs)),11} | {MedianOf(group.Select(s => s.GqlHttpMs)),12} | {group.Count(s => !s.Ok)}");
        }

        var csv = new StringBuilder("test,variable,iteration,written,lvar_ms,effect_ms,gql_sub_ms,gql_http_ms,ok\n");
        foreach (var s in Samples)
            csv.Append(CultureInfo.InvariantCulture, $"{s.Test},{s.Var},{s.Iteration},{s.Written},{Csv(s.LvarMs)},{Csv(s.EffectMs)},{Csv(s.GqlSubMs)},{Csv(s.GqlHttpMs)},{(s.Ok ? 1 : 0)}\n");
        var path = Path.Combine(ProjectDir(), "results.csv");
        File.WriteAllText(path, csv.ToString());
        Console.WriteLine($"\nSamples written to {path}");
    }

    static List<double> Sorted(IEnumerable<double?> xs) => xs.Where(x => x.HasValue).Select(x => x!.Value).Order().ToList();
    static string MedianOf(IEnumerable<double?> xs)
    {
        var all = xs.ToList();
        var s = Sorted(all);
        return s.Count == 0 ? "-" : $"{Stats.F(Stats.Median(s))} ({s.Count}/{all.Count})";
    }
    static string Csv(double? x) => x is double d ? d.ToString("F1", CultureInfo.InvariantCulture) : "";

    static string ProjectDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "VarTypes.csproj"))) return dir.FullName;
        return Directory.GetCurrentDirectory();
    }

    sealed class GuardViolation(string message) : Exception(message);
}
