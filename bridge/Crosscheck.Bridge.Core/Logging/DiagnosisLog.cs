using System.Text.Json.Nodes;
using Crosscheck.Bridge.Follow;

namespace Crosscheck.Bridge.Logging;

/// <summary>
/// 0.6.1 diagnosis log: every 5 s one "diag" line in the bridge log, on both sides, so the next flight shows what the
/// follower's engines, fuel, weight, brakes and autoflight lamps do next to the PF's. Read-only: it reads values the
/// bridge can already see, writes nothing to the sim, never goes to the server or the panel (only the log file).
///
/// Only values that are known to exist are read (profile, scan, GRAPHQL.md). Not known, so not logged: EPR, a live
/// gross weight, per-wheel brake temperatures, FMA mode text, FD pitch/roll bars (<see cref="Missing"/>, listed once in
/// the "diag_start" line). "_raw" fields have an unverified scale or unit. Null values are left out of the line.
/// </summary>
public sealed class DiagnosisLog
{
    /// <summary>L: vars read in addition to the profile's (scan: "A320_Sound:*"; scale and unit unverified). Read only, never compared or synced.</summary>
    public static readonly IReadOnlyList<string> ExtraLvars =
        ["A320_Sound:ENG_1_N1", "A320_Sound:ENG_2_N1", "A320_Sound:Highest_brake_temp"];

    /// <summary>GraphQL paths read once per tick (GRAPHQL.md): field in the line, path.</summary>
    public static readonly IReadOnlyList<(string Field, string Path)> GraphQlFields =
    [
        ("fuel_left_kg", "aircraft.fuel.left.amount.kg"),
        ("fuel_right_kg", "aircraft.fuel.right.amount.kg"),
        ("fuel_center_kg", "aircraft.fuel.center.amount.kg"),
        ("fuel_act1_kg", "aircraft.fuel.ACT1.amount.kg"),
        ("fuel_act2_kg", "aircraft.fuel.ACT2.amount.kg"),
        ("fuel_total_kg", "aircraft.fuel.total.amount.kg"),
        ("zfw_kg", "aircraft.weight.zfw"),
    ];

    /// <summary>FCU lamps and flags already in the profile (autopilot, A/THR, managed and dashed state, FD buttons).</summary>
    public static readonly IReadOnlyList<string> FcuVars =
    [
        "I_FCU_AP1", "I_FCU_AP2", "I_FCU_ATHR", "I_FCU_LOC", "I_FCU_APPR", "I_FCU_EXPED",
        "I_FCU_HEADING_MANAGED", "I_FCU_SPEED_MANAGED", "I_FCU_ALTITUDE_MANAGED",
        "I_FCU_HEADING_VS_MODE", "I_FCU_TRACK_FPA_MODE",
        "B_FCU_SPEED_DASHED", "B_FCU_HEADING_DASHED", "B_FCU_VERTICALSPEED_DASHED",
        "I_FCU_EFIS1_FD", "I_FCU_EFIS2_FD",
    ];

    /// <summary>What the owner asked for that no known var provides (needs a read-only probe first).</summary>
    public static readonly IReadOnlyList<string> Missing =
        ["epr_1_2", "gross_weight_live", "brake_temp_per_wheel", "fma_mode_text", "fd_pitch_roll_bars", "n1_scaled"];

    private readonly Func<string, double?> _lvar;
    private readonly Func<string, CancellationToken, Task<object?>> _gql;
    private readonly Func<PhysicsReading> _physics;
    private readonly Func<string?> _role;
    private readonly Func<string> _mode;
    private readonly Action<string, JsonObject> _emit;
    private readonly TimeSpan _every;
    private readonly TimeSpan _gqlTimeout;

