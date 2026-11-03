using System.Text.Json.Nodes;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Api;

/// <summary>What the panel builder needs to know about this cockpit (the engine, or a replay in tests).</summary>
public interface IPanelContext
{
    string? ClientId { get; }
    bool IsPf { get; }
    /// <summary>This bridge's seat in the session (host or guest), as advisories name it.</summary>
    string? Seat { get; }
    /// <summary>The other pilot as the pilots say it: "captain" or "first officer".</summary>
    string OtherSide { get; }
    bool TryGetLocal(string var, out double value);
    /// <summary>"Set both" is allowed for this var (a proven absolute switch or analog knob, never the deny list).</summary>
    bool CanSetBoth(string var);
    bool IsAllowedValue(string var, double value);
    bool CanReleaseHere(string var, JsonObject desync);
}

/// <summary>The open state the panel is built from, at one moment.</summary>
public sealed record PanelInput(
    IReadOnlyList<(JsonObject Body, DateTimeOffset Since)> Desyncs,
    IReadOnlyDictionary<string, DateTimeOffset> Activity,
    IReadOnlyList<JsonObject> Advisories,
    IReadOnlyList<BridgeEvent> Events,
    DateTimeOffset Now);

/// <summary>
/// The session panel (CRO-34): one line per control. Red only when a pilot can act and it has lasted
/// (open 2 s, and 2 s after the last movement or press burst of that control), each with one action.
/// Everything else is collapsed: "not synced yet" (moved in the last 2 minutes), one "outside" line for
/// walkaround items, one amber line per hardware fight, one grey line for indicators, and the log.
/// Data only: the UI writes the sentences (ui/src/words.ts). The log is text, with plain labels.
/// </summary>
public static class PanelLines
{
    public static readonly TimeSpan Hold = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan NotSyncedWindow = TimeSpan.FromMinutes(2);

    /// <summary>Safety lines (protocol.md section 5): always red after the hold, priority 1.</summary>
    private static readonly HashSet<string> Safety = ["not_mirrored", "stuck_pressed", "safety_release"];
    /// <summary>Crosscheck's own corrections: the entry goes to the log; the difference behind it stays the control's line.</summary>
    private static readonly HashSet<string> Corrections = ["correction_failed", "correction_loop"];

    /// <summary>Which desync of a control decides its line (first wins).</summary>
    private static readonly string[] Precedence =
    [
        "stuck_pressed", "not_mirrored", "safety_release", "press_lost", "toggle_mismatch", "effect_mismatch", "press_mismatch",
        "mode_mismatch", "display_mismatch", "refused", "no_ack", "log_conflict", "missing_value", "correction_failed",
        "correction_loop", "no_pf",
    ];

    /// <summary>Events that belong in the log.</summary>
    public static readonly HashSet<string> LogTypes =
    [
        "action_confirmed", "action_failed", "action_applied", "corrected", "overridden", "desync", "desync_cleared",
        "control_changed", "takeover_pressed", "peer_joined", "peer_lost", "peer_resumed", "peer_left", "peer_expired",
        "press_here_only", "release_it", "denied_input", "counter_suspect", "press_lost",
    ];

    private sealed record Open(JsonObject Body, DateTimeOffset Since, string Var, string Reason, string Tier);

