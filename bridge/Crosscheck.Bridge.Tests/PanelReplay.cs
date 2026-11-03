using System.Text.Json.Nodes;
using Crosscheck.Bridge.Api;
using Crosscheck.Bridge.Sim;
using Crosscheck.Bridge.Sync;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;

namespace Crosscheck.Bridge.Tests;

/// <summary>
/// Replays the gate test 2 panel stream (server/test/fixtures/gate-test-2.json, panel_stream) through the
/// bridge's status store and the panel builder, with its own clock (CRO-34).
/// </summary>
public sealed class PanelReplay
{
    public static readonly JsonObject Fixture =
        JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.Root, "server", "test", "fixtures", "gate-test-2.json")))!["panel_stream"]!.AsObject();

    public static readonly Profile Real =
        ProfileLoader.LoadFile(Path.Combine(Repo.Root, "profiles", "fenix-a320", "profile.yaml")).Profile!;

    /// <summary>Start of the replay: the guest joined at about 18:51:03 (UTC).</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 2, 18, 51, 3, TimeSpan.Zero);

    public Labels Labels { get; } = new(Real);
    public ControlMap Controls { get; }
    public BridgeStatus Status { get; } = new();
    public List<BridgeEvent> Events { get; } = [];
    public ReplayContext Context { get; }
    private readonly JsonArray _events = Fixture["events"]!.AsArray();
    private int _next;

    public PanelReplay()
    {
        Controls = new ControlMap(Real, Labels,
            UnsyncedList.LoadNextTo(Path.Combine(Repo.Root, "profiles", "fenix-a320", "profile.yaml"), Real).Labels);
        Context = new ReplayContext(Real);
    }

    public long EndMs => Fixture["end_ms"]!.GetValue<long>();
    public long PeakMs => Fixture["peak_ms"]!.GetValue<long>();

    /// <summary>Applies every event up to t (ms since the start).</summary>
    public void AdvanceTo(long t)
    {
        while (_next < _events.Count && _events[_next]![0]!.GetValue<long>() <= t)
            Apply(_events[_next++]!.AsArray());
    }

    /// <summary>Extra events (synthetic cases) at time t.</summary>
    public void Apply(JsonArray e)
    {
        var at = Start.AddMilliseconds(e[0]!.GetValue<long>());
        string S(int i) => e[i]!.GetValue<string>();
        string? N(int i) => e[i]?.GetValue<string>();
        switch (S(1))
        {
            case "d":
            {
                var body = new JsonObject
                {
                    ["var"] = S(2), ["reason"] = S(3), ["tier"] = S(4), ["expected"] = e[5]?.DeepClone(), ["observed"] = e[6]?.DeepClone(),
                };
                if (e[7] is not null || e[8] is not null) body["values"] = new JsonObject { ["pf"] = e[7]?.DeepClone(), ["pm"] = e[8]?.DeepClone() };
                if (N(9) is { } who) body["client_id"] = who == "self" ? ReplayContext.Self : "peer";
                if (e.Count > 10 && N(10) is { } group) body["group"] = group;
                Status.SetDesync(S(2), body, at);
                Events.Add(new BridgeEvent("desync", "", body, at));
                break;
            }
            case "c":
                Status.ClearDesync(S(2));
                Events.Add(new BridgeEvent("desync_cleared", "", new JsonObject { ["var"] = S(2), ["reason"] = S(3) }, at));
                break;
            case "a":
            {
                var body = new JsonObject
                {
                    ["key"] = S(2), ["kind"] = S(3), ["active"] = e[4]!.GetValue<int>() == 1, ["var"] = N(5), ["label"] = N(6), ["seat"] = N(7),
                    ["at"] = at.ToUnixTimeMilliseconds(), ["count"] = e[8]?.DeepClone(),
                };
                if (body["active"]!.GetValue<bool>()) Status.SetAdvisory(S(2), body);
                else Status.RemoveAdvisory(S(2));
                break;
            }
            case "m":
                Status.NoteActivity(S(2), at);
                break;
            case "v":
                _own[S(2)] = e[3]!.GetValue<double>();
                _ownAt[S(2)] = e[0]!.GetValue<long>();
                break;
            case "x":
                _peerDiff[S(2)] = (e[0]!.GetValue<long>(), e[3]?.GetValue<double>(), e[4]?.GetValue<double>());
                break;
        }
    }

    public JsonObject Panel(long t) => PanelLines.Build(Real, Controls, Labels, Context,
        new PanelInput(Status.Desyncs.Where(d => CanRaiseToday(d.Body, t)).ToList(), Status.Activity, Status.Advisories, Events, Start.AddMilliseconds(t)));

    // ---- what the 0.4 engine could raise with today's profile

    private readonly Dictionary<string, double> _own = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (long T, double? Pf, double? Pm)> _peerDiff = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VarEntry> _vars = Real.Vars.ToDictionary(v => v.Name, StringComparer.Ordinal);

    private double? OwnValue(string var) => _own.TryGetValue(var, out var v) ? v : null;

    private readonly Dictionary<string, long> _ownAt = new(StringComparer.Ordinal);

    /// <summary>
    /// The other cockpit's value: from its last snapshot diff, until this cockpit's value changes (the server
    /// stops sending diffs that only hold lines), else the same as this one.
    /// </summary>
    private double? PeerValue(string var, long t) =>
        _peerDiff.TryGetValue(var, out var d) && d.T >= _ownAt.GetValueOrDefault(var, long.MinValue) ? (Context.IsPf ? d.Pm : d.Pf) : OwnValue(var);

    private bool Powered(string? flag, long t) => flag is null || (OwnValue(flag) is not 0 && PeerValue(flag, t) is not 0);

    /// <summary>
    /// The stream is from bridge 0.3.2 with the profile of that day. Lines the 0.4 server and today's profile
    /// can no longer raise are left out, by the rules 0.4 applies itself: an encoder display is compared only
    /// while its unit is powered in both cockpits and its mode lamps match (compare_when, CRO-33), an indicator
    /// with a power flag only while powered (CRO-34), and a lamp check exists only where the profile has one
    /// (ECAM CLR lost it in CRO-33). Everything else is replayed as it happened.
    /// </summary>
    public bool CanRaiseToday(JsonObject body, long t)
    {
        var name = body["var"]!.GetValue<string>();
        var reason = body["reason"]?.GetValue<string>();
        if (!_vars.TryGetValue(name, out var spec)) return true;
        if (reason is "toggle_mismatch" or "effect_mismatch" && spec.Class == SyncClass.Pulse && spec.Verify?.Lvar is null) return false;
        // A counter the old profile treated as a switch (door video, cause A): today it is an edge button,
        // whose presses are mirrored as edges; a refused or corrected absolute value cannot happen any more.
        // (Not the deny list: an IDG press is still a red "not mirrored" line, whatever the old reason.)
        if (spec is { Class: SyncClass.Pulse, Press: PressMode.Edge, NeverWrite: false }
            && reason is "refused" or "no_ack" or "correction_failed" or "correction_loop" or "log_conflict" or "missing_value") return false;
        if (spec.Verify?.Lvar is { } lamp && _vars.TryGetValue(lamp, out var lampSpec) && reason is "toggle_mismatch" or "effect_mismatch"
            && !Powered(lampSpec.Powered, t)) return false;
        if (!Powered(spec.Powered, t)) return false;
        // An indicator with a tolerance (battery volts, brake pressure) within it is a match today (CRO-34).
        if (spec.Tolerance is { } tol && body["values"] is JsonObject v
            && v["pf"]?.GetValue<double>() is { } pf && v["pm"]?.GetValue<double>() is { } pm && Math.Abs(pf - pm) <= tol + 1e-9) return false;
        var encoder = spec.Class == SyncClass.Delta ? spec : Real.Vars.FirstOrDefault(v => v.Class == SyncClass.Delta && v.Display == name);
        if (encoder?.CompareWhen is { } when)
        {
            if (!Powered(when.Powered, t)) return false;
            foreach (var same in when.Same ?? [])
                if (OwnValue(same) != PeerValue(same, t)) return false;
        }
        return true;
    }

    /// <summary>The guest's view: first officer, pilot monitoring, its own client id is "self".</summary>
    public sealed class ReplayContext(Profile profile) : IPanelContext
    {
        public const string Self = "self";
        private readonly SimSafetyGuard _guard = new(profile);
        private readonly Dictionary<string, VarEntry> _vars = profile.Vars.ToDictionary(v => v.Name, StringComparer.Ordinal);
        public string? ClientId => Self;
        public bool IsPf => Fixture["is_pf"]!.GetValue<bool>();
        public string? Seat => Fixture["seat"]!.GetValue<string>();
        public string OtherSide => "captain";
        /// <summary>Local values a test sets (the log has none); otherwise the desync's own values are used.</summary>
        public Dictionary<string, double> Local { get; } = new(StringComparer.Ordinal);
        public bool TryGetLocal(string var, out double value) => Local.TryGetValue(var, out value);
        public bool CanSetBoth(string var) =>
            _vars.TryGetValue(var, out var spec) && spec.Class is SyncClass.Verified or SyncClass.Analog
            && !spec.NeverWrite && spec.Rest is null && _guard.IsWritable(var);
        public bool IsAllowedValue(string var, double value) => _guard.IsAllowedValue(var, value);
        public bool CanReleaseHere(string var, JsonObject desync) =>
            _vars.TryGetValue(var, out var spec) && spec is { Class: SyncClass.Pulse, Press: PressMode.Edge, NeverWrite: false }
            && desync["reason"]?.GetValue<string>() == "stuck_pressed" && desync["client_id"]?.GetValue<string>() == Self;
    }
}
