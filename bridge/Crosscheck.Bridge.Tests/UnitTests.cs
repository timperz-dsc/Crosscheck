using System.Text.Json;
using System.Text.Json.Nodes;
using Crosscheck.Bridge.Fenix;
using Crosscheck.Bridge.Logging;
using Crosscheck.Bridge.Protocol;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles;

namespace Crosscheck.Bridge.Tests;

public class EchoTrackerTests
{
    private readonly ManualClock _clock = new();

    [Fact]
    public void Own_write_is_consumed_once()
    {
        var echo = new EchoTracker(_clock);
        echo.Expect("S_X", 1, 1000);
        Assert.True(echo.TryConsume("S_X", 1));
        Assert.False(echo.TryConsume("S_X", 1)); // a second identical change is not ours
        Assert.Equal(1, echo.Suppressed);
    }

    [Fact]
    public void Expired_entries_do_not_suppress_a_pilot_action()
    {
        var echo = new EchoTracker(_clock);
        echo.Expect("S_X", 1, 1000);
        _clock.Advance(1001);
        Assert.False(echo.TryConsume("S_X", 1));
    }

    [Fact]
    public void A_non_matching_change_clears_the_var_so_a_later_pilot_action_is_seen()
    {
        var echo = new EchoTracker(_clock);
        echo.Expect("S_X", 1, 1000);
        Assert.False(echo.TryConsume("S_X", 2)); // something else changed it
        Assert.False(echo.TryConsume("S_X", 1)); // the pilot setting 1 now is a real action
        Assert.Equal(0, echo.Pending("S_X"));
    }

    [Fact]
    public void Pulse_pair_and_skipped_frames()
    {
        var echo = new EchoTracker(_clock);
        echo.Expect("S_KEY", 1, 1000);
        echo.Expect("S_KEY", 0, 1000);
        Assert.True(echo.TryConsume("S_KEY", 1));
        Assert.True(echo.TryConsume("S_KEY", 0));

        // Frames coalesced: only the last of three writes is seen; it consumes the older ones too.
        echo.Expect("E_ENC", 101, 1000);
        echo.Expect("E_ENC", 102, 1000);
        echo.Expect("E_ENC", 103, 1000);
        Assert.True(echo.TryConsume("E_ENC", 103));
        Assert.Equal(0, echo.Pending("E_ENC"));
    }

    [Fact]
    public void Vars_are_independent()
    {
        var echo = new EchoTracker(_clock);
        echo.Expect("S_A", 1, 1000);
        Assert.False(echo.TryConsume("S_B", 1));
        Assert.True(echo.TryConsume("S_A", 1));
    }
}

public class CanonicalHashTests
{
    [Fact]
    public void Matches_the_shared_vectors_from_the_server()
    {
        var path = Path.Combine(Repo.Root, "protocol", "test-vectors", "canonical-hash.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var cases = doc.RootElement.GetProperty("cases").EnumerateArray().ToList();
        Assert.True(cases.Count > 5);
        foreach (var c in cases)
        {
            var values = c.GetProperty("values").EnumerateArray().Select(v => (
                v.GetProperty("var").GetString()!,
                v.GetProperty("value").ValueKind switch
                {
                    JsonValueKind.Number => (object?)v.GetProperty("value").GetDouble(),
                    JsonValueKind.String => v.GetProperty("value").GetString(),
                    _ => null,
                })).ToList();
            var formatted = c.GetProperty("formatted").EnumerateArray().Select(f => f.GetString()!).ToList();
            Assert.Equal(formatted, values.Select(v => CanonicalHash.FormatValue(v.Item2)).ToList());
            Assert.Equal(c.GetProperty("hash").GetString(), CanonicalHash.Compute(values));
        }
    }

    [Fact]
    public void Minus_zero_and_float_noise()
    {
        Assert.Equal("0", CanonicalHash.FormatValue(-0.0));
        Assert.Equal("0.3", CanonicalHash.FormatValue(0.1 + 0.2));
        Assert.Equal("-0.5", CanonicalHash.FormatValue(-0.5));
        Assert.Equal("0", CanonicalHash.FormatValue(-0.0000001));
    }
}

public class SafetyGuardTests
{
    private readonly Crosscheck.Profiles.Model.Profile _profile = Repo.LoadFenix();

