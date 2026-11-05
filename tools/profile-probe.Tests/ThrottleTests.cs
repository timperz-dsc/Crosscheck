using ProfileProbe;

namespace ProfileProbe.Tests;

public class ThrottleTests
{
    static readonly IReadOnlyList<string> Vars = ThrottleVars.All;
    const double FrameMs = 33.3;

    /// <summary>A recorded-style frame: both inputs set, everything else 0.</summary>
    static ThrottleSample Frame(double t, double left, double right, double leftGate = 0, double rightGate = 0)
    {
        var v = new double[Vars.Count];
        v[Index(ThrottleVars.Left.Input)] = left;
        v[Index(ThrottleVars.Right.Input)] = right;
        v[Index(ThrottleVars.Left.Gate)] = leftGate;
        v[Index(ThrottleVars.Right.Gate)] = rightGate;
        return new ThrottleSample(t, v);
    }

    static int Index(string var) => Vars.ToList().IndexOf(var);

    /// <summary>Small fixed jitter, like a hardware axis at rest: -0.001, 0, +0.001, ...</summary>
    static double Jitter(int k) => (k % 3 - 1) * 0.001;

    /// <summary>A sweep shaped like a real one: holds of <paramref name="holdMs"/> at each level, linear ramps between.</summary>
    static List<(double TMs, double V)> Staircase(double[] levels, double holdMs = 500, double rampMs = 300)
    {
        var s = new List<(double, double)>();
        double t = 0;
        var k = 0;
        for (var i = 0; i < levels.Length; i++)
        {
            if (i > 0)
                for (var r = FrameMs; r < rampMs; r += FrameMs)
                    s.Add((t += FrameMs, levels[i - 1] + (levels[i] - levels[i - 1]) * r / rampMs));
            for (var h = 0.0; h < holdMs; h += FrameMs)
                s.Add((t += FrameMs, levels[i] + Jitter(k++)));
        }
        return s;
    }

    static ThrottleRecord Record(string id, IEnumerable<ThrottleSample> samples, bool complete = true)
    {
        var r = new ThrottleRecord { Step = ThrottleVars.Steps.Single(s => s.Id == id), Complete = complete };
        r.Samples.AddRange(samples);
        return r;
    }

    static IEnumerable<ThrottleSample> Hold(double left, double right, int frames = 60, double gate = 0) =>
        Enumerable.Range(0, frames).Select(k => Frame(k * FrameMs, left + Jitter(k), right + Jitter(k + 1), gate, gate));

    // --- vars

    [Fact]
    public void Every_lever_lvar_is_in_the_scan_and_fits_the_reader()
    {
        var scan = File.ReadLines(RepoPaths.Find().Scan).Where(l => !l.StartsWith('#')).Select(l => l.Split(',')[0]).ToHashSet(StringComparer.Ordinal);
        Assert.All(ThrottleVars.LeverVars.Where(v => !SimVars.IsSimVar(v)), v => Assert.Contains(v, scan));
        Assert.All(ThrottleVars.LeverVars.Where(SimVars.IsSimVar), v => Assert.NotNull(SimVars.Unit(v)));
        Assert.True(Vars.Count(v => !SimVars.IsSimVar(v)) <= ObserveReader.MaxVars);
        Assert.True(Vars.Count(SimVars.IsSimVar) <= ObserveReader.MaxSimVars);
        Assert.Equal(ThrottleVars.Steps.Length, ThrottleVars.Steps.Select(s => s.Id).Distinct().Count());
    }

    // --- stats

    [Fact]
    public void Stats_of_a_known_set()
    {
        var s = ThrottleAnalysis.Stats([1, 2, 3, 4]);
        Assert.Equal(4, s.N);
        Assert.Equal(1, s.Min);
        Assert.Equal(4, s.Max);
        Assert.Equal(2.5, s.Mean, 9);
        Assert.Equal(2.5, s.Median, 9);
        Assert.Equal(Math.Sqrt(1.25), s.StdDev, 9);
        Assert.Equal(3, s.Spread);
        Assert.Equal(2, ThrottleAnalysis.Stats([3, 1, 2]).Median);
    }

