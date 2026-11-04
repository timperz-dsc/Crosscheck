using System.Net.Http;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// A SimConnect stand-in for the write path: it refuses every definition past <c>limit</c> distinct definition
/// IDs, like the sim did in gate tests 2 and 3, refuses clearing a definition that never had a var added
/// (UNRECOGNIZED_ID, flight 1 on 2026-11-04), and remembers what each definition points at.
/// </summary>
internal sealed class FakeSimConnectCalls(int limit) : ISimConnectCalls
{
    private uint _send;
    public HashSet<int> Definitions { get; } = [];
    public Dictionary<int, string?> Bound { get; } = [];
    /// <summary>Definitions the sim knows (an add went through).</summary>
    public HashSet<int> Created { get; } = [];
    /// <summary>Send IDs SimConnect would answer with an exception (a refused definition).</summary>
    public List<uint> Refused { get; } = [];
    /// <summary>Every write-path call in order: (call, definition).</summary>
    public List<(string Call, int Def)> Log { get; } = [];
    /// <summary>Values that landed: var → value.</summary>
    public Dictionary<string, double> Landed { get; } = new(StringComparer.Ordinal);
    public int Clears { get; private set; }
    /// <summary>The send ID of the last call.</summary>
    public uint LastSend => _send;

    private uint Define(int definition)
    {
        var id = ++_send;
        Definitions.Add(definition);
        if (Definitions.Count > limit) Refused.Add(id);
        return id;
    }

    public uint RegisterOneVar(int definition) => Define(definition);

    public uint ClearDefinition(int definition)
    {
        Log.Add(("clear", definition));
        Clears++;
        var id = ++_send;
        if (!Created.Contains(definition)) Refused.Add(id); // UNRECOGNIZED_ID, as the real sim answers
        Bound[definition] = null;
        return id;
    }

    public uint AddToDefinition(int definition, string lvar)
    {
        Log.Add(("add", definition));
        var id = Define(definition);
        if (Definitions.Count <= limit) Created.Add(definition);
        Bound[definition] = lvar;
        return id;
    }

    public uint SetValue(int definition, double value)
    {
        Log.Add(("set", definition));
        var id = ++_send;
        if (Definitions.Count <= limit && Bound.GetValueOrDefault(definition) is { } name) Landed[name] = value;
        return id;
    }

    /// <summary>A read chunk registered at connect (as the adapter does), one send per call.</summary>
    public uint ReadChunk(int chunk) => Define(chunk);
}

/// <summary>
/// CRO-40 (gate test 3 cause A): the bridge registered one SimConnect definition per writable var (about 1,240);
/// the sim refused those past roughly 900–1,190 and writes to vars late in the profile never landed, silently.
/// Now writes share a small pool, the total stays far below the limit, and a refused write is never silent.
/// </summary>
public sealed class WriteSlotTests
{
    private static readonly Profile Real =
        ProfileLoader.LoadFile(Path.Combine(Repo.Root, "profiles", "fenix-a320", "profile.yaml")).Profile!;

    private static List<string> WritableInProfileOrder(Profile p)
    {
        var guard = new SimSafetyGuard(p);
        return p.Vars.Where(v => guard.IsWritable(v.Name)).Select(v => v.Name).ToList();
    }

    /// <summary>The adapter's connect sequence: read chunks first, then the write slots.</summary>
    private static (SimDefinitions Defs, FakeSimConnectCalls Calls) Connect(Profile p, int limit)
    {
        var watched = SyncEngine.PlanFor(p).Watched;
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(limit);
        for (var c = 0; c < SimDefinitions.ReadChunks(watched.Count); c++) defs.Note(calls.ReadChunk(c), SendKind.ReadChunk, watched[c * SimDefinitions.ChunkSize], c);
        defs.SetupSlots(calls);
        return (defs, calls);
    }