    [Theory]
    [InlineData("S_OH_EXT_LT_NAV_LOGO", 1)] // not in the profile
    [InlineData("I_OH_ELEC_APU_MASTER_L", 1)] // observe: never written
    [InlineData("S_OH_ELEC_APU_MASTER", 2)] // not in values [0, 1]
    [InlineData("A_OH_PNEUMATIC_COCKPIT_TEMP", 1.5)] // outside range [0, 1]
    [InlineData("S_CDU1_KEY_FPLN", 2)] // a pulse writes only 1 and 0
    [InlineData("S_OH_ELEC_APU_MASTER", double.NaN)]
    [InlineData("A:PLANE LATITUDE", 0)] // not an L: var name
    [InlineData("K:PAUSE_ON", 1)]
    public void Refuses(string name, double value)
    {
        var guard = new SimSafetyGuard(_profile);
        Assert.Throws<SafetyViolationException>(() => guard.CheckWrite(name, value));
        Assert.Equal(1, guard.Refused);
    }

    [Theory]
    [InlineData("S_OH_ELEC_APU_MASTER", 1)]
    [InlineData("S_FCU_EFIS1_ND_MODE", 4)]
    [InlineData("A_OH_PNEUMATIC_COCKPIT_TEMP", 0.37)]
    [InlineData("E_FCU_ALTITUDE", 12345)] // encoder counters are free
    [InlineData("S_CDU1_KEY_FPLN", 1)]
    public void Allows(string name, double value) => new SimSafetyGuard(_profile).CheckWrite(name, value);

    [Fact]
    public async Task The_fake_adapter_enforces_the_guard_itself()
    {
        var sim = new FakeSimAdapter(_profile);
        await sim.StartAsync(new SimPlan(["A_OH_PNEUMATIC_COCKPIT_TEMP"]), CancellationToken.None);
        await Assert.ThrowsAsync<SafetyViolationException>(() => sim.WriteAsync("A_OH_PNEUMATIC_COCKPIT_TEMP", 3));
        Assert.Empty(sim.Writes);
        Assert.Equal(0, sim.Get("A_OH_PNEUMATIC_COCKPIT_TEMP"));
        await sim.DisposeAsync();
    }

    [Fact]
    public void Stream_writer_is_only_active_on_the_non_PF_side()
    {
        var writer = new GuardedStreamWriter();
        var frame = new Dictionary<string, double> { ["lat"] = 47.4 };
        Assert.False(writer.Apply(frame)); // no role yet: refuse
        writer.SetRole(isPf: true);
        Assert.False(writer.Apply(frame));
        writer.SetRole(isPf: false);
        Assert.True(writer.Apply(frame));
        Assert.Equal(2, writer.Refused);
        Assert.Equal(1, writer.Applied);
    }
}

public class SafetyTripwireTests
{
    /// <summary>The SimConnect adapter must never map or send sim events (pause, sim rate, slew, position).</summary>
    [Fact]
    public void SimConnect_adapter_contains_no_event_APIs()
    {
        var source = File.ReadAllText(Path.Combine(Repo.Root, "bridge", "Crosscheck.Bridge.SimConnect", "SimConnectAdapter.cs"));
        foreach (var forbidden in new[] { "TransmitClientEvent", "MapClientEventToSimEvent", "MapInputEventToClientEvent", "PAUSE", "SIM_RATE", "SLEW", "PLANE LATITUDE", "PLANE LONGITUDE", "FREEZE" })
            Assert.DoesNotContain(forbidden, source, StringComparison.OrdinalIgnoreCase);
        // Every write goes through the guard.
        Assert.Contains("Guard.CheckWrite(name, value);", source);
    }
}

public class MessageValidatorTests
{
    [Fact]
    public void Every_protocol_example_is_valid_and_every_type_has_one()
    {
        var dir = Path.Combine(Repo.Root, "protocol", "examples");
        var files = Directory.GetFiles(dir, "*.json");
        Assert.Equal(MessageValidator.Shared.Types.Order(), files.Select(f => Path.GetFileNameWithoutExtension(f)).Order());
        foreach (var file in files)
            Assert.Null(MessageValidator.Shared.Validate(JsonNode.Parse(File.ReadAllText(file))!));
    }