    [Fact]
    public void Stats_of_nothing_is_empty_not_zero()
    {
        var s = ThrottleAnalysis.Stats([]);
        Assert.Equal(0, s.N);
        Assert.True(double.IsNaN(s.Min) && double.IsNaN(s.Mean));
    }

    [Fact]
    public void Band_comes_from_the_noise_with_a_floor()
    {
        Assert.Equal(ThrottleAnalysis.MinBand, ThrottleAnalysis.Band([]));
        Assert.Equal(ThrottleAnalysis.MinBand, ThrottleAnalysis.Band([ThrottleAnalysis.Stats([2, 2, 2])]));
        var noisy = ThrottleAnalysis.Stats(Enumerable.Range(0, 300).Select(k => 2 + Jitter(k) * 4));
        Assert.Equal(Math.Max(4 * noisy.StdDev, noisy.Spread), ThrottleAnalysis.Band([noisy]), 9);
    }

    // --- plateaus

    static readonly double[] Levels = [0, 1, 2, 3, 3.5, 4];

    [Fact]
    public void A_staircase_sweep_gives_one_plateau_per_level()
    {
        var p = ThrottleAnalysis.Plateaus(Staircase(Levels), 0.005);
        Assert.Equal(Levels.Length, p.Count);
        foreach (var (plateau, level) in p.Zip(Levels))
        {
            Assert.InRange(plateau.Median, level - 0.0015, level + 0.0015);
            Assert.True(plateau.DurationMs >= ThrottleAnalysis.DefaultMinPlateauMs);
        }
    }

    [Fact]
    public void A_short_stop_is_not_a_plateau()
    {
        var p = ThrottleAnalysis.Plateaus(Staircase([1, 2, 3], holdMs: 150), 0.005);
        Assert.Empty(p);
    }

    [Fact]
    public void A_steady_ramp_has_no_plateau()
    {
        var ramp = Enumerable.Range(0, 300).Select(k => (k * FrameMs, 4.0 * k / 300)).ToList();
        Assert.Empty(ThrottleAnalysis.Plateaus(ramp, 0.005));
        Assert.Equal(4.0 / 300, ThrottleAnalysis.LargestStep(ramp.Select(x => x.Item2).ToList()), 9);
    }

    [Fact]
    public void Noise_within_the_band_does_not_split_a_plateau_and_a_spike_is_merged_over()
    {
        var s = Enumerable.Range(0, 15).Select(k => (k * FrameMs, 2 + Jitter(k) * 2)).ToList();
        s.Add((15 * FrameMs, 2.5)); // one frame spike
        s.AddRange(Enumerable.Range(16, 15).Select(k => (k * FrameMs, 2 + Jitter(k) * 2)));
        var p = Assert.Single(ThrottleAnalysis.Plateaus(s, 0.005));
        Assert.Equal(2, p.Median, 3);
        Assert.Equal(0, p.StartMs);
        Assert.Equal(30 * FrameMs, p.EndMs, 6);
    }

    // --- detents, left vs right, proposal

    [Fact]
    public void Plateaus_match_the_nearest_held_detent_and_an_extra_stop_is_reported()
    {
        var plateaus = new[] { 2.001, 3.0, 2.6 }.Select(m => new Plateau(0, 500, 15, m, m, m));
        var holds = new Dictionary<string, double> { ["IDLE"] = 2, ["CL"] = 3 };
        var m = ThrottleAnalysis.MatchDetents(plateaus, holds, ThrottleAnalysis.Reach(0.005));
        Assert.Equal(["IDLE", "CL", null], m.Select(x => x.Detent));
    }

    [Fact]
    public void Left_and_right_difference_per_detent()
    {
        var lr = ThrottleAnalysis.LeftVsRight(
            new Dictionary<string, double> { ["IDLE"] = 2, ["CL"] = 3, ["TOGA"] = 4 },
            new Dictionary<string, double> { ["IDLE"] = 2.01, ["CL"] = 3 });
        Assert.Equal(["IDLE", "CL"], lr.Select(x => x.Detent));
        Assert.Equal(0.01, lr[0].Diff, 9);
        Assert.Equal(0, lr[1].Diff, 9);
    }

