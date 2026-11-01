using System.Text.Json.Nodes;
using Crosscheck.Bridge.Protocol;
using Crosscheck.Bridge.Sim;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Api;

/// <summary>
/// Builds what GET /api/status returns: the existing status fields plus everything the UI
/// screens show (phase, this computer, preflight table, crew, panels, desyncs with values).
/// Data only; the UI writes the sentences.
/// </summary>
internal static class UiStatus
{
    /// <summary>Events that belong in the "Recent" list.</summary>
    public static readonly HashSet<string> RecentTypes =
    [
        "action_confirmed", "action_failed", "action_applied", "corrected", "overridden", "desync", "desync_cleared",
        "control_changed", "takeover_pressed", "peer_joined", "peer_lost", "peer_resumed", "peer_left", "peer_expired",
    ];

    public static JsonArray Recent(EventHub hub) =>
        new(hub.Recent.Where(e => RecentTypes.Contains(e.Type)).Reverse().Take(50)
            .Select(e => (JsonNode)new JsonObject { ["type"] = e.Type, ["message"] = e.Message, ["at"] = e.At.ToString("O") }).ToArray());

    public static JsonObject Build(BridgeRuntime r)
    {
        var json = r.Status.ToJson(r.StatusCounters);
        var engine = r.Engine;
        var client = r.Client;
        var simConnected = r.Sim?.State == SimState.Connected;

        json["phase"] = engine is null ? (r.Preflight is not null ? "preflight" : "idle")
            : client?.State == ServerState.Ended ? "ended"
            : r.Hosting && r.Status.PeerState is null ? "waiting"
            : "session";
        json["bridge_version"] = BridgeRuntime.BridgeVersion;
        json["last_error"] = r.LastError;
        json["this_computer"] = new JsonObject
        {
            ["sim"] = new JsonObject { ["connected"] = simConnected, ["name"] = "MSFS 2024" },
            ["aircraft"] = new JsonObject
            {
                ["name"] = r.Profile.Aircraft.Name,
                ["app_version"] = r.Versions?.AppVersion,
                ["package_version"] = r.Versions?.PackageVersion,
                ["reading"] = r.Versions is null,
            },
            ["profile"] = new JsonObject
            {
                ["id"] = r.Profile.Aircraft.Id,
                ["builds"] = new JsonArray(r.Profile.Aircraft.Builds.Select(b => (JsonNode)b).ToArray()),
                ["hash"] = r.ProfileHash,
            },
            ["server"] = r.ServerUrl.ToString(),
        };
        json["start_blocked"] = simConnected ? null : "sim_not_connected";
        json["preflight"] = r.Preflight is { } p ? PreflightTable(r, p) : null;

        if (engine is null || client is null) return json;

        json["is_pf"] = engine.IsPf;
        json["crew"] = Crew(r, engine);
        json["peer_lost_at"] = engine.PeerLostAt?.ToString("O");
        json["session_ttl_ms"] = client.SessionTtlMs;
        json["last_compared_at"] = engine.LastComparedAt?.ToString("O");
        json["groups"] = Groups(r, engine);
        json["owned"] = Owned(r, engine);
        json["desyncs"] = Desyncs(r, engine, client);
        json["control_notice"] = engine.IsPf && engine.ControlNotice is { } n ? ControlNotice(r, n, engine.ControlNoticeAt) : null;
        return json;
    }

    private static JsonObject PreflightTable(BridgeRuntime r, PreflightCheck p)
    {
        var diffs = p.Rejects.Concat(p.Warnings).OfType<JsonObject>().ToList();
        string Severity(params string[] fields)
        {
            var hit = diffs.Where(d => fields.Contains(d["field"]!.GetValue<string>())).ToList();
            return hit.Any(d => d["severity"]!.GetValue<string>() == "reject") ? "reject" : hit.Count > 0 ? "warn" : "same";
        }
        static string? S(JsonNode? n) => n?.GetValue<string>();
        static string Short(string? hash) => hash is null ? "unknown" : hash.Replace("sha256:", "")[..Math.Min(6, hash.Replace("sha256:", "").Length)];
        var aircraft = r.Profile.Aircraft.Name;
        var rows = new JsonArray
        {
            Row("bridge_version", "Crosscheck bridge", S(p.Mine["bridge_version"]), S(p.Reference["bridge_version"]), Severity("bridge_version")),
            Row("profile", "Profile", $"{S(p.Mine["profile_id"])} · {Short(S(p.Mine["profile_hash"]))}",
                $"{S(p.Reference["profile_id"])} · {Short(S(p.Reference["profile_hash"]))}", Severity("profile_id", "profile_hash")),
            Row("aircraft.package_version", $"{aircraft} package", S(p.Mine["aircraft"]?["package_version"]), S(p.Reference["aircraft"]?["package_version"]), Severity("aircraft.package_version")),
            Row("aircraft.app_version", $"{aircraft} app", S(p.Mine["aircraft"]?["app_version"]), S(p.Reference["aircraft"]?["app_version"]), Severity("aircraft.app_version")),
            Row("airac", "AIRAC", S(p.Mine["airac"]) ?? "unknown", S(p.Reference["airac"]) ?? "unknown", Severity("airac")),
        };
        return new JsonObject { ["code"] = p.Code, ["joinable"] = p.Joinable, ["rows"] = rows };

        static JsonObject Row(string field, string item, string? you, string? other, string severity) => new()
        {
            ["field"] = field, ["item"] = item, ["you"] = you ?? "unknown", ["other"] = other ?? "unknown", ["result"] = severity,
        };
    }

