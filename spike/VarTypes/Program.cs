// Spike: write + read-back for every kind of Fenix A320 control, and what the GraphQL backend exposes.
//
//   VarTypes indicators                 read all L:I_* from both profiles, list the non-zero ones
//   VarTypes gqlrate [seconds]          subscribe to many datarefs at once and measure update rates
//   VarTypes probe L:VAR [opts] v...    write values to one var and log what changes (semantics discovery)
//   VarTypes test                       the measurement suite, see Tests.cs

using System.Globalization;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

return args.FirstOrDefault() switch
{
    "indicators" => await Indicators(),
    "gqlrate" => await GqlRate(args.Length > 1 ? int.Parse(args[1]) : 10),
    "probe" when args.Length >= 3 => await Probe(args[1..]),
    "test" => await Tests.Run(cts.Token),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("Usage: VarTypes indicators | gqlrate [s] | probe L:VAR [--watch v1,v2] [--gql d1,d2] [--gap ms] values... | test");
    Console.Error.WriteLine("  probe values: 5 (absolute), +1 / -1 (relative to current value)");
    return 1;
}

async Task<int> Indicators()
{
    var names = Profiles.LVars(Profiles.V11, false)
        .Concat(Profiles.LVars(Profiles.Big, true))
        .Where(n => n.StartsWith("L:I_"))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order()
        .ToArray();
    using var sim = new Sim(names);
    await sim.WaitForValues(5000);
    var nonZero = Enumerable.Range(0, names.Length).Where(i => sim.Values[i] != 0).ToArray();
    Console.WriteLine($"Read {sim.Received.Count(r => r)} of {names.Length} L:I_* vars; {nonZero.Length} are non-zero:");
    foreach (var i in nonZero)
    {
        var gql = await Gql.Get("system.indicators." + names[i][2..]);
        Console.WriteLine($"  {names[i],-45} {sim.Values[i],6}   GraphQL system.indicators: {gql}");
    }
    return 0;
}

async Task<int> GqlRate(int seconds)
{
    string[] names =
    {
        "aircraft.mcdu1.display", "aircraft.mcdu2.display",
        "aircraft.fuel.left.amount.kg", "aircraft.fuel.right.amount.kg", "aircraft.fuel.center.amount.kg", "aircraft.fuel.total.amount.kg",
        "aircraft.hydraulics.green.pressure", "aircraft.hydraulics.blue.pressure", "aircraft.hydraulics.yellow.pressure",
        "aircraft.electrical.battery1.voltage", "aircraft.cabin.pressure", "aircraft.cabin.fwd.temp.air",
        "aircraft.heading.magnetic", "aircraft.altitude", "aircraft.cg", "aircraft.weight.zfw",
        "system.numerical.N_FCU_ALTITUDE", "system.numerical.N_FCU_HEADING", "system.numerical.N_FCU_SPEED",
        "system.switches.S_OH_ELEC_BAT1", "system.indicators.I_MIP_GEAR_1_L", "system.analog.A_OH_PNEUMATIC_COCKPIT_TEMP",
    };
    var gql = new Gql();
    var counts = names.ToDictionary(n => n, _ => 0);
    var messages = 0;
    var lastMessageAt = 0.0;
    var gaps = new List<double>();
    gql.ValueChanged += (n, _, t) =>
    {
        lock (counts)
        {
            counts[n]++;
            if (t != lastMessageAt) { if (lastMessageAt > 0) gaps.Add(t - lastMessageAt); messages++; lastMessageAt = t; }
        }
    };
    using var window = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
    window.CancelAfter(TimeSpan.FromSeconds(seconds));
    double start = Clock.Now;
    try { await gql.Subscribe(names, window.Token); } catch (OperationCanceledException) { }
    double elapsed = (Clock.Now - start) / 1000;

    Console.WriteLine($"One subscription, {names.Length} datarefs, {elapsed:F1} s, {messages} distinct message timestamps");
    gaps.Sort();
    if (gaps.Count > 0)
        Console.WriteLine($"Gap between messages: min {Stats.F(Stats.Min(gaps))} / median {Stats.F(Stats.Median(gaps))} / max {Stats.F(Stats.Max(gaps))} ms");
    foreach (var n in names)
        Console.WriteLine($"  {n,-45} {counts[n],5} updates  {counts[n] / elapsed,6:F1}/s   last {Trim(gql.Values.GetValueOrDefault(n))}");
    return 0;

    static string Trim(string? s) => s == null ? "(none)" : s.Length > 40 ? s[..40] + "…" : s;
}

async Task<int> Probe(string[] a)
{
    var target = a[0];
    var watch = new List<string>();
    var gqlNames = new List<string>();
    var pollNames = new List<string>();
    var values = new List<string>();
    int gap = 1500;
    for (int i = 1; i < a.Length; i++)
    {
        switch (a[i])
        {
            case "--watch": watch.AddRange(a[++i].Split(',')); break;
            case "--gql": gqlNames.AddRange(a[++i].Split(',')); break;
            case "--poll": pollNames.AddRange(a[++i].Split(',')); break;
            case "--gap": gap = int.Parse(a[++i]); break;
            default: values.Add(a[i]); break;
        }
    }

    using var sim = new Sim(watch.Prepend(target));
    var gql = new Gql();
    double t0 = Clock.Now;
    sim.ValueChanged += (i, v, t) => Console.WriteLine($"  {t - t0,9:F1}  {sim.Names[i]} = {v}");
    gql.ValueChanged += (n, v, t) => Console.WriteLine($"  {t - t0,9:F1}  gql {n} = {v}");
    var sub = gqlNames.Count > 0 ? gql.Subscribe(gqlNames, cts.Token) : Task.CompletedTask;
    var poll = Task.Run(async () =>
    {
        var last = new Dictionary<string, string>();
        while (pollNames.Count > 0 && !cts.IsCancellationRequested)
        {
            foreach (var n in pollNames)
            {
                var v = await Gql.Get(n);
                if (last.GetValueOrDefault(n) != v) Console.WriteLine($"  {Clock.Now - t0,9:F1}  http {n} = {v}");
                last[n] = v;
            }
            await Task.Delay(50);
        }
    });
    await sim.WaitForValues();
    foreach (var n in pollNames) await Gql.Get(n); // the first HTTP query takes ~1.5 s
    await Task.Delay(500);

    double original = sim.Values[0];
    foreach (var token in values)
    {
        if (cts.IsCancellationRequested) break;
        double current = sim.Values[0];
        double value = token[0] is '+' or '-' ? current + double.Parse(token, CultureInfo.InvariantCulture) : double.Parse(token, CultureInfo.InvariantCulture);
        Console.WriteLine($"  {Clock.Now - t0,9:F1}  WRITE {target} = {value}");
        await sim.Write(0, value);
        await Task.Delay(gap);
    }

    if (!target.StartsWith("L:E_") && sim.Values[0] != original)
    {
        Console.WriteLine($"  {Clock.Now - t0,9:F1}  RESTORE {target} = {original}");
        await sim.Write(0, original);
        await Task.Delay(gap);
    }
    cts.Cancel();
    try { await sub; } catch (OperationCanceledException) { }
    await poll;
    return 0;
}