    [Fact]
    public void Proposal_rounds_the_range_outward_and_names_the_missing_detents()
    {
        var records = new List<ThrottleRecord>
        {
            Record("NOISE", Hold(2, 2)),
            Record("MAX_REV", Hold(-0.0021, 0, gate: 1)),
            Record("IDLE", Hold(2, 2, gate: 3)),
            Record("CL", Hold(3, 3.002)),
            Record("FLX_MCT", Hold(3.5, 3.5)),
            Record("TOGA", Hold(9, 9), complete: false), // cut short by Ctrl+C: left out
            Record("SWEEP_LEFT", Enumerable.Range(0, 20).Select(k => Frame(k * FrameMs, 3.5011 * k / 19, 2))),
        };
        var redone = Record("REV_IDLE", Hold(7, 7));
        redone.Replaced = true;
        records.Add(redone);

        var p = ThrottleAnalysis.Proposal(records, Vars);
        Assert.Equal(-0.01, p.Min, 9);
        Assert.Equal(3.51, p.Max, 9);
        Assert.Equal(["MAX_REV", "IDLE", "CL", "FLX_MCT"], p.Detents.Select(d => d.Detent));
        Assert.Equal(["REV_IDLE", "TOGA"], p.MissingDetents);
        var idle = p.Detents.Single(d => d.Detent == "IDLE");
        Assert.Equal(3, idle.GateLeft);
        Assert.InRange(idle.Value, 1.999, 2.001);
    }

    [Fact]
    public void Report_shows_detents_sweep_plateaus_and_a_proposal_that_is_not_applied()
    {
        var records = new List<ThrottleRecord> { Record("NOISE", Hold(2, 2, frames: 300)) };
        foreach (var (d, v) in ThrottleVars.Detents.Zip(Levels)) records.Add(Record(d, Hold(v, v)));
        var sweep = Staircase(Levels);
        records.Add(Record("SWEEP_LEFT", sweep.Select(x => Frame(x.TMs, x.V, 2))));
        var md = ThrottleAnalysis.Report(records, Vars, new ThrottleAnalysis.RunInfo(new DateTime(2026, 11, 5, 10, 0, 0), "Fenix", 60, 40, []));
        Assert.Contains("not applied: to be confirmed by the owner", md);
        Assert.Contains("range: [-0.01, 4.01]", md); // jitter of 0.001 around 0 and 4, rounded outward
        Assert.Contains("FLX/MCT (held 3.5", md);
        Assert.Contains("Steps not recorded (skipped, stopped or cut short): SWEEP_RIGHT", md);
        Assert.Contains("Left and right agree", md);
        Assert.DoesNotContain("extra plateau", md);
    }

    // --- the guided run on a fake sim

    static ThrottleSettings Settings(string dir) => new()
    {
        ResultsPath = Path.Combine(dir, "throttle-results.md"),
        SamplesPath = Path.Combine(dir, "throttle-samples.csv"),
        RawDir = Path.Combine(dir, "raw"),
        ConnectSeconds = 5,
        NoiseMs = 150,
        HoldMs = 80,
        SweepCapMs = 5000,
    };

    static string TempDir() => Directory.CreateTempSubdirectory("throttle-test-").FullName;