    [Fact]
    public void The_real_profile_needs_far_fewer_definitions_than_the_sim_refused_and_any_profile_size_is_bounded()
    {
        var watched = SyncEngine.PlanFor(Real).Watched.Count;
        var total = SimDefinitions.Total(watched);
        Assert.True(total <= SimDefinitions.MaxDefinitions, $"{total} definitions");
        // Gate test 3: the cut lay between about 870 and 1,190 definitions; the old scheme needed 1,238 + 57.
        Assert.True(total < 200, $"{total} definitions");
        Assert.True(WritableInProfileOrder(Real).Count > 1000); // the writable vars no longer each need one
        // A profile too large for the budget is refused loudly, never left to lose writes silently.
        Assert.Null(SimDefinitions.BudgetError(watched));
        Assert.NotNull(SimDefinitions.BudgetError(20_000));
    }

    [Fact]
    public void Writes_to_every_writable_var_land_even_the_last_one_and_the_definitions_stay_bounded()
    {
        // A sim that refuses definitions past 900, like the cut seen in gate tests 2 and 3.
        var (defs, calls) = Connect(Real, limit: 900);
        var writable = WritableInProfileOrder(Real);
        foreach (var name in writable) defs.Write(calls, name, 1);
        Assert.Empty(calls.Refused);
        Assert.True(calls.Definitions.Count <= SimDefinitions.MaxDefinitions, $"{calls.Definitions.Count} definitions");
        Assert.Equal(writable.Count, calls.Landed.Count);
        // The vars that failed in gate test 3 (late in the list) land now.
        foreach (var name in new[] { writable[^1], "S_FC_FLAPS", "A_FC_SPEEDBRAKE", "S_TRAY_TABLE_CAPT", "S_OXYGEN_MASK_COVER_CAPT", "S_COCKPIT_DOOR" })
            Assert.True(calls.Landed.ContainsKey(name), name);
    }