    /// <param name="lvar">Latest value of a watched L: var, or null.</param>
    /// <param name="gql">A fresh GraphQL read of one path (the value, or null).</param>
    /// <param name="physics">The own aircraft and the last placed PF frame.</param>
    /// <param name="role">"pf", "pm" or null (no session).</param>
    /// <param name="mode">The follow mode word.</param>
    /// <param name="emit">Writes one event to the log file (name, data).</param>
    public DiagnosisLog(Func<string, double?> lvar, Func<string, CancellationToken, Task<object?>> gql, Func<PhysicsReading> physics,
        Func<string?> role, Func<string> mode, Action<string, JsonObject> emit, double everyS = 5, double gqlTimeoutS = 1.5)
    {
        _lvar = lvar;
        _gql = gql;
        _physics = physics;
        _role = role;
        _mode = mode;
        _emit = emit;
        _every = TimeSpan.FromSeconds(everyS);
        _gqlTimeout = TimeSpan.FromSeconds(gqlTimeoutS);
    }

    /// <summary>The one-time "diag_start" line: where each field comes from and what is not logged.</summary>
    public static JsonObject StartEvent() => new()
    {
        ["every_s"] = 5,
        ["lvars"] = new JsonArray(ExtraLvars.Select(v => (JsonNode)v).ToArray()),
        ["graphql"] = new JsonArray(GraphQlFields.Select(f => (JsonNode)$"{f.Field}={f.Path}").ToArray()),
        ["fcu"] = new JsonArray(FcuVars.Select(v => (JsonNode)v).ToArray()),
        ["missing"] = new JsonArray(Missing.Select(v => (JsonNode)v).ToArray()),
        ["note"] = "gw_est_kg = zfw_kg + fuel_total_kg (an estimate: no live gross weight var is known); *_raw: scale or unit unverified",
    };

    private static void Put(JsonObject o, string key, double? value)
    {
        if (value is { } d && double.IsFinite(d)) o[key] = Math.Round(d, 3);
    }

    /// <summary>One "diag" line's content. Also called by tests.</summary>
    public async Task<JsonObject> SnapshotAsync(CancellationToken ct)
    {
        var o = new JsonObject();
        if (_role() is { } role) o["role"] = role;
        o["mode"] = _mode();
        var ph = _physics();
        Put(o, "own_gs_kt", ph.OwnGsKt);
        if (ph.OwnOnGround is { } og) o["own_on_ground"] = og;
        Put(o, "pf_gs_kt", ph.PfGsKt);
        if (ph.PfOnGround is { } pg) o["pf_on_ground"] = pg;
        if (ph.SurfacesOn is { } so) o["surfaces_on"] = so;

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (field, path) in GraphQlFields)
        {
            if (await ReadGqlAsync(path, ct) is { } v)
            {
                values[field] = v;
                Put(o, field, v);
            }
        }
        if (values.TryGetValue("zfw_kg", out var zfw) && values.TryGetValue("fuel_total_kg", out var fuel)) Put(o, "gw_est_kg", zfw + fuel);

        Put(o, "n1_1_raw", _lvar("A320_Sound:ENG_1_N1"));
        Put(o, "n1_2_raw", _lvar("A320_Sound:ENG_2_N1"));
        Put(o, "brake_temp_max_raw", _lvar("A320_Sound:Highest_brake_temp"));

        var fcu = new JsonObject();
        foreach (var name in FcuVars)
            if (_lvar(name) is { } v) fcu[name[6..]] = v; // "I_FCU_" / "B_FCU_" prefix off
        if (fcu.Count > 0) o["fcu"] = fcu;
        return o;
    }

    private async Task<double?> ReadGqlAsync(string path, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_gqlTimeout);
        try
        {
            return await _gql(path, cts.Token) is double d ? d : null;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && e is OperationCanceledException or HttpRequestException or InvalidOperationException or TimeoutException)
        {
            return null; // Fenix not there (or slow): this value is left out, the line still goes
        }
    }

    /// <summary>Logs "diag_start" and then one "diag" every few seconds until <paramref name="ct"/> is cancelled. Never throws.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            _emit("diag_start", StartEvent());
            if (_every <= TimeSpan.Zero) return;
            using var timer = new PeriodicTimer(_every);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    _emit("diag", await SnapshotAsync(ct));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _emit("diag_failed", new JsonObject { ["why"] = e.Message });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }
}
