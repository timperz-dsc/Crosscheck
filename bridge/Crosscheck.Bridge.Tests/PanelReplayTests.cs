using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crosscheck.Bridge.Api;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// CRO-34: the gate test 2 stream through the panel builder, second by second. One line per control, red
/// only for the real, lasting differences of the diagnosis, every red line with its action, corrections in
/// the log only. Also writes the frames the UI tests and screenshots use (ui/src/test/gate-test-2-frames.json).
/// </summary>
public class PanelReplayTests
{
    private static readonly JsonObject RealRed = JsonNode.Parse(
        File.ReadAllText(Path.Combine(Repo.Root, "server", "test", "fixtures", "gate-test-2.json")))!["panel_real_red"]!.AsObject();

    private static readonly Regex RawName = new(@"\b[A-Z]{1,3}_[A-Z0-9]+_[A-Z0-9_]+\b");

    /// <summary>Every second of the session (and 3 s after), with the panel of that moment.</summary>
    private static IEnumerable<(long T, JsonObject Panel, PanelReplay Replay)> Seconds()
    {
        var r = new PanelReplay();
        for (long t = 0; t <= r.EndMs + 3000; t += 1000)
        {
            r.AdvanceTo(t);
            yield return (t, r.Panel(t), r);
        }
    }

    private static List<string> Controls(JsonObject panel, string section) => section switch
    {
        "red" => panel["red"]!.AsArray().Select(l => l!["control"]!.GetValue<string>()).ToList(),
        _ => panel[section]!["items"]!.AsArray().Select(l => l!["control"]!.GetValue<string>()).ToList(),
    };

    [Fact]
    public void At_most_one_line_per_control_at_every_second()
    {
        foreach (var (t, panel, _) in Seconds())
        {
            var all = new List<string>();
            foreach (var section in new[] { "red", "outside", "indicators" })
            {
                var c = Controls(panel, section);
                Assert.True(c.Count == c.Distinct().Count(), $"{section} lists a control twice at {t} ms: {string.Join(", ", c)}");
                all.AddRange(c);
            }
            Assert.True(all.Count == all.Distinct().Count(), $"a control is in two sections at {t} ms: {string.Join(", ", all)}");
            var notSynced = Controls(panel, "not_synced");
            Assert.Equal(notSynced.Count, notSynced.Distinct().Count());
        }
    }

    [Fact]
    public void Red_only_for_the_real_lasting_differences_and_never_more_than_seven()
    {
        var allowed = RealRed["controls"]!.AsObject().Select(p => p.Key).ToHashSet();
        var max = RealRed["max_at_once"]!.GetValue<int>();
        var peak = 0;
        var seen = new HashSet<string>();
        foreach (var (t, panel, r) in Seconds())
        {
            var red = panel["red"]!.AsArray();
            peak = Math.Max(peak, red.Count);
            foreach (var line in red)
            {
                var control = line!["control"]!.GetValue<string>();
                seen.Add(control);
                Assert.True(allowed.Contains(control), $"false red at {PanelReplay.Start.AddMilliseconds(t):HH:mm:ss}: {control} ({line["reason"]})");
                // Never red: walkaround items, an indicator on its own, a var this profile does not sync.
                Assert.False(r.Controls.Outside(control), control);
                Assert.True(r.Controls.IsKnown(control) || PanelReplay.Real.Controls?.ContainsKey(control) == true, control);
                Assert.NotEqual(SyncClass.Observe, r.Controls.Spec(control)?.Class);
                Assert.NotEqual("unknown", line["kind"]!.GetValue<string>());
            }
        }
        Assert.True(peak <= max, $"{peak} red lines at once (max {max}; the old panel showed 19)");
        Assert.Equal(max, peak); // tightened to what the replay gives; a change here is a change of behaviour
        // Every real control from the diagnosis is still shown: nothing real was hidden.
        Assert.Empty(allowed.Except(seen));
    }

    [Fact]
    public void Every_red_line_has_one_action_or_a_plain_instruction_and_no_raw_names()
    {
        string[] kinds = ["set_both", "lamp", "turn", "release", "not_mirrored", "by_hand", "held", "released", "press_lost", "mode", "cannot_write"];
        foreach (var (t, panel, _) in Seconds())
        {
            foreach (var line in panel["red"]!.AsArray())
            {
                var kind = line!["kind"]!.GetValue<string>();
                Assert.Contains(kind, kinds);
                var action = line["action"]?["type"]?.GetValue<string>();
                // Set both and Press here are buttons; Release it only in the cockpit where it is stuck.
                if (kind == "set_both") Assert.Equal("set_both", action);
                if (kind is "turn" or "not_mirrored" or "by_hand" or "held" or "released" or "mode" or "cannot_write") Assert.Null(action);
                if (kind == "release") Assert.Equal(line["where"]!.GetValue<string>() == "here" ? "release" : null, action);
                Assert.DoesNotMatch(RawName, line["label"]!.GetValue<string>());
            }
            foreach (var section in new[] { "outside", "indicators", "not_synced" })
                foreach (var item in panel[section]!["items"]!.AsArray())
                    Assert.DoesNotMatch(RawName, item!["label"]!.GetValue<string>());
            foreach (var row in panel["log"]!.AsArray())
                Assert.DoesNotMatch(RawName, row!["text"]!.GetValue<string>());
        }
    }

