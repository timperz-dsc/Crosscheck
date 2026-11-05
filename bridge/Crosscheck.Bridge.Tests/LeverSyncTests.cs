using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Follow;
using Crosscheck.Bridge.Logging;

namespace Crosscheck.Bridge.Tests;

/// <summary>Thrust levers on the follower (0.6.2, owner decision 2026-11-05): range [0, 5], band 0.005, own hardware.</summary>
public class LeverFollowerTests
{
    private const string L = "A_FC_THROTTLE_LEFT_INPUT", R = "A_FC_THROTTLE_RIGHT_INPUT";
    private readonly ManualClock _clock = new();
    private readonly LeverFollower _f;

    public LeverFollowerTests() => _f = new LeverFollower([new LeverSpec(L, 0, 5), new LeverSpec(R, 0, 5)], _clock);

    private static Dictionary<string, double> Own(double left, double right = 2) => new() { [L] = left, [R] = right };
    private static Dictionary<string, double> Pf(double left, double right = 2) => new() { [L.ToLowerInvariant()] = left, [R.ToLowerInvariant()] = right };
    private LeverStep Step(double own, double pf, bool write = true) => _f.Step(Own(own), Pf(pf), write, _ => true);

    [Theory]
    [InlineData(5.4, 5)]
    [InlineData(-0.2, 0)]
    [InlineData(3.5, 3.5)]
    public void The_PF_value_is_clamped_to_the_range(double pf, double written)
    {
        var s = Step(2, pf);
        Assert.Equal([(L, written)], s.Writes);
    }

    [Fact]
    public void Only_a_difference_above_the_noise_band_is_written()
    {
        Assert.Empty(Step(3, 3.003).Writes);
        Assert.Equal([(L, 3.01)], Step(3, 3.01).Writes);
    }

    [Fact]
    public void Our_own_write_read_back_is_not_a_hardware_move()
    {
        Step(2, 4);
        var s = Step(4, 4);
        Assert.Empty(s.Moved);
        Assert.Empty(s.Writes);
        Assert.Null(_f.Hardware(L));
    }

    [Fact]
    public void A_hardware_move_is_remembered_and_the_PF_value_is_written_back_at_once()
    {
        Step(2, 4);
        Step(4, 4);
        var s = Step(1, 4); // the pilot's hardware
        Assert.Equal([(L, 1.0)], s.Moved);
        Assert.Equal(1, _f.Hardware(L));
        Assert.Equal([(L, 4.0)], s.Writes);
        Assert.Empty(Step(4, 4).Moved); // the write-back read back: ours
        Assert.Equal(1, _f.Hardware(L)); // the hardware is still where the pilot left it
    }

    [Fact]
    public void A_write_still_on_its_way_is_not_sent_again_until_its_window_ends()
    {
        Assert.Single(Step(2, 4).Writes);
        Assert.Empty(Step(2, 4).Writes);
        _clock.Advance(LeverFollower.EchoWindowMs + 1);
        Assert.Single(Step(2, 4).Writes);
    }

    [Fact]
    public void Nothing_is_written_when_not_allowed_or_without_a_range()
    {
        Assert.Empty(Step(2, 4, write: false).Writes);
        Assert.Empty(_f.Step(Own(2), Pf(4), true, _ => false).Writes);
        var noRange = new LeverFollower([new LeverSpec(L, null, null)], _clock);
        Assert.Empty(noRange.Step(Own(2), Pf(4), true, _ => true).Writes);
    }

    [Fact]
    public void Hardware_is_noted_without_writing_and_forgotten_by_a_new_session()
    {
        Step(2, 4, write: false);
        Assert.Equal([(L, 3.0)], Step(3, 4, write: false).Moved);
        Assert.Equal(3, _f.Hardware(L));
        _f.Reset();
        Assert.Null(_f.Hardware(L));
    }
}

public class LeverDetentTests
{
    private const string Note = "label generated; thrust lever: the PF streams it, the follower writes it (CRO-13); detents MAX REV 0, REV IDLE 1, IDLE 2, CL 3, FLX/MCT 4, TOGA 5";