    private static JsonArray Crew(BridgeRuntime r, Sync.SyncEngine engine)
    {
        var mySide = r.Status.SeatSide ?? "captain";
        var crew = new JsonArray
        {
            new JsonObject { ["side"] = mySide, ["you"] = true, ["role"] = engine.IsPf ? "pf" : "pm", ["state"] = "connected" },
        };
        if (r.Status.PeerState is { } state and not ("left" or "expired"))
        {
            crew.Add(new JsonObject
            {
                ["side"] = mySide == "fo" ? "captain" : "fo", ["you"] = false, ["role"] = engine.IsPf ? "pm" : "pf",
                ["state"] = state == "joined" || state == "resumed" ? "connected" : state, ["name"] = engine.PeerName,
            });
        }
        return crew;
    }

    private static JsonArray Groups(BridgeRuntime r, Sync.SyncEngine engine)
    {
        var desyncs = r.Status.Desyncs;
        var withControls = r.Profile.Vars.Where(v => v.Class is not (SyncClass.Ignore or SyncClass.Stream)).Select(v => v.Group).ToHashSet();
        return new JsonArray(engine.ActiveGroups.Where(withControls.Contains).Select(g => (JsonNode)new JsonObject
        {
            ["id"] = g,
            ["label"] = r.Labels.Group(g),
            ["differs"] = desyncs.Count(d => d.Body["group"]?.GetValue<string>() == g),
        }).ToArray());
    }

    private static JsonArray Owned(BridgeRuntime r, Sync.SyncEngine engine)
    {
        var drift = engine.Drift;
        var warnings = engine.DriftWarnings.Select(w => w.Var).ToHashSet();
        return new JsonArray((r.Profile.Datarefs ?? []).Where(d => d.Class == SyncClass.Owned).Select(d => (JsonNode)new JsonObject
        {
            ["var"] = d.Path,
            ["label"] = r.Labels.Var(d.Path),
            ["unit"] = d.Unit,
            ["delta"] = drift.TryGetValue(d.Path, out var x) ? x.Delta : 0,
            ["warn"] = warnings.Contains(d.Path),
        }).ToArray());
    }

    private static JsonArray Desyncs(BridgeRuntime r, Sync.SyncEngine engine, ProtocolClient client)
    {
        var list = new JsonArray();
        foreach (var (body, since) in r.Status.Desyncs.OrderBy(d => d.Since))
        {
            var name = body["var"]!.GetValue<string>();
            var group = body["group"]?.GetValue<string>();
            var spec = r.Profile.Vars.FirstOrDefault(v => v.Name == name);
            double? mine = r.Sim.TryGet(name, out var m) ? m : null;
            var receiverIsYou = body["client_id"]?.GetValue<string>() is { } c && c == client.ClientId;
            double? other = body["values"] is JsonObject values
                ? Num(values[engine.IsPf ? "pm" : "pf"])
                : receiverIsYou ? Num(body["expected"]) : Num(body["observed"]);
            if (body["values"] is JsonObject v2 && mine is null) mine = Num(v2[engine.IsPf ? "pf" : "pm"]);
            var writable = spec?.Class is SyncClass.Verified or SyncClass.Analog;
            var options = new JsonArray();
            if (writable)
                foreach (var value in new[] { mine, other }.OfType<double>().Distinct().Where(x => r.Sim.Guard.IsAllowedValue(name, x)))
                    options.Add(new JsonObject { ["value"] = value, ["text"] = r.Labels.Value(name, value) });
            list.Add(new JsonObject
            {
                ["var"] = name,
                ["label"] = r.Labels.Var(name),
                ["has_label"] = r.Labels.HasLabel(name),
                ["group"] = group,
                ["group_label"] = group is null ? null : r.Labels.Group(group),
                ["reason"] = body["reason"]!.GetValue<string>(),
                ["since"] = since.ToString("O"),
                ["receiver_is_you"] = receiverIsYou,
                ["you"] = new JsonObject { ["value"] = mine, ["text"] = r.Labels.Value(name, mine) },
                ["other"] = new JsonObject { ["value"] = other, ["text"] = r.Labels.Value(name, other) },
                ["writable"] = writable,
                ["options"] = options,
                ["message"] = body["message"]?.GetValue<string>(),
            });
        }
        return list;
    }

    private static JsonObject ControlNotice(BridgeRuntime r, JsonObject notice, DateTimeOffset? at)
    {
        var groups = (notice["differences"]?["groups"] as JsonArray ?? []).Select(g => g!.GetValue<string>()).ToList();
        var vars = (notice["differences"]?["vars"] as JsonArray ?? []).Select(v => v!.GetValue<string>()).ToList();
        return new JsonObject
        {
            ["at"] = at?.ToString("O"),
            ["kind"] = notice["kind"]?.GetValue<string>(),
            ["groups"] = new JsonArray(groups.Select(g => (JsonNode)r.Labels.Group(g)).ToArray()),
            ["vars"] = new JsonArray(vars.Select(v => (JsonNode)r.Labels.Var(v)).ToArray()),
        };
    }

    private static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