    [Fact]
    public void Red_lines_are_ordered_by_priority_then_oldest_first()
    {
        foreach (var (t, panel, _) in Seconds())
        {
            var red = panel["red"]!.AsArray().Select(l => (P: l!["priority"]!.GetValue<int>(), S: DateTimeOffset.Parse(l["since"]!.GetValue<string>()))).ToList();
            Assert.Equal(red.OrderBy(x => x.P).ThenBy(x => x.S).ToList(), red);
        }
    }

    [Fact]
    public void Not_synced_yet_counts_only_controls_moved_in_the_last_two_minutes()
    {
        var everExpired = false;
        foreach (var (t, panel, r) in Seconds())
        {
            var now = PanelReplay.Start.AddMilliseconds(t);
            foreach (var item in panel["not_synced"]!["items"]!.AsArray())
                Assert.True(now - DateTimeOffset.Parse(item!["at"]!.GetValue<string>()) <= PanelLines.NotSyncedWindow);
            // The v3 server never ended these lines: the store keeps more than the panel counts.
            var active = r.Status.Advisories.Count(a => a["kind"]?.GetValue<string>() == "not_synced");
            if (panel["not_synced"]!["count"]!.GetValue<int>() < active) everExpired = true;
        }
        Assert.True(everExpired);
    }

    [Fact]
    public void A_correction_is_in_the_log_and_the_difference_behind_it_stays_the_controls_own_line()
    {
        // From the stream: every correction that still differs once the control is quiet for 2 s is that
        // control's red line with its normal action (ALT, V/S, HDG: turn the knob), never a line of its own.
        var events = PanelReplay.Fixture["events"]!.AsArray().Select(e => e!.AsArray()).ToList();
        var r = new PanelReplay();
        var checkedLines = 0;
        foreach (var e in events.Where(e => e[1]!.GetValue<string>() == "d" && e[3]!.GetValue<string>() is "correction_failed" or "correction_loop"))
        {
            var name = e[2]!.GetValue<string>();
            var at = e[0]!.GetValue<long>() + 3000;
            r.AdvanceTo(at);
            var open = r.Status.DesyncOf(name);
            if (open is null || !r.CanRaiseToday(open, at) || !r.Controls.IsKnown(name) || r.Controls.Spec(name)?.Class == SyncClass.Ignore) continue;
            var panel = r.Panel(at);
            var control = r.Controls.ControlOf(name);
            if (r.Controls.Outside(control))
            {
                // A walkaround item: on the one outside line, never red.
                Assert.Contains(panel["outside"]!["items"]!.AsArray(), i => i!["control"]!.GetValue<string>() == control);
                continue;
            }
            if (panel["holding"]!.AsArray().Any(h => h!.GetValue<string>() == control)) continue;
            var line = panel["red"]!.AsArray().SingleOrDefault(l => l!["control"]!.GetValue<string>() == control);
            Assert.True(line is not null, $"{name}: correction still open at {at} ms, but no line for {control}");
            if (r.Controls.Spec(name)?.Class == SyncClass.Delta) Assert.Equal("turn", line!["kind"]!.GetValue<string>());
            checkedLines++;
        }
        Assert.True(checkedLines >= 5, $"only {checkedLines} corrections checked");
    }

    [Fact]
    public void A_switch_whose_correction_failed_while_the_sims_still_differ_is_one_set_both_line()
    {
        var r = new PanelReplay();
        // ENG 1 BLEED: on in the captain's sim (PF), off here; Crosscheck's correction did not take effect.
        r.Apply(new JsonArray(10_000L, "d", "S_OH_PNEUMATIC_ENG1_BLEED", "correction_failed", "red", 1, 0, 1, 0, "self", "overhead"));
        r.Apply(new JsonArray(10_100L, "d", "I_OH_PNEUMATIC_ENG1_BLEED_L", "observe_mismatch", "quiet", 1, 0, 1, 0, null, "overhead"));
        Assert.Empty(r.Panel(11_000)["red"]!.AsArray()); // inside the 2 s hold

        var panel = r.Panel(12_500);
        var line = Assert.Single(panel["red"]!.AsArray())!;
        Assert.Equal("S_OH_PNEUMATIC_ENG1_BLEED", line["control"]!.GetValue<string>());
        Assert.True(line["kind"]!.GetValue<string>() == "set_both", line.ToJsonString());
        // Yours first (off here), then the other cockpit's.
        Assert.Equal([0.0, 1.0], line["action"]!["options"]!.AsArray().Select(o => o!["value"]!.GetValue<double>()));
        Assert.Equal("1", line["other"]!["value"]!.ToJsonString());
        // Its lamp is part of the same line, not an indicator of its own.
        Assert.Equal(0, panel["indicators"]!["count"]!.GetValue<int>());
        // The correction itself is only in the log, in plain words.
        Assert.Contains(panel["log"]!.AsArray(), row => row!["text"]!.GetValue<string>() == "ENG1 BLEED: Crosscheck could not set it to match.");

        // Values that match again: no line, even while the entry is still open.
        var r2 = new PanelReplay();
        r2.Apply(new JsonArray(10_000L, "d", "S_OH_PNEUMATIC_ENG1_BLEED", "correction_failed", "red", 1, 1, null, null, "self", "overhead"));
        Assert.Empty(r2.Panel(15_000)["red"]!.AsArray());
    }