    [Fact]
    public void Detents_come_from_the_profile_note()
    {
        var d = LeverDetents.Parse(Note);
        Assert.Equal(["MAX REV", "REV IDLE", "IDLE", "CL", "FLX/MCT", "TOGA"], d.Select(x => x.Name));
        Assert.Equal([0.0, 1, 2, 3, 4, 5], d.Select(x => x.Value));
        Assert.Empty(LeverDetents.Parse("no list here"));
    }

    [Theory]
    [InlineData(3.02, "CL")]
    [InlineData(5, "TOGA")]
    [InlineData(0.09, "MAX REV")]
    [InlineData(3.4, "3.4")]
    public void A_value_near_a_detent_is_called_by_its_name(double value, string name) =>
        Assert.Equal(name, LeverDetents.NameOf(LeverDetents.Parse(Note), value));
}

/// <summary>The follower writes the levers, the PF never does, and the takeover checks use the pilot's hardware.</summary>
public sealed class LeverSyncTests : IAsyncLifetime
{
    private const string L = "A_FC_THROTTLE_LEFT_INPUT", R = "A_FC_THROTTLE_RIGHT_INPUT";
    private static readonly IReadOnlyList<PhysicsFrame> Parked = Synthetic.From(Synthetic.Circuit(), 0);
    private readonly FakePhysicsLink _link = new();
    private readonly EventHub _hub = new();
    private readonly ManualClock _clock = new();
    private readonly string _logs = Repo.TempDir("crosscheck-lever-logs");
    private readonly List<(string Var, double Value)> _writes = [];
    private readonly Dictionary<string, double> _pf = new() { [L] = 2, [R] = 2 };
    private BridgeLog _log = null!;
    private PhysicsSync _sync = null!;
    private CancellationTokenSource _feed = new();

    public Task InitializeAsync()
    {
        _log = new BridgeLog(_logs);
        _sync = new PhysicsSync(_link, _log, _hub, new PhysicsOptions { StatsEveryS = 0 },
            new Dictionary<string, double> { [L] = 0.1, [R] = 0.1 },
            name => name is L or R, Write,
            [new LeverSpec(L, 0, 5), new LeverSpec(R, 0, 5)],
            LeverDetents.Parse("detents MAX REV 0, REV IDLE 1, IDLE 2, CL 3, FLX/MCT 4, TOGA 5"), _clock);
        _link.SetLvar(L, 2);
        _link.SetLvar(R, 2);
        _link.Start([L, R]);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _feed.Cancel();
        await _sync.DisposeAsync();
        try { Directory.Delete(_logs, recursive: true); } catch (IOException) { }
    }

    /// <summary>The sim stores what is written: the follower's own lever reads it back on the next frame.</summary>
    private Task Write(string name, double value)
    {
        lock (_writes) _writes.Add((name, value));
        _link.SetLvar(name, value);
        return Task.CompletedTask;
    }

    private List<(string Var, double Value)> Writes()
    {
        lock (_writes) return [.. _writes];
    }

    private void SetPf(double left, double right)
    {
        lock (_pf) { _pf[L] = left; _pf[R] = right; }
    }