    [Fact]
    public async Task A_full_run_writes_the_report_and_every_sample()
    {
        var dir = TempDir();
        await using var source = new FakeObserveSource(Vars);
        void Set(double left, double right) { source.Set(ThrottleVars.Left.Input, left); source.Set(ThrottleVars.Right.Input, right); }
        async Task<string?> Ramp(string input)
        {
            for (var k = 0; k <= 40; k++) { source.Set(input, 4.0 * k / 40); await Task.Delay(5); }
            return "";
        }
        var script = new Queue<Func<Task<string?>>>();
        script.Enqueue(() => Task.FromResult<string?>(""));                          // connect
        script.Enqueue(() => { Set(2, 2); return Task.FromResult<string?>(""); });   // noise
        foreach (var (_, v) in ThrottleVars.Detents.Zip(Levels))
            script.Enqueue(() => { Set(v, v); return Task.FromResult<string?>(""); });
        script.Enqueue(() => { Set(0, 2); return Task.FromResult<string?>(""); });   // sweep left: start
        script.Enqueue(() => Ramp(ThrottleVars.Left.Input));                         //   stop at TOGA
        script.Enqueue(() => { Set(2, 0); return Task.FromResult<string?>(""); });   // sweep right
        script.Enqueue(() => Ramp(ThrottleVars.Right.Input));
        script.Enqueue(() => Task.FromResult<string?>(""));                          // save

        var phase = new ThrottlePhase(source, new FakeSimvars(), new StringWriter(), _ => script.Dequeue()(), Settings(dir));
        Assert.Equal(0, await phase.Run(CancellationToken.None));

        Assert.Empty(script);
        Assert.Equal(ThrottleVars.Steps.Select(s => s.Id), phase.Records.Select(r => r.Step.Id));
        Assert.All(phase.Records, r => Assert.True(r.Complete && r.Samples.Count > 0, r.Name));
        var md = File.ReadAllText(Path.Combine(dir, "throttle-results.md"));
        Assert.Contains("range: [0, 4]", md);
        var csv = File.ReadAllLines(Path.Combine(dir, "throttle-samples.csv"));
        Assert.Equal(phase.Records.Sum(r => r.Samples.Count) + 1, csv.Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(dir, "raw")).Length);
    }

    [Fact]
    public async Task Ctrl_c_mid_run_still_saves_what_was_recorded()
    {
        var dir = TempDir();
        using var cts = new CancellationTokenSource();
        await using var source = new FakeObserveSource(Vars);
        var answers = 0;
        Task<string?> Pilot(CancellationToken ct)
        {
            if (++answers == 4) { cts.Cancel(); ct.ThrowIfCancellationRequested(); } // Ctrl+C at the REV IDLE prompt
            return Task.FromResult<string?>("");
        }
        var phase = new ThrottlePhase(source, new FakeSimvars(), new StringWriter(), Pilot, Settings(dir));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => phase.Run(cts.Token));

        Assert.Equal(["NOISE", "MAX_REV"], phase.Records.Select(r => r.Step.Id));
        var md = File.ReadAllText(Path.Combine(dir, "throttle-results.md"));
        Assert.Contains("MAX REV", md);
        Assert.Contains("Detents not recorded, so not in the list: REV IDLE, IDLE, CL, FLX/MCT, TOGA", md);
    }

    [Fact]
    public async Task Redo_replaces_the_step_before_and_skip_leaves_it_out()
    {
        var dir = TempDir();
        await using var source = new FakeObserveSource(Vars);
        // connect, noise, MAX_REV, then r (redo MAX_REV), MAX_REV again, REV_IDLE skipped, then stop.
        var script = new Queue<string?>(["", "", "", "r", "", "s", "q"]);
        var phase = new ThrottlePhase(source, new FakeSimvars(), new StringWriter(), _ => Task.FromResult(script.Dequeue()), Settings(dir));
        Assert.Equal(0, await phase.Run(CancellationToken.None));

        Assert.Equal(["NOISE", "MAX_REV", "MAX_REV-2"], phase.Records.Select(r => r.Name));
        Assert.True(phase.Records[1].Replaced);
        Assert.Single(ThrottleAnalysis.Valid(phase.Records), r => r.Step.Id == "MAX_REV");
    }

    [Fact]
    public async Task A_parked_setup_problem_stops_before_any_step()
    {
        var dir = TempDir();
        await using var source = new FakeObserveSource(Vars);
        source.Set(SafetyVars.ParkingBrake, 0);
        var phase = new ThrottlePhase(source, new FakeSimvars(), new StringWriter(), _ => Task.FromResult<string?>(""), Settings(dir));
        var e = await Assert.ThrowsAsync<SafetyStopException>(() => phase.Run(CancellationToken.None));
        Assert.Contains("parking brake", e.Message);
        Assert.Empty(phase.Records);
        Assert.False(File.Exists(Path.Combine(dir, "throttle-results.md")));
    }
}