    [Fact]
    public void A_slot_stays_with_its_var_while_in_use_and_the_least_recently_used_slot_is_reused()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        defs.Write(calls, "V0", 1);
        defs.Write(calls, "V0", 0);
        Assert.Equal(0, calls.Clears); // a fresh slot needs no clear, a second write of the same var re-points nothing
        for (var i = 1; i < SimDefinitions.PoolSize; i++) defs.Write(calls, $"V{i}", 1);
        defs.Write(calls, "V0", 1); // V0 is the most recent now; V1 the least recent
        defs.Write(calls, "V8", 1);
        Assert.Contains("V0", Enumerable.Range(0, SimDefinitions.PoolSize).Select(defs.SlotVar));
        Assert.DoesNotContain("V1", Enumerable.Range(0, SimDefinitions.PoolSize).Select(defs.SlotVar));
        Assert.Equal(SimDefinitions.PoolSize, calls.Definitions.Count);
        Assert.Equal(1, calls.Landed["V8"]);
    }

    [Fact]
    public void A_refused_setup_call_is_a_setup_error()
    {
        // A sim that refuses everything past 3 definitions: the read chunk and two slots fit, the rest do not.
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(limit: 3);
        defs.Note(calls.ReadChunk(0), SendKind.ReadChunk, "S_A", 0);
        defs.SetupSlots(calls);
        Assert.Equal(SimDefinitions.PoolSize - 2, calls.Refused.Count);
        Assert.All(calls.Refused, id => Assert.IsType<SimExceptionOutcome.SetupError>(defs.OnException(id, "ERROR")));
    }

    /// <summary>Flight 1 (2026-11-04): each slot's first bind cleared a definition the sim never had, so 8 vars per side were blocked.</summary>
    [Fact]
    public void First_use_of_each_slot_sends_no_clear_and_nothing_is_refused()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        // The guest's first writes in flight 1, then more than a pool's worth so slots are re-used.
        string[] vars = ["A_ASP_INT_VOLUME", "S_ASP_INT_REC_LATCH", "A_FC_SPEEDBRAKE", "S_OH_INT_LT_EMER", "S_OH_EXT_LT_NAV_LOGO",
            "S_OH_OXYGEN_CREW_OXYGEN", "S_OH_RCRD_GND_CTL", "A_OH_PNEUMATIC_FWD_TEMP", "S_ARMREST_LEFT_FO", "E_FCU_EFIS1_BARO", "S_CDU1_KEY_FPLN"];
        foreach (var v in vars) defs.Write(calls, v, 1);
        Assert.Empty(calls.Refused);
        Assert.Equal(vars.Length - SimDefinitions.PoolSize, calls.Clears); // only re-used slots are cleared
        Assert.All(vars, v => Assert.Equal(1, calls.Landed[v]));
        Assert.All(vars, v => Assert.Equal(0, defs.Strikes(v)));
    }

    [Fact]
    public void Rebind_sends_clear_then_add_then_set_on_the_same_definition_in_order()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        for (var i = 0; i < SimDefinitions.PoolSize; i++) defs.Write(calls, $"V{i}", 1);
        calls.Log.Clear();
        defs.Write(calls, "NEW", 5); // takes V0's slot (least recently used)
        const int def = SimDefinitions.WriteDefBase;
        Assert.Equal([("clear", def), ("add", def), ("set", def)], calls.Log);
        Assert.Equal("NEW", calls.Bound[def]);
        Assert.Equal(5, calls.Landed["NEW"]);
        Assert.Empty(calls.Refused);
    }

    [Fact]
    public void An_exception_for_an_unknown_send_is_logged_and_blocks_nothing()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        defs.Write(calls, "S_TRAY_TABLE_CAPT", 1);
        Assert.IsType<SimExceptionOutcome.Unknown>(defs.OnException(99_999, "UNRECOGNIZED_ID"));
        Assert.IsType<SimExceptionOutcome.Unknown>(defs.OnException(uint.MaxValue, "UNRECOGNIZED_ID"));
        Assert.Equal(0, defs.Strikes("S_TRAY_TABLE_CAPT"));
        Assert.Contains("S_TRAY_TABLE_CAPT", Enumerable.Range(0, SimDefinitions.PoolSize).Select(defs.SlotVar)); // still bound
    }

    [Fact]
    public void A_refused_clear_is_ignored_blocks_nothing_and_retires_the_slot()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        for (var i = 0; i < SimDefinitions.PoolSize; i++) defs.Write(calls, $"V{i}", 1);
        defs.Write(calls, "NEW", 1);
        var clear = calls.LastSend - 2; // clear, add, set
        var outcome = Assert.IsType<SimExceptionOutcome.Ignored>(defs.OnException(clear, "UNRECOGNIZED_ID"));
        Assert.Contains("not blocked", outcome.Why);
        Assert.Equal(0, defs.Strikes("NEW"));
        // The slot is not used again; NEW moves to another slot on its next write.
        Assert.DoesNotContain("NEW", Enumerable.Range(0, SimDefinitions.PoolSize).Select(defs.SlotVar));
        calls.Log.Clear();
        defs.Write(calls, "NEW", 2);
        Assert.DoesNotContain(calls.Log, c => c.Def == SimDefinitions.WriteDefBase);
        Assert.Equal(2, calls.Landed["NEW"]);
    }

    [Fact]
    public void A_matched_rejection_retries_once_with_the_same_value_and_the_second_blocks()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        defs.Write(calls, "S_TRAY_TABLE_CAPT", 1);
        // The write itself is refused (its send ID matches exactly): first strike, retry the same value.
        var retry = Assert.IsType<SimExceptionOutcome.Retry>(defs.OnException(calls.LastSend, "DATA_ERROR"));
        Assert.Equal("S_TRAY_TABLE_CAPT", retry.Var);
        Assert.Equal(1, retry.Value);
        Assert.DoesNotContain("S_TRAY_TABLE_CAPT", Enumerable.Range(0, SimDefinitions.PoolSize).Select(defs.SlotVar)); // fresh slot next time
        // The retry is refused too: blocked.
        defs.Write(calls, retry.Var, retry.Value);
        var rejected = Assert.IsType<SimExceptionOutcome.WriteRejected>(defs.OnException(calls.LastSend, "DATA_ERROR"));
        Assert.Equal("S_TRAY_TABLE_CAPT", rejected.Var);
        Assert.Contains("DATA_ERROR", rejected.Why);
    }

    [Fact]
    public void Two_exceptions_from_one_attempt_count_as_one_strike()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        defs.Write(calls, "V", 1);
        var add = calls.LastSend - 1;
        Assert.IsType<SimExceptionOutcome.Retry>(defs.OnException(add, "NAME_UNRECOGNIZED"));
        Assert.IsType<SimExceptionOutcome.Ignored>(defs.OnException(calls.LastSend, "DATA_ERROR"));
        Assert.Equal(1, defs.Strikes("V"));
    }

    [Fact]
    public void A_newer_write_of_the_var_is_the_retry()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        defs.Write(calls, "V", 1);
        var first = calls.LastSend;
        defs.Write(calls, "V", 0); // the pilot moved it again before the exception arrived
        Assert.IsType<SimExceptionOutcome.Ignored>(defs.OnException(first, "DATA_ERROR"));
        Assert.Equal(1, defs.Strikes("V"));
    }

    [Fact]
    public void A_landed_write_resets_the_strikes()
    {
        var defs = new SimDefinitions();
        var calls = new FakeSimConnectCalls(int.MaxValue);
        defs.SetupSlots(calls);
        defs.Write(calls, "V", 1);
        Assert.IsType<SimExceptionOutcome.Retry>(defs.OnException(calls.LastSend, "DATA_ERROR"));
        defs.Write(calls, "V", 1);
        defs.NoteLanded("V", 0.5); // another value proves nothing
        Assert.Equal(1, defs.Strikes("V"));
        defs.NoteLanded("V", 1);
        Assert.Equal(0, defs.Strikes("V"));
        // A later single refusal is a retry again, not a block.
        defs.Write(calls, "V", 0);
        Assert.IsType<SimExceptionOutcome.Retry>(defs.OnException(calls.LastSend, "DATA_ERROR"));
    }

    [Fact]
    public void A_struct_registration_that_sends_no_packet_does_not_relabel_the_previous_call()
    {
        var defs = new SimDefinitions();
        defs.Note(7, SendKind.ReadChunk, "S_A", 0);
        defs.SetupSlots(new NoPacketRegister());
        var error = Assert.IsType<SimExceptionOutcome.SetupError>(defs.OnException(7, "ERROR"));
        Assert.Contains("read definition", error.Why);
    }

    /// <summary>RegisterDataDefineStruct may send no packet: "last sent" is still the read request (send 7).</summary>
    private sealed class NoPacketRegister : ISimConnectCalls
    {
        public uint RegisterOneVar(int definition) => 7;
        public uint ClearDefinition(int definition) => 0;
        public uint AddToDefinition(int definition, string lvar) => 0;
        public uint SetValue(int definition, double value) => 0;
    }
}