    private void Feed()
    {
        _feed.Cancel();
        _feed = new CancellationTokenSource();
        var ct = _feed.Token;
        _ = Task.Run(async () =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long n = 0;
            while (!ct.IsCancellationRequested)
            {
                var t = sw.Elapsed.TotalSeconds;
                Dictionary<string, double> levers;
                lock (_pf) levers = new Dictionary<string, double>(_pf);
                _sync.OnStream(++n, StreamFields.ToWire(Interpolator.Sample(Parked, t) with { T = t }, levers));
                await Task.Delay(33);
            }
        }, ct);
    }

    private async Task Following(double left, double right)
    {
        SetPf(left, right);
        _sync.StartSession((_, _) => { });
        _sync.SetRole(isPf: false);
        Feed();
        await Wait.Until(() => _sync.Mode == FollowMode.Following, "following");
    }

    private List<JsonObject> Events(string name)
    {
        var list = new List<JsonObject>();
        if (_log.Path is null || !File.Exists(_log.Path)) return list;
        using var fs = new FileStream(_log.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var r = new StreamReader(fs);
        while (r.ReadLine() is { } line)
            if (line.Contains($"\"{name}\"") && JsonNode.Parse(line)!.AsObject() is { } o && o["event"]?.GetValue<string>() == name) list.Add(o);
        return list;
    }

    private double Own(string name) => _link.ReadLvar(name);

    /// <summary>Both levers read <paramref name="left"/>/<paramref name="right"/>, and the bridge has seen a few frames since.</summary>
    private async Task Written(double left, double right)
    {
        await Wait.Until(() => Own(L) == left && Own(R) == right, $"levers at {left}/{right}");
        var n = _sync.FramesPlaced;
        await Wait.Until(() => _sync.FramesPlaced >= n + 3, "frames after the write");
    }

    [Fact]
    public async Task The_follower_writes_both_levers_from_the_PF_on_the_ground_clamped_to_the_range()
    {
        await Following(3, 4);
        await Wait.Until(() => Own(L) == 3 && Own(R) == 4, "levers written");
        Assert.True(Parked[0].OnGround && Parked[0].GroundSpeedKt < 1);
        var on = Assert.Single(Events("follow_levers"));
        Assert.True(on["on"]!.GetValue<bool>());
        SetPf(6, -1); // outside the range: clamped, never written outside it
        await Wait.Until(() => Own(L) == 5 && Own(R) == 0, "clamped levers");
        Assert.All(Writes(), w => Assert.InRange(w.Value, 0, 5));
        Assert.Null(_sync.LeverHardware(L)); // our own writes are not the pilot's hardware
        Assert.False(_sync.StatusJson()["levers_moved"]!.GetValue<bool>());
    }

    [Fact]
    public async Task The_PF_never_writes_a_lever()
    {
        _link.SetLvar(L, 3);
        _sync.StartSession((_, _) => { });
        _sync.SetRole(isPf: true);
        SetPf(5, 5);
        Feed(); // a stream arriving at the PF is ignored
        await Wait.Until(() => _sync.FramesSent > 20, "streaming");
        Assert.Empty(Writes());
        Assert.Empty(Events("follow_levers"));
    }

    [Fact]
    public async Task A_hardware_move_is_written_back_and_shown_once_per_30_s()
    {
        await Following(3, 3);
        await Written(3, 3);
        _link.SetLvar(L, 1); // the follower pilot's hardware
        await Wait.Until(() => _sync.LeverHardware(L) == 1 && Own(L) == 3, "written back");
        Assert.True(_sync.StatusJson()["levers_moved"]!.GetValue<bool>());
        Assert.Single(Events("lever_hardware"));

        _link.SetLvar(L, 0.5);
        await Wait.Until(() => _sync.LeverHardware(L) == 0.5 && Own(L) == 3, "written back again");
        Assert.Single(Events("lever_hardware")); // not again within 30 s

        _clock.Advance(10_001);
        Assert.False(_sync.StatusJson()["levers_moved"]!.GetValue<bool>()); // the line is gone after 10 s
        _clock.Advance(20_000);
        _link.SetLvar(L, 4);
        await Wait.Until(() => Events("lever_hardware").Count == 2, "a second notice after 30 s");
    }

    [Fact]
    public async Task Take_control_compares_the_PF_lever_with_the_remembered_hardware()
    {
        await Following(3, 3);
        await Written(3, 3);
        // Never seen the hardware move: both listed, with the PF's detent.
        var never = _sync.LeverDifferences();
        Assert.Equal(2, never.Count);
        Assert.All(never, d => Assert.Equal((3.0, (double?)null, "CL"), (d.Pf, d.Mine, d.Detent)));

        _link.SetLvar(L, 2);
        _link.SetLvar(R, 3.08);
        await Wait.Until(() => _sync.LeverHardware(L) == 2 && _sync.LeverHardware(R) == 3.08,
            () => $"hardware seen: left {_sync.LeverHardware(L)} right {_sync.LeverHardware(R)}, sim {Own(L)} {Own(R)}, writes {string.Join(" ", Writes())}");
        var d = Assert.Single(_sync.LeverDifferences()); // the right lever is within 0.1
        Assert.Equal((L, 3.0, (double?)2.0), (d.Var, d.Pf, d.Mine));

        _link.SetLvar(L, 2.95);
        await Wait.Until(() => _sync.LeverDifferences().Count == 0, "levers match");
        Assert.Equal(3, Own(L)); // the sim still shows the PF's lever
    }

    [Fact]
    public async Task Writing_stops_with_a_log_line_when_this_side_becomes_PF()
    {
        await Following(3, 3);
        await Wait.Until(() => Events("follow_levers").Count == 1, "levers on");
        _sync.SetRole(isPf: true);
        var off = Events("follow_levers")[^1];
        Assert.False(off["on"]!.GetValue<bool>());
        Assert.Equal("became_pf", off["cause"]!.GetValue<string>());
    }

    [Fact]
    public async Task After_a_sidestick_takeover_the_hardware_must_match_where_the_aircraft_lever_was()
    {
        await Following(2, 2); // the PF at IDLE
        await Wait.Until(() => Events("follow_levers").Count == 1, "levers on");
        _link.SetLvar(L, 3);
        _link.SetLvar(R, 3); // this pilot's hardware at CL
        await Wait.Until(() => _sync.LeverHardware(L) == 3 && _sync.LeverHardware(R) == 3 && Own(L) == 2 && Own(R) == 2, "hardware at CL, written back");

        _sync.OnTakeoverPressed();
        _sync.SetRole(isPf: true);
        StopFeed();
        var writes = Writes().Count;
        var open = _sync.TakeoverLevers();
        Assert.Equal(2, open.Count);
        Assert.All(open, t => Assert.Equal((2.0, "IDLE", (double?)3.0, "CL"), (t.Aircraft, t.AircraftDetent, t.Mine, t.MineDetent)));
        var status = _sync.StatusJson()["takeover_levers"]!.AsArray();
        Assert.Equal("IDLE", status[0]!["aircraft_detent"]!.GetValue<string>());
        Assert.True(Events("takeover_levers")[0]["on"]!.GetValue<bool>());

        _link.SetLvar(L, 2.04);
        await Wait.Until(() => _sync.TakeoverLevers().Count == 1, "left matched");
        _link.SetLvar(R, 1.95);
        await Wait.Until(() => _sync.TakeoverLevers().Count == 0, "both matched");
        await Wait.Until(() => Events("takeover_levers").Count == 2, "check ended");
        Assert.Equal("matched", Events("takeover_levers")[1]["cause"]!.GetValue<string>());
        Assert.Equal(writes, Writes().Count); // the new PF writes nothing
    }

    [Fact]
    public async Task After_a_sidestick_takeover_with_hardware_never_seen_the_pilot_is_asked_to_move_the_levers()
    {
        await Following(4, 4);
        await Written(4, 4);
        _sync.OnTakeoverPressed();
        _sync.SetRole(isPf: true);
        var open = _sync.TakeoverLevers();
        Assert.Equal(2, open.Count);
        Assert.All(open, t => Assert.Equal((4.0, "FLX/MCT", (double?)null), (t.Aircraft, t.AircraftDetent, t.Mine)));
    }

    [Fact]
    public async Task Becoming_PF_without_a_takeover_press_has_no_lever_check()
    {
        await Following(4, 4);
        await Written(4, 4);
        _sync.SetRole(isPf: true);
        Assert.Empty(_sync.TakeoverLevers());
        Assert.Empty(Events("takeover_levers"));
    }

    private void StopFeed() => _feed.Cancel();
}