    public static JsonObject Build(Profile profile, ControlMap controls, Labels labels, IPanelContext ctx, PanelInput input)
    {
        var red = new List<(JsonObject Line, int Priority, DateTimeOffset Since)>();
        var indicators = new List<(JsonObject Item, DateTimeOffset Since)>();
        var outside = new List<JsonObject>();
        var holding = new JsonArray();

        var byControl = input.Desyncs
            .Select(d => new Open(d.Body, d.Since, d.Body["var"]!.GetValue<string>(), d.Body["reason"]?.GetValue<string>() ?? "",
                TierOf(controls, d.Body)))
            // A var the profile ignores (each pilot's own window) is never on the panel.
            .Where(o => controls.Spec(o.Var)?.Class != SyncClass.Ignore)
            .GroupBy(o => controls.ControlOf(o.Var), StringComparer.Ordinal)
            .ToList();
        // Controls with an input difference right now: their downstream controls are effects, not lines.
        var differing = byControl.Where(g => g.Any(o => o.Tier == "red" && controls.IsKnown(o.Var))).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var g in byControl)
        {
            var control = g.Key;
            var open = g.ToList();
            var label = controls.Label(control);
            if (controls.Outside(control))
            {
                var o = open.OrderBy(x => x.Since).First();
                var (mine, other) = Values(controls, ctx, o);
                outside.Add(new JsonObject
                {
                    ["control"] = control, ["label"] = label,
                    ["you"] = Value(labels, o, mine), ["other"] = Value(labels, o, other), ["since"] = o.Since.ToString("O"),
                });
                continue;
            }

            // Red only for a control this profile syncs; a var it does not know (not synced yet) is never red.
            var inputs = open.Where(o => o.Tier == "red" && !IsSettledCorrection(o) && controls.IsKnown(o.Var)).ToList();
            if (inputs.Count == 0)
            {
                // Only indicators (or a correction whose values match again): one grey item per control.
                var quiet = open.Where(o => o.Tier == "quiet" || !controls.IsKnown(o.Var)).OrderBy(o => o.Since).FirstOrDefault();
                if (quiet is null) continue;
                var (mine, other) = Values(controls, ctx, quiet);
                indicators.Add((new JsonObject
                {
                    ["control"] = control, ["label"] = label,
                    ["you"] = Value(labels, quiet, mine), ["other"] = Value(labels, quiet, other), ["since"] = quiet.Since.ToString("O"),
                }, quiet.Since));
                continue;
            }

            // An effect of another line (both FDs off un-dash the FCU windows): folded into that line.
            var upstream = controls.Members(control).Append(control).SelectMany(m => controls.Spec(m)?.Upstream ?? [])
                .Select(controls.ControlOf).FirstOrDefault(u => u != control && differing.Contains(u));
            if (upstream is not null)
            {
                holding.Add(control);
                continue;
            }

            var since = inputs.Min(o => o.Since);
            // The last movement of this control, or of a control upstream of it (its effect may still settle).
            var ups = controls.Members(control).Append(control).SelectMany(m => controls.Spec(m)?.Upstream ?? [])
                .SelectMany(u => controls.Members(controls.ControlOf(u)));
            var lastMove = controls.Members(control).Append(control).Concat(ups)
                .Select(m => input.Activity.TryGetValue(m, out var at) ? at : DateTimeOffset.MinValue).Max();
            if (input.Now - since < Hold || input.Now - lastMove < Hold)
            {
                // Still moving, or a press burst still settling: no line yet (never red mid-burst).
                holding.Add(control);
                continue;
            }

            var main = inputs.OrderBy(o => Rank(o.Reason)).ThenBy(o => o.Since).First();
            var line = Line(profile, controls, labels, ctx, control, label, main, since);
            red.Add((line, line["priority"]!.GetValue<int>(), since));
        }