/// <summary>CRO-40 against the real server: a write SimConnect refuses is a red line for both pilots, never silent.</summary>
[Collection("server")]
public sealed class CannotWriteEndToEndTests(ServerFixture server) : IAsyncLifetime
{
    private readonly List<BridgeRuntime> _bridges = [];
    private readonly string _dir = Repo.TempDir("crosscheck-cro40");
    private readonly HttpClient _http = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var b in _bridges) await b.DisposeAsync();
        _http.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static FakeSimAdapter Sim(BridgeRuntime b) => (FakeSimAdapter)b.Sim;

    private async Task<BridgeRuntime> Bridge(SessionMode? mode, string? code, string name)
    {
        var b = new BridgeRuntime(new BridgeOptions
        {
            Server = server.Url, ProfilePath = Repo.FixtureProfile, Mode = mode, JoinCode = code, Name = name, ApiPort = 0,
            LogDir = Path.Combine(_dir, name), VersionTimeout = TimeSpan.Zero, StrictValidation = true, UiDir = Path.Combine(_dir, "no-ui"),
        }, p => BridgeBackends.Fake(p));
        _bridges.Add(b);
        await b.StartAsync(CancellationToken.None);
        return b;
    }

    private async Task<JsonObject> Status(BridgeRuntime b) =>
        JsonNode.Parse(await _http.GetStringAsync($"http://127.0.0.1:{b.Api!.Port}/api/status"))!.AsObject();

    [Fact]
    public async Task A_write_SimConnect_refuses_blocks_the_var_and_is_one_red_cannot_write_line_for_both_pilots()
    {
        var host = await Bridge(SessionMode.Create, null, "host");
        var guest = await Bridge(SessionMode.Join, host.JoinCode, "guest");
        await Task.Delay(400);
        const string name = "S_OH_ELEC_APU_MASTER";
        Sim(guest).RejectDefinition(name);
        Sim(host).PilotSet(name, 1);

        foreach (var b in new[] { host, guest })
            await Wait.Until(() => b.Status.Desyncs.Any(d => d.Body["var"]?.GetValue<string>() == name && d.Body["reason"]?.GetValue<string>() == "cannot_write"),
                "red cannot_write line", 10000);
        Assert.True(guest.Sim.Guard.CannotWrite(name)); // blocked in the sim that refused it
        Assert.False(host.Sim.Guard.CannotWrite(name));
        Assert.False(guest.Engine!.CanSetBoth(name));

        // On the panel: one red line with no action ("set it by hand"), after the 2 s hold.
        JsonObject? line = null;
        for (var i = 0; i < 100 && line is null; i++)
        {
            line = (await Status(host))["panel"]!["red"]!.AsArray().FirstOrDefault(l => l!["control"]!.GetValue<string>() == name)?.AsObject();
            if (line is null) await Task.Delay(100);
        }
        Assert.NotNull(line);
        Assert.Equal("cannot_write", line!["kind"]!.GetValue<string>());
        Assert.Null(line["action"]);
        Assert.Equal("there", line["where"]!.GetValue<string>());

        // Back to the value the guest shows: the cockpits match, so the line clears (nothing for the pilots to do).
        Sim(host).PilotSet(name, 0);
        foreach (var b in new[] { host, guest })
            await Wait.Until(() => b.Status.Desyncs.All(d => d.Body["var"]?.GetValue<string>() != name), "matches again", 10000);
        // The next difference is red again at once: the var stays blocked in that sim, nothing is re-sent or retried.
        Sim(host).PilotSet(name, 1);
        foreach (var b in new[] { host, guest })
            await Wait.Until(() => b.Status.Desyncs.Any(d => d.Body["var"]?.GetValue<string>() == name && d.Body["reason"]?.GetValue<string>() == "cannot_write"),
                "red again", 10000);
        Assert.Single(Sim(guest).WritesOf(name)); // only the first attempt reached the (fake) SimConnect

        // In the log, with the var.
        var log = File.ReadAllText(guest.LogPath!);
        Assert.Contains("\"event\":\"write_rejected\"", log);
        Assert.Contains("\"event\":\"write_blocked\"", log);
    }

    [Fact]
    public async Task A_refused_setup_call_blocks_every_session_loudly()
    {
        var b = await Bridge(null, null, "solo");
        Sim(b).FailSetup("SimConnect refused read definition 3 (test)");
        var s = await Status(b);
        Assert.Equal("sim_setup_error", s["start_blocked"]!.GetValue<string>());
        Assert.Contains("Do not fly with Crosscheck", s["sim_setup_error"]!.GetValue<string>());
        var r = await b.CreateAsync();
        Assert.False(r.Ok);
        Assert.Contains("could not set up its link to the sim", r.Error);
        Assert.Null(b.Engine);
        Assert.Contains("\"event\":\"sim_setup_error\"", File.ReadAllText(b.LogPath!));
    }
}

