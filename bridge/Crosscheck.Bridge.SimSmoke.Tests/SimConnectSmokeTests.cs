using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.SimConnect;
using Crosscheck.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Crosscheck.Bridge.SimSmoke.Tests;

/// <summary>Runs only with CROSSCHECK_SIM_SMOKE=1 and MSFS 2024 + Fenix A320 loaded (CRO-8). See README.md.</summary>
public sealed class SimSmokeFactAttribute : FactAttribute
{
    public SimSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CROSSCHECK_SIM_SMOKE") != "1")
            Skip = "Real-sim smoke test (CRO-8): set CROSSCHECK_SIM_SMOKE=1 with MSFS 2024 and the Fenix A320 loaded. See bridge/Crosscheck.Bridge.SimSmoke.Tests/README.md.";
    }
}

public class SimConnectSmokeTests
{
    // Harmless and visible: the captain's ND mode (0..4). Restored at the end, also on failure.
    private const string Var = "S_FCU_EFIS1_ND_MODE";

    [SimSmokeFact]
    public async Task Connects_reads_profile_vars_writes_one_and_restores_it()
    {
        var profile = ProfileLoader.LoadFile(FindProfile()).Profile ?? throw new InvalidOperationException("profile invalid");
        await using var sim = new SimConnectAdapter(profile, NullLogger.Instance);
        var plan = new SimPlan(profile.Vars.Where(v => v.Class is not (Profiles.Model.SyncClass.Ignore or Profiles.Model.SyncClass.Stream)).Select(v => v.Name).ToList());
        await sim.StartAsync(plan, CancellationToken.None);

        await Until(() => sim.State == SimState.Connected, "SimConnect connection", 60_000);
        await Until(() => plan.Watched.All(n => sim.TryGet(n, out _)), "first values for every profile var", 10_000);

        Assert.True(sim.TryGet(Var, out var original));
        var test = (original + 1) % 5;
        try
        {
            await sim.WriteAsync(Var, test);
            await Until(() => sim.TryGet(Var, out var v) && v == test, "read-back of the write", 2_000);

            // The guard refuses out-of-range values before anything reaches the sim.
            await Assert.ThrowsAsync<SafetyViolationException>(() => sim.WriteAsync(Var, 9));
            await Task.Delay(200);
            Assert.True(sim.TryGet(Var, out var after) && after == test);
        }
        finally
        {
            await sim.WriteAsync(Var, original);
            await Until(() => sim.TryGet(Var, out var v) && v == original, "restore", 2_000);
        }
    }

    private static string FindProfile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine(dir.FullName, "profiles", "fenix-a320", "profile.yaml");
            if (File.Exists(p)) return p;
        }
        throw new FileNotFoundException("profiles/fenix-a320/profile.yaml");
    }

    private static async Task Until(Func<bool> condition, string what, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException($"timed out: {what}");
            await Task.Delay(20);
        }
    }
}
