using System.Text.RegularExpressions;

namespace Crosscheck.Follower.Tests;

public class SafetyTests
{
    [Theory]
    [InlineData("PAUSE_ON")]
    [InlineData("PAUSE_TOGGLE")]
    [InlineData("SIM_RATE_INCR")]
    [InlineData("SIM_RATE_SET")]
    [InlineData("SLEW_TOGGLE")]
    [InlineData("FREEZE_LATITUDE_LONGITUDE_TOGGLE")]
    [InlineData("freeze_altitude_set")]
    [InlineData("")]
    public void Only_the_allowed_events_pass(string name) =>
        Assert.Throws<SafetyViolationException>(() => AllowedEvents.Check(name));

    [Fact]
    public void The_allowed_list_is_exactly_freeze_and_slew()
    {
        Assert.Equal(["FREEZE_LATITUDE_LONGITUDE_SET", "FREEZE_ALTITUDE_SET", "FREEZE_ATTITUDE_SET", "SLEW_ON", "SLEW_OFF"], AllowedEvents.All);
        Assert.Empty(AllowedEvents.Engage(Method.A));
        Assert.Equal(3, AllowedEvents.Engage(Method.B).Count);
        Assert.Equal(["SLEW_ON"], AllowedEvents.Engage(Method.C));
    }

    [Theory]
    [InlineData(Method.B)]
    [InlineData(Method.C)]
    public void Every_engaged_event_is_undone_exactly_once(Method method)
    {
        var sent = new List<(string, uint)>();
        var p = new PlacementState((e, d) => sent.Add((e, d)));
        foreach (var ev in AllowedEvents.Engage(method)) p.Engage(ev);
        var undone = p.Undo();
        Assert.Empty(p.Undo()); // second stop: nothing more
        Assert.True(p.IsUndone);
        var expectedUndo = AllowedEvents.Engage(method).Reverse().Select(AllowedEvents.UndoOf).ToArray();
        Assert.Equal(expectedUndo, undone);
        Assert.Equal(AllowedEvents.Engage(method).Select(e => (e, 1u)).Concat(expectedUndo), sent);
        Assert.Throws<InvalidOperationException>(() => p.Engage(AllowedEvents.SlewOn));
        Assert.False(p.Reassert(AllowedEvents.Engage(method)[0]));
    }

    [Fact]
    public void A_failed_undo_still_tries_the_others()
    {
        var sent = new List<string>();
        var p = new PlacementState((e, d) =>
        {
            if (d == 0 && e == AllowedEvents.FreezeAltitude) throw new InvalidOperationException("sim gone");
            sent.Add($"{e}={d}");
        });
        foreach (var ev in AllowedEvents.Engage(Method.B)) p.Engage(ev);
        Assert.Throws<AggregateException>(() => p.Undo());
        Assert.Contains($"{AllowedEvents.FreezeLatLon}=0", sent);
        Assert.Contains($"{AllowedEvents.FreezeAttitude}=0", sent);
        Assert.Empty(p.Undo());
    }

    [Fact]
    public void Undo_is_registered_before_the_event_is_sent()
    {
        // The send of the second freeze fails: the first one, and the failed one, are still undone.
        var sent = new List<string>();
        var p = new PlacementState((e, d) =>
        {
            if (d == 1 && e == AllowedEvents.FreezeAltitude) throw new InvalidOperationException("send failed");
            sent.Add($"{e}={d}");
        });
        p.Engage(AllowedEvents.FreezeLatLon);
        Assert.Throws<InvalidOperationException>(() => p.Engage(AllowedEvents.FreezeAltitude));
        p.Undo();
        Assert.Contains($"{AllowedEvents.FreezeLatLon}=0", sent);
        Assert.Contains($"{AllowedEvents.FreezeAltitude}=0", sent);
    }

    [Fact]
    public void Undo_runs_once_across_threads()
    {
        var count = 0;
        var p = new PlacementState((e, d) => { if (d == 0) Interlocked.Increment(ref count); });
        foreach (var ev in AllowedEvents.Engage(Method.B)) p.Engage(ev);
        Parallel.For(0, 16, _ => p.Undo());
        Assert.Equal(3, count);
    }

    [Fact]
    public void Slew_off_cannot_be_engaged_and_restore_switches_everything_off()
    {
        var p = new PlacementState((_, _) => { });
        Assert.Throws<SafetyViolationException>(() => p.Engage(AllowedEvents.SlewOff));
        Assert.Throws<SafetyViolationException>(() => p.Engage("PAUSE_ON"));
        Assert.All(AllowedEvents.RestoreAll, r => Assert.Equal(0u, r.Data));
        Assert.Contains(AllowedEvents.RestoreAll, r => r.Event == AllowedEvents.SlewOff);
    }