/// <summary>CRO-40 in the engine: "Set both" only after a write landed, the cannot_write ack, limits and amber notices.</summary>
public sealed class WriteConfirmationTests
{
    private const string Apu = "S_OH_ELEC_APU_MASTER";

    [Fact]
    public async Task Set_both_is_offered_only_after_a_write_that_changed_the_value_landed_in_this_sim()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        Assert.False(h.Engine.CanSetBoth(Apu)); // nothing written yet in this session

        // The value the sim already shows proves nothing (gate test 3: "close ok, open failed").
        var same = h.Remote(Apu, "verified", 0);
        await Wait.Until(() => h.AckFor(same) is not null, "ack");
        Assert.True(h.AckFor(same)!["ok"]!.GetValue<bool>());
        Assert.False(h.Engine.CanSetBoth(Apu));

        var on = h.Remote(Apu, "verified", 1);
        await Wait.Until(() => h.AckFor(on) is not null, "ack");
        Assert.True(h.AckFor(on)!["ok"]!.GetValue<bool>());
        Assert.True(h.Engine.CanSetBoth(Apu));
    }

    [Fact]
    public async Task A_write_SimConnect_refuses_is_acked_cannot_write_and_reported_as_write_blocked()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: false);
        h.Sim.RejectDefinition(Apu);
        // What the runtime does on WriteRejected (BridgeRuntime.OnWriteRejected).
        h.Sim.WriteRejected += (name, why) =>
        {
            h.Sim.Guard.BlockCannotWrite(name, why);
            h.Engine.ReportCannotWrite(name, why);
        };
        var id = h.Remote(Apu, "verified", 1);
        await Wait.Until(() => h.AckFor(id) is not null, "ack");
        var ack = h.AckFor(id)!;
        Assert.False(ack["ok"]!.GetValue<bool>());
        Assert.Equal("cannot_write", ack["reason"]!.GetValue<string>());
        var blocked = Assert.Single(h.Channel.SentOf("write_blocked"));
        Assert.Equal(Apu, blocked["var"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(blocked["label"]!.GetValue<string>()));
        Assert.False(h.Engine.CanSetBoth(Apu));

        // The next write is refused by the guard at once, with the same reason.
        var again = h.Remote(Apu, "verified", 0);
        await Wait.Until(() => h.AckFor(again) is not null, "ack");
        Assert.Equal("cannot_write", h.AckFor(again)!["reason"]!.GetValue<string>());
        Assert.Single(h.Sim.WritesOf(Apu));
        Assert.Empty(h.Channel.Invalid);
    }

    [Fact]
    public async Task Snapshot_values_carry_the_profile_limits_so_the_server_never_corrects_outside_them()
    {
        await using var h = new EngineHarness();
        await h.StartAsync(pf: true);
        h.Channel.Inject("snapshot_request", new JsonObject { ["request_id"] = "r1", ["groups"] = null, ["reason"] = "join" }, h.Channel.NextSeq());
        var items = h.Channel.SentOf("snapshot_values").SelectMany(a => a["values"]!.AsArray()).Select(i => i!.AsObject()).ToList();
        var apu = items.Single(i => i["var"]!.GetValue<string>() == Apu);
        Assert.Equal([0.0, 1.0], apu["values"]!.AsArray().Select(v => v!.GetValue<double>()));
        var temp = items.Single(i => i["var"]!.GetValue<string>() == "A_OH_PNEUMATIC_COCKPIT_TEMP");
        Assert.Equal([0.0, 1.0], temp["range"]!.AsArray().Select(v => v!.GetValue<double>()));
        Assert.Empty(h.Channel.Invalid);
    }

    [Fact]
    public async Task An_indicator_with_a_notice_is_compared_loose_with_its_amber_text()
    {
        var fixture = Repo.LoadFixture();
        var profile = fixture with
        {
            Vars = fixture.Vars.Select(v => v.Name == "I_MIP_GEAR_1_L" ? v with { Tolerance = 1, Notice = "Gear differs – check it." } : v).ToList(),
        };
        await using var h = new EngineHarness(profile: profile);
        await h.StartAsync(pf: true);
        var loose = Assert.Single(h.Engine.LooseValues("mip"), l => l.Var == "I_MIP_GEAR_1_L");
        Assert.Equal("Gear differs – check it.", loose.Notice);
        Assert.Equal(1, loose.Tolerance);
        // Not in the exact hash any more: compared value by value.
        Assert.DoesNotContain(h.Engine.GroupValues("mip"), v => v.Var == "I_MIP_GEAR_1_L");
    }
}