        var orderedRed = red.OrderBy(r => r.Priority).ThenBy(r => r.Since).ThenBy(r => r.Line["control"]!.GetValue<string>(), StringComparer.Ordinal)
            .Select(r => (JsonNode)r.Line).ToArray();
        return new JsonObject
        {
            ["red"] = new JsonArray(orderedRed),
            ["holding"] = holding,
            ["not_synced"] = NotSynced(controls, ctx, input),
            ["outside"] = new JsonObject { ["count"] = outside.Count, ["items"] = new JsonArray(outside.Select(o => (JsonNode)o).ToArray()) },
            ["amber"] = Amber(controls, ctx, input),
            ["indicators"] = new JsonObject
            {
                ["count"] = indicators.Count,
                ["items"] = new JsonArray(indicators.OrderBy(i => i.Since).Select(i => (JsonNode)i.Item).ToArray()),
            },
            ["log"] = Log(controls, input.Events),
        };
    }

    private static int Rank(string reason)
    {
        var i = Array.IndexOf(Precedence, reason);
        return i < 0 ? Precedence.Length : i;
    }

    /// <summary>A correction entry whose values match again (expected = observed): nothing left to fix.</summary>
    private static bool IsSettledCorrection(Open o) =>
        Corrections.Contains(o.Reason) && o.Body["values"] is not JsonObject && Num(o.Body["expected"]) is { } e && Num(o.Body["observed"]) is { } x
        && Math.Abs(e - x) < 1e-9;

    /// <summary>
    /// The tier the bridge trusts (defence in depth, as before): quiet only for an indicator that is not an
    /// encoder display, with a reason that is not a failed input. Everything else is red.
    /// </summary>
    private static string TierOf(ControlMap controls, JsonObject body)
    {
        var name = body["var"]!.GetValue<string>();
        var spec = controls.Spec(name);
        var cls = spec?.Class ?? (name.StartsWith("aircraft.", StringComparison.Ordinal) ? SyncClass.Observe : null);
        var deltaDisplay = controls.ControlOf(name) is var c && c != name && controls.Spec(c)?.Display == name;
        return UiStatus.TierOf(cls, deltaDisplay, body["reason"]?.GetValue<string>(), body["tier"]?.GetValue<string>());
    }

    private static JsonObject Line(Profile profile, ControlMap controls, Labels labels, IPanelContext ctx,
        string control, string label, Open main, DateTimeOffset since)
    {
        var name = main.Var;
        var spec = controls.Spec(name);
        var (mine, other) = Values(controls, ctx, main);
        var clientIsMe = main.Body["client_id"]?.GetValue<string>() is { } c && c == ctx.ClientId;
        string? where = null;
        JsonObject? action = null;
        var showValues = false;
        string kind;
        // Deny list (IDG, fire, AGENT/DISCH, RAT, passenger oxygen): always the "not mirrored" line, whatever
        // the reason, never advice to set it (gate test 2: IDG lines invited "Set both").
        var denied = spec?.NeverWrite == true || controls.Spec(control)?.NeverWrite == true;
        var safety = denied || Safety.Contains(main.Reason);

        if (denied)
        {
            kind = "not_mirrored";
            // not_mirrored names the cockpit that pressed it; any other reason names the cockpit it did not reach.
            where = (main.Reason == "not_mirrored" ? clientIsMe : !clientIsMe) ? "here" : "there";
        }
        else switch (main.Reason)
        {
            case "stuck_pressed":
                kind = "release";
                where = clientIsMe ? "here" : "there";
                if (ctx.CanReleaseHere(name, main.Body)) action = new JsonObject { ["type"] = "release", ["var"] = name };
                break;
            case "not_mirrored":
                kind = "not_mirrored";
                where = clientIsMe ? "here" : "there";
                break;
            case "safety_release":
                kind = "released";
                where = clientIsMe ? "here" : "there";
                break;
            case "press_lost":
                kind = "press_lost";
                where = clientIsMe ? "here" : "there";
                break;
            case "press_mismatch":
                kind = "held";
                where = mine is { } p && ((long)Math.Round(p) & 1) == 1 ? "here" : "there";
                break;
            case "toggle_mismatch" or "effect_mismatch":
                kind = "lamp";
                where = mine > 0 ? "here" : "there";
                if (CanPressHere(profile, controls, name))
                {
                    var press = PressVar(controls, control, name);
                    action = new JsonObject { ["type"] = "press_here", ["var"] = press, ["label"] = labels.Var(press) };
                }
                break;
            case "mode_mismatch":
                kind = "mode";
                break;
            default:
                if (spec?.Class == SyncClass.Delta || (spec is null && main.Reason == "display_mismatch") || controls.Spec(control)?.Class == SyncClass.Delta
                    || control.StartsWith("rmp", StringComparison.Ordinal))
                {
                    kind = "turn";
                    showValues = mine is not null || other is not null;
                    break;
                }
                showValues = mine is not null || other is not null;
                var options = new JsonArray();
                if (ctx.CanSetBoth(name) && mine is { } a && other is { } b
                    && ctx.IsAllowedValue(name, a) && ctx.IsAllowedValue(name, b))
                    foreach (var value in new[] { a, b }.Distinct())
                        options.Add(new JsonObject { ["value"] = value, ["text"] = labels.Value(name, value) });
                if (options.Count > 0)
                {
                    kind = "set_both";
                    action = new JsonObject { ["type"] = "set_both", ["var"] = name, ["options"] = options };
                }
                else kind = "by_hand";
                break;
        }

        var group = controls.Group(control);
        return new JsonObject
        {
            ["control"] = control,
            ["label"] = label,
            ["group"] = group,
            ["group_label"] = group is null ? null : labels.Group(group),
            ["since"] = since.ToString("O"),
            ["priority"] = safety ? 1 : controls.Priority(control),
            ["kind"] = kind,
            ["where"] = where,
            ["you"] = showValues ? Value(labels, main, mine) : null,
            ["other"] = showValues ? Value(labels, main, other) : null,
            ["action"] = action,
            ["message"] = main.Reason == "mode_mismatch" ? main.Body["message"]?.GetValue<string>() : null,
            ["reason"] = main.Reason,
        };
    }

    /// <summary>The button to press for a lamp line (an ND filter line presses the button whose lamp differs).</summary>
    private static string PressVar(ControlMap controls, string control, string name) =>
        controls.Spec(name) is { Class: SyncClass.Pulse } ? name
        : controls.Members(control).FirstOrDefault(m => controls.Spec(m) is { Class: SyncClass.Pulse } s && s.Verify?.Lvar == name) ?? name;

    private static bool CanPressHere(Profile profile, ControlMap controls, string name)
    {
        var button = controls.Spec(name) is { Class: SyncClass.Pulse } ? controls.Spec(name)
            : profile.Vars.FirstOrDefault(v => v.Class == SyncClass.Pulse && v.Verify?.Lvar == name);
        if (button is not { Press: PressMode.Edge, Verify.Lvar: not null, NeverWrite: false }) return false;
        // The takeover pushbutton is a control request, never pressed by Crosscheck.
        return profile.Roles?.Takeover is not { } t || (t.Captain != button.Name && t.Fo != button.Name);
    }

    /// <summary>This cockpit's value and the other's for a desync, as the old panel showed them.</summary>
    private static (double? Mine, double? Other) Values(ControlMap controls, IPanelContext ctx, Open o)
    {
        var body = o.Body;
        var compare = o.Reason is "effect_mismatch" or "toggle_mismatch" or "press_mismatch" or "mode_mismatch" or "stuck_pressed"
            or "display_mismatch" or "not_mirrored" or "press_lost" or "observe_mismatch" or "correction_loop"
            || controls.Spec(o.Var)?.Class == SyncClass.Delta;
        double? mine = !compare && ctx.TryGetLocal(o.Var, out var m) ? m : null;
        var receiverIsYou = body["client_id"]?.GetValue<string>() is { } c && c == ctx.ClientId;
        double? other = body["values"] is JsonObject values
            ? Num(values[ctx.IsPf ? "pm" : "pf"])
            : receiverIsYou ? Num(body["expected"]) : Num(body["observed"]);
        if (body["values"] is JsonObject v2 && mine is null) mine = Num(v2[ctx.IsPf ? "pf" : "pm"]);
        if (mine is null && body["values"] is not JsonObject)
            mine = receiverIsYou ? Num(body["observed"]) : Num(body["expected"]);
        return (mine, other);
    }

    private static JsonObject Value(Labels labels, Open o, double? v)
    {
        string text = v is not { } x ? "unknown" : o.Reason switch
        {
            "toggle_mismatch" or "mode_mismatch" or "effect_mismatch" => x > 0 ? "on" : "off",
            "press_mismatch" or "stuck_pressed" => ((long)Math.Round(x) & 1) == 1 ? "held down" : "up",
            "display_mismatch" or "not_mirrored" or "observe_mismatch" or "correction_loop" => Labels.Number(x),
            _ => labels.Value(o.Var, x),
        };
        return new JsonObject { ["value"] = v, ["text"] = text };
    }

    /// <summary>Controls not synced yet that a pilot moved in the last 2 minutes, one per control.</summary>
    private static JsonObject NotSynced(ControlMap controls, IPanelContext ctx, PanelInput input)
    {
        var items = input.Advisories
            .Where(a => a["kind"]?.GetValue<string>() == "not_synced")
            .Select(a => (Body: a, At: DateTimeOffset.FromUnixTimeMilliseconds(a["at"]?.GetValue<long>() ?? 0)))
            .Where(a => input.Now - a.At <= NotSyncedWindow)
            .GroupBy(a => controls.ControlOf(a.Body["var"]?.GetValue<string>() ?? a.Body["key"]!.GetValue<string>()), StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.OrderByDescending(a => a.At).First();
                var name = latest.Body["var"]?.GetValue<string>();
                var label = controls.Spec(g.Key) is not null || g.Key != name ? controls.Label(g.Key)
                    : latest.Body["label"]?.GetValue<string>() ?? controls.Label(g.Key);
                var mine = latest.Body["seat"]?.GetValue<string>() == ctx.Seat;
                return (At: latest.At, Item: new JsonObject
                {
                    ["control"] = g.Key, ["label"] = label, ["mine"] = mine, ["who"] = mine ? "you" : ctx.OtherSide,
                    ["at"] = latest.At.ToString("O"),
                });
            })
            .OrderByDescending(i => i.At).Select(i => (JsonNode)i.Item).ToArray();
        return new JsonObject { ["count"] = items.Length, ["items"] = new JsonArray(items) };
    }

    /// <summary>One amber line per hardware fight, counter suspect or sim that is not connected. Newest first.</summary>
    private static JsonArray Amber(ControlMap controls, IPanelContext ctx, PanelInput input) =>
        new(input.Advisories
            .Where(a => a["kind"]?.GetValue<string>() is { } k && k != "not_synced")
            .OrderByDescending(a => a["at"]?.GetValue<long>() ?? 0)
            .Select(a =>
            {
                var name = a["var"]?.GetValue<string>();
                var mine = a["seat"]?.GetValue<string>() == ctx.Seat;
                return (JsonNode)new JsonObject
                {
                    ["key"] = a["key"]!.GetValue<string>(),
                    ["kind"] = a["kind"]!.GetValue<string>(),
                    ["label"] = name is null ? a["label"]?.GetValue<string>() : controls.Label(controls.ControlOf(name)),
                    ["mine"] = mine,
                    ["who"] = mine ? "you" : ctx.OtherSide,
                    ["at"] = DateTimeOffset.FromUnixTimeMilliseconds(a["at"]?.GetValue<long>() ?? 0).ToString("O"),
                };
            }).ToArray());

    /// <summary>The log: newest first, 50 rows, plain labels (never a raw var name).</summary>
    public static JsonArray Log(ControlMap controls, IReadOnlyList<BridgeEvent> events) =>
        new(events.Where(e => LogTypes.Contains(e.Type)).Reverse().Take(50)
            .Select(e => (JsonNode)new JsonObject { ["type"] = e.Type, ["text"] = LogText(controls, e), ["at"] = e.At.ToString("O") }).ToArray());

    public static string LogText(ControlMap controls, BridgeEvent e)
    {
        var name = e.Data?["var"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        if (name is null) return e.Message;
        var label = controls.Label(controls.ControlOf(name));
        var reason = e.Data?["reason"]?.GetValue<string>();
        return e.Type switch
        {
            "desync" => $"{label}: {ReasonText(reason)}.",
            "desync_cleared" => reason is "correction_failed" or "correction_loop"
                ? $"{label} matches again." : $"{label} matches again in both cockpits.",
            "corrected" => $"{label} set to match the pilot flying.",
            "overridden" => $"{label}: the newer change won.",
            _ => e.Message,
        };
    }

    private static string ReasonText(string? reason) => reason switch
    {
        "refused" => "the change did not arrive in the other cockpit",
        "no_ack" => "the other sim did not confirm the change",
        "correction_failed" => "Crosscheck could not set it to match",
        "correction_loop" => "Crosscheck stopped setting it (it kept changing back)",
        "effect_mismatch" or "toggle_mismatch" => "its light differs between the cockpits",
        "press_mismatch" => "held down in one cockpit only",
        "stuck_pressed" => "stuck pressed in one cockpit",
        "press_lost" => "a press did not arrive",
        "not_mirrored" => "pressed in one cockpit, not mirrored (irreversible)",
        "safety_release" => "released by Crosscheck, the release never arrived",
        "mode_mismatch" => "managed in one cockpit only",
        "display_mismatch" => "the display differs between the cockpits",
        "observe_mismatch" => "the indicator differs between the cockpits",
        "missing_value" => "one sim does not report it",
        "log_conflict" => "neither cockpit shows the last pilot command",
        "no_pf" => "differs, and there is no pilot flying",
        _ => "differs between the cockpits",
    };

    private static double? Num(JsonNode? n) => n is not JsonValue v ? null
        : v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i : null;
}
