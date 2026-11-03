using Crosscheck.Bridge;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;

namespace ProfileProbe.Tests;

/// <summary>A SimConnect stand-in for deflimit: refuses every definition past <c>limit</c>, records every call.</summary>
sealed class FakeRegistrar(int limit) : IDefinitionRegistrar
{
    uint _send;
    readonly List<(uint, string)> _pending = [];
    public HashSet<int> Defined { get; } = [];
    public HashSet<int> Cleared { get; } = [];

    public uint Add(int definition)
    {
        var id = ++_send;
        Defined.Add(definition);
        if (definition >= limit) _pending.Add((id, "DEFINITION_ERROR"));
        return id;
    }

    public uint Clear(int definition)
    {
        Cleared.Add(definition);
        return ++_send;
    }

    public IReadOnlyList<(uint SendId, string Exception)> Drain(TimeSpan wait)
    {
        var list = _pending.ToList();
        _pending.Clear();
        return list;
    }
}

/// <summary>CRO-40: the two tools for the owner's sim checks (gate test 3 section 8).</summary>
public class Cro40ProbeTests
{
    static readonly string Dir = Path.Combine(RepoPaths.Find().Root, "tools", "profile-probe");

    [Fact]
    public void Deflimit_finds_the_first_refused_definition_and_clears_everything_it_made()
    {
        var r = new FakeRegistrar(limit: 937);
        var result = DefLimit.Probe(r, 2000, TimeSpan.Zero);
        Assert.Equal(937, result.FirstRefused);
        Assert.Equal(2000 - 937, result.Refused);
        Assert.Equal(2000, result.Cleared);
        Assert.Equal(r.Defined, r.Cleared); // every definition it made is gone again
        Assert.Contains("accepted 937 data definitions", DefLimit.Report(result));

        var none = DefLimit.Probe(new FakeRegistrar(int.MaxValue), 100, TimeSpan.Zero);
        Assert.Null(none.FirstRefused);
        Assert.Contains("accepted all 100", DefLimit.Report(none));
    }

    [Fact]
    public void Deflimit_registers_only_it_never_writes_requests_data_or_sends_events()
    {
        var text = File.ReadAllText(Path.Combine(Dir, "DefLimit.cs"));
        string[] forbidden =
        [
            "SetDataOnSimObject", "RequestDataOnSimObject", "TransmitClientEvent", "MapClientEventToSimEvent", "MapInputEventToClientEvent",
            "SetClientData", "SetInputGroup", "SimConnectAdapter", "ISimAdapter", "WriteAsync", "mutation",
        ];
        foreach (var word in forbidden) Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"DefLimit.cs contains {word}");
        // The registrar can define and clear, nothing else.
        Assert.Equal(["Add", "Clear", "Drain"], typeof(IDefinitionRegistrar).GetMethods().Select(m => m.Name).Order());
    }

    static (Profile Real, UnsyncedList Unsynced) RealProfile()
    {
        var path = RepoPaths.Find().Profile;
        var real = ProfileLoader.LoadFile(path).Profile!;
        return (real, UnsyncedList.LoadNextTo(path, real));
    }

    [Fact]
    public void Bridge_profile_uses_the_bridge_watch_list_and_only_the_tray_table_is_writable()
    {
        var (real, unsynced) = RealProfile();
        var tray = WriteTest.Controls.Single(c => c.Id == "TRAY");
        var (profile, watched) = BridgePath.For(real, unsynced, [tray]);
        // The same read definitions as a bridge session (plus the probe's safety vars), and the write-slot pool.
        Assert.Superset(SyncEngine.PlanFor(real, unsynced.Names).Watched.ToHashSet(), watched.ToHashSet());
        Assert.Superset(SafetyVars.Lvars.ToHashSet(), watched.ToHashSet());
        Assert.True(SimDefinitions.Total(watched.Count) <= SimDefinitions.MaxDefinitions);
        Assert.Contains("write slots", BridgePath.Describe(watched.Count));
        // The guard lets exactly the tray table through, with its values; every other var of the profile is read-only.
        var guard = new SimSafetyGuard(profile);
        Assert.True(guard.IsAllowedValue("S_TRAY_TABLE_CAPT", 1));
        Assert.True(guard.IsAllowedValue("S_TRAY_TABLE_CAPT", 0));
        Assert.False(guard.IsAllowedValue("S_TRAY_TABLE_CAPT", 2));
        Assert.Equal(["S_TRAY_TABLE_CAPT"], profile.Vars.Where(v => guard.IsWritable(v.Name)).Select(v => v.Name));
        foreach (var name in new[] { "S_FC_FLAPS", "S_TRAY_TABLE_FO", "S_OH_ELEC_BAT1", "E_FCU_ALTITUDE", "S_OH_ELEC_IDG1" })
            Assert.False(guard.IsWritable(name), name);
    }

    [Fact]
    public void Bridge_profile_refuses_anything_but_a_plain_switch_of_the_profile()
    {
        var (real, unsynced) = RealProfile();
        var spd = WriteTest.Controls.Single(c => c.Id == "FCU_SPD"); // a counter, not a switch with values
        Assert.Throws<InvalidOperationException>(() => BridgePath.For(real, unsynced, [spd]));
    }

    [Fact]
    public async Task Tray_table_writes_only_its_var_and_ends_where_it_started()
    {
        var tray = WriteTest.Controls.Single(c => c.Id == "TRAY");
        await using var rig = await Rig.Make(WriteTest.Targets([tray]), []);
        var results = Path.Combine(rig.Dir, "writetest-results.md");
        rig.Sim.Logic("S_TRAY_TABLE_CAPT", 0);
        await WriteTest.Run(rig.Session, [tray], _ => Task.FromResult<string?>(""), results, default, mode: "bridge write path (test)");
        Assert.Equal([1, 0], rig.Sim.WritesOf("S_TRAY_TABLE_CAPT"));
        Assert.All(rig.Sim.Writes, w => Assert.Equal("S_TRAY_TABLE_CAPT", w.Name));
        var text = File.ReadAllText(results);
        Assert.Contains("bridge write path (test)", text);
        Assert.Contains("0 -> 1 (position 1): STUCK", text);
        Assert.Contains("back at 0 (where it started)", text);
    }
}