    [Fact]
    public void Lamps_mid_burst_and_effects_of_another_line_are_never_red()
    {
        // The FD burst at 19:58: the FD lamp checks mid-burst and the FCU managed lamps flickering with
        // them (gate test 2 section 3: 32 + 18 false lines) never reach the panel.
        foreach (var (t, panel, _) in Seconds())
        {
            var clock = PanelReplay.Start.AddMilliseconds(t);
            if (clock < new DateTimeOffset(2026, 11, 2, 19, 57, 30, TimeSpan.Zero) || clock > new DateTimeOffset(2026, 11, 2, 19, 58, 15, TimeSpan.Zero)) continue;
            foreach (var line in panel["red"]!.AsArray())
                Assert.True(line!["control"]!.GetValue<string>() is not ("E_FCU_HEADING" or "E_FCU_SPEED" or "E_FCU_VS" or "E_FCU_ALTITUDE")
                    || line["kind"]!.GetValue<string>() == "turn", $"{line["control"]} at {clock:HH:mm:ss}");
        }
    }

    // ---------------------------------------------------------------- frames for the UI

    private static readonly string FramesPath = Path.Combine(Repo.Root, "ui", "src", "test", "gate-test-2-frames.json");

    [Fact]
    public void Ui_frames_are_up_to_date()
    {
        var text = Frames().ToJsonString(new JsonSerializerOptions { WriteIndented = true }).Replace("\r\n", "\n") + "\n";
        if (Environment.GetEnvironmentVariable("CROSSCHECK_WRITE_FRAMES") == "1") File.WriteAllText(FramesPath, text);
        Assert.True(File.Exists(FramesPath) && File.ReadAllText(FramesPath).Replace("\r\n", "\n") == text,
            "ui/src/test/gate-test-2-frames.json is out of date: run the bridge tests once with CROSSCHECK_WRITE_FRAMES=1");
    }

    /// <summary>
    /// One frame every 5 minutes, plus the moments the screenshots show: the peak, the end, a calm moment and one
    /// with every collapsed line at once. Each frame: the panel (log cut to 12 rows) and the Panels summary.
    /// </summary>
    private static JsonObject Frames()
    {
        var frames = new JsonArray();
        var named = new Dictionary<string, long>();
        var maxRed = -1;
        long peakT = 0, busyT = -1, calmT = -1;
        var all = Seconds().ToList();
        foreach (var (t, panel, _) in all)
        {
            var red = panel["red"]!.AsArray().Count;
            if (red > maxRed) { maxRed = red; peakT = t; }
            var collapsed = panel["not_synced"]!["count"]!.GetValue<int>() > 0 && panel["outside"]!["count"]!.GetValue<int>() > 0
                && panel["indicators"]!["count"]!.GetValue<int>() > 0;
            if (collapsed && red > 0 && busyT < 0 && t > 60_000) busyT = t;
            if (red == 0 && calmT < 0 && t > 30_000) calmT = t;
        }
        named["peak"] = peakT;
        named["end"] = all[^1].T;
        if (busyT >= 0) named["collapsed"] = busyT;
        if (calmT >= 0) named["calm"] = calmT;
        var wanted = all.Where(s => s.T % 300_000 == 0).Select(s => s.T).Concat(named.Values).Distinct().Order().ToHashSet();
        var groups = PanelReplay.Real.Groups.Where(g => PanelReplay.Real.Vars.Any(v => v.Group == g && v.Class is not (SyncClass.Ignore or SyncClass.Stream))).ToList();
        foreach (var (t, panel, r) in all.Where(s => wanted.Contains(s.T)))
        {
            var p = panel.DeepClone().AsObject();
            p["log"] = new JsonArray(panel["log"]!.AsArray().Take(12).Select(x => x!.DeepClone()).ToArray());
            p.Remove("holding");
            var red = panel["red"]!.AsArray().Select(l => l!["group"]?.GetValue<string>()).ToList();
            frames.Add(new JsonObject
            {
                ["t_ms"] = t,
                ["at"] = PanelReplay.Start.AddMilliseconds(t).ToString("O"),
                ["name"] = named.FirstOrDefault(n => n.Value == t).Key,
                ["panel"] = p,
                ["groups"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject
                {
                    ["id"] = g, ["label"] = r.Labels.Group(g), ["differs"] = red.Count(x => x == g),
                }).ToArray()),
            });
        }
        return new JsonObject
        {
            ["note"] = "Generated by PanelReplayTests (bridge) from the gate test 2 panel stream (server/test/fixtures/gate-test-2.json). Do not edit; rerun with CROSSCHECK_WRITE_FRAMES=1.",
            ["frames"] = frames,
        };
    }
}