    [Theory]
    [InlineData("OFFLINE", true)]
    [InlineData("  OFFLINE \n", true)]
    [InlineData("offline", false)]
    [InlineData("yes", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Confirmation_needs_the_exact_word(string? input, bool ok)
    {
        Assert.Equal(ok, OfflineConfirmation.Accept(input));
        Assert.Contains("VATSIM", OfflineConfirmation.Prompt);
    }

    [Fact]
    public void Network_clients_are_found_by_process_name()
    {
        Assert.Equal(["vPilot"], NetworkClients.Find(["explorer", "vPilot", "FlightSimulator2024"]));
        Assert.Equal(["xpilot"], NetworkClients.Find(["xpilot", "xpilot"]));
        Assert.Empty(NetworkClients.Find(["explorer", "FlightSimulator2024", "Crosscheck"]));
    }

    [Fact]
    public void Args_reject_typos_and_bad_values()
    {
        var a = new Args(["rec.cxrec", "--method", "c", "--rate", "20", "--allow-air-end"], PlaybackArgs.Flags);
        var s = PlaybackArgs.Parse(a);
        a.EnsureAllUsed();
        Assert.Equal(Method.C, s.Method);
        Assert.Equal(20, s.RateHz);
        Assert.True(s.AllowAirEnd);
        Assert.Equal(["rec.cxrec"], a.Positionals);
        Assert.Equal(35, s.Network.LatencyMs);
        Assert.Equal(10, s.Network.JitterMs);
        Assert.Equal(100, s.Playout.BufferMs);

        var typo = new Args(["--latancy", "50"], PlaybackArgs.Flags);
        PlaybackArgs.Parse(typo);
        Assert.Throws<UsageException>(() => typo.EnsureAllUsed());
        Assert.Throws<UsageException>(() => PlaybackArgs.Parse(new Args(["--method", "D"], PlaybackArgs.Flags)));
        Assert.Throws<UsageException>(() => PlaybackArgs.Parse(new Args(["--rate", "500"], PlaybackArgs.Flags)));
        Assert.Throws<UsageException>(() => new Args(["--rate"], PlaybackArgs.Flags));
        Assert.Equal(Tangents.FiniteDifference, PlaybackArgs.Parse(new Args(["--tangents", "fd"], PlaybackArgs.Flags)).Playout.Tangents);
    }
}

/// <summary>Checks on the source text, like the bridge's SafetyTripwireTests.</summary>
public class TripwireTests
{
    private static IEnumerable<string> Sources(string project) =>
        Directory.GetFiles(Path.Combine(Repo.Follower, project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Theory]
    [InlineData("Follower.Core")]
    [InlineData("Follower.Cli")]
    public void Core_and_cli_cannot_touch_a_sim(string project)
    {
        foreach (var file in Sources(project).Append(Path.Combine(Repo.Follower, project, project + ".csproj")))
        {
            var text = File.ReadAllText(file);
            foreach (var word in new[] { "Microsoft.FlightSimulator", "SetDataOnSimObject", "TransmitClientEvent", "MapClientEventToSimEvent", "DllImport", "LibraryImport" })
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{Path.GetFileName(file)} contains {word}");
        }
    }

    [Fact]
    public void Recorder_is_read_only()
    {
        var text = File.ReadAllText(Path.Combine(Repo.Follower, "Follower.Sim", "Recorder.cs"));
        foreach (var word in new[] { "SetDataOnSimObject", "TransmitClientEvent", "MapClientEventToSimEvent", "SetClientData", "MapAllowedEvents", "SimLink.Send", "SimLink.Place", "DefinePlacement", "PlacementState", "AllowedEvents" })
            Assert.False(text.Contains(word, StringComparison.Ordinal), $"Recorder.cs contains {word}");
    }

    [Fact]
    public void Sim_host_never_pauses_or_changes_sim_rate()
    {
        foreach (var file in Sources("Follower.Sim"))
        {
            var text = File.ReadAllText(file);
            foreach (var word in new[] { "PAUSE", "SIM_RATE", "SIM RATE" })
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(file)} contains {word}");
            // Event names only ever come from AllowedEvents, never as string literals.
            Assert.False(Regex.IsMatch(text, "\"(FREEZE|SLEW|K:)[A-Z_]*\""), $"{Path.GetFileName(file)} has an event name literal");
        }
    }

    [Fact]
    public void Sim_host_maps_and_sends_events_in_one_guarded_place()
    {
        var all = Sources("Follower.Sim").ToDictionary(f => Path.GetFileName(f), File.ReadAllText);
        int Count(string word) => all.Values.Sum(t => Regex.Matches(t, Regex.Escape(word)).Count);
        Assert.Equal(1, Count("MapClientEventToSimEvent("));
        Assert.Equal(1, Count("TransmitClientEvent("));
        Assert.Equal(4, Count("SetDataOnSimObject(")); // SimLink.Place (position only, mixed, body/world) and SimLink.PlaceExtra
        var link = all["SimLink.cs"];
        Assert.Contains("sc.MapClientEventToSimEvent((Ev)(uint)i, AllowedEvents.Check(AllowedEvents.All[i]));", link);
        Assert.Contains("IndexOf(AllowedEvents.Check(name))", link);
        Assert.All(all.Where(kv => kv.Key != "SimLink.cs"), kv =>
        {
            Assert.DoesNotContain("TransmitClientEvent", kv.Value);
            Assert.DoesNotContain("MapClientEventToSimEvent", kv.Value);
            Assert.DoesNotContain("SetDataOnSimObject", kv.Value);
        });
    }

    [Fact]
    public void Player_asks_for_the_offline_confirmation_before_connecting()
    {
        var program = File.ReadAllText(Path.Combine(Repo.Follower, "Follower.Sim", "Program.cs"));
        var confirm = program.IndexOf("OfflineConfirmation.Accept", StringComparison.Ordinal);
        var player = program.IndexOf("new Player(", StringComparison.Ordinal);
        var clients = program.IndexOf("NetworkClients.Find", StringComparison.Ordinal);
        Assert.True(confirm > 0 && player > confirm && clients > 0 && clients < confirm);
    }

    [Fact]
    public void Nothing_in_the_bridge_references_the_spike()
    {
        var bridge = Path.Combine(Repo.Root, "bridge");
        var files = Directory.GetFiles(bridge, "*.csproj", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(bridge, "*.sln", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(bridge, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")));
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.False(text.Contains("Crosscheck.Follower", StringComparison.Ordinal) || text.Contains("spike/Follower", StringComparison.Ordinal) || text.Contains(@"spike\Follower", StringComparison.Ordinal),
                $"{Path.GetRelativePath(Repo.Root, f)} references the follower spike");
        }
    }
}