    [Fact]
    public void Rejects_malformed_messages()
    {
        var bad = JsonNode.Parse("""{"v":1,"type":"command","session":"s_abcdefg","ts":1,"body":{"id":"a","var":"X","class":"delta","value":1,"group":"g","settle_ms":1}}""")!;
        Assert.NotNull(MessageValidator.Shared.Validate(bad));
        var unknown = JsonNode.Parse("""{"v":1,"type":"nope","session":null,"ts":1,"body":{}}""")!;
        Assert.NotNull(MessageValidator.Shared.Validate(unknown));
    }
}

public class AircraftVersionTests
{
    private readonly FakeFenixGraphQl _gql = new(new FakeSimAdapter(Repo.LoadFenix()));

    [Fact]
    public async Task Reads_both_versions_without_warnings()
    {
        var v = await AircraftVersionReader.ReadAsync(_gql, new FixedPackageVersion("3.0.0.7613"), ["3.0.0.7613"], TimeSpan.Zero, TimeSpan.FromMilliseconds(10), default);
        Assert.Equal("3.0.0.7613", v.PackageVersion);
        Assert.Equal(FakeFenixGraphQl.FakeSoftwareVersion, v.AppVersion);
        Assert.Empty(v.Warnings);
    }

    [Fact]
    public async Task Unreadable_values_are_sent_as_unknown_with_a_warning_never_as_the_profile_build()
    {
        _gql.Available = false;
        var v = await AircraftVersionReader.ReadAsync(_gql, new FixedPackageVersion(null), ["3.0.0.7613"], TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10), default);
        Assert.Equal("unknown", v.PackageVersion);
        Assert.Equal("unknown", v.AppVersion);
        Assert.Equal(2, v.Warnings.Count);
        Assert.Contains(v.Warnings, w => w.Contains("app version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_build_the_profile_was_not_made_for_gives_a_warning()
    {
        var v = await AircraftVersionReader.ReadAsync(_gql, new FixedPackageVersion("3.1.0.8000"), ["3.0.0.7613"], TimeSpan.Zero, TimeSpan.FromMilliseconds(10), default);
        Assert.Equal("3.1.0.8000", v.PackageVersion);
        Assert.Single(v.Warnings);
        Assert.Contains("not one this profile was made for", v.Warnings[0]);
    }
}

public class NonAsciiPathTests
{
    [Fact]
    public void Profile_and_log_work_in_a_folder_with_åäö()
    {
        var dir = Path.Combine(Repo.TempDir("crosscheck-ø"), "Prøfil åäö");
        Directory.CreateDirectory(dir);
        try
        {
            var profile = Path.Combine(dir, "profile åäö.yaml");
            File.Copy(Repo.FenixProfile, profile);
            var loaded = ProfileLoader.LoadFile(profile);
            Assert.True(loaded.IsValid, string.Join("\n", loaded.FormatIssues()));

            var log = new BridgeLog(Path.Combine(dir, "logs ä"));
            log.Event("test", new JsonObject { ["text"] = "Prøfil åäö" });
            var line = File.ReadAllText(log.Path!);
            Assert.Contains("Prøfil åäö", JsonNode.Parse(line)!["text"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);
        }
    }

    [Fact]
    public void Mcdu_screen_text_strips_style_letters()
    {
        var screen = McduScreen.Parse("<root><title>       MCDU MENU        </title><line>g&lt;FMGCw            CONFIG&gt;</line></root>")!;
        Assert.Equal(["       MCDU MENU        ", "<FMGC            CONFIG>"], screen.Rows);
        Assert.Matches("^[0-9a-f]{16}$", screen.Hash);
    }
}
