// Naming rules: class, group, allowed values and labels for a scanned var.
// Sources: spike/VarTypes/TYPES.md (what was proven), the scan's class guesses, profiles/SCHEMA.md.
// Anything that cannot be decided with confidence becomes a Review item, never a guess.

using System.Text.RegularExpressions;

namespace ProfileGen;

sealed record Verify(string Kind, string Target, int SettleMs, double? Scale = null);

sealed class Entry
{
    public required string Name { get; init; }
    public required string Class { get; init; }
    public required string Group { get; init; }
    public string Label { get; set; } = "";
    public double[]? Values { get; init; }
    public double[]? Range { get; init; }
    /// <summary>pulse only: momentary (1 then 0 after PulseMs) or edge (Fenix push buttons, CRO-29/30).</summary>
    public int? PulseMs { get; init; }
    public string? Press { get; init; }
    public string? Display { get; set; }
    public Verify? Verify { get; set; }
    public Dictionary<string, string>? ValueLabels { get; init; }
    /// <summary>verified, spring-loaded: the position it returns to by itself.</summary>
    public double? Rest { get; init; }
    /// <summary>observe: red comparison text, and the control it is reported under.</summary>
    public (string Text, string? As)? Alarm { get; init; }
    /// <summary>Written as a comment line above the entry (from overrides.yaml).</summary>
    public string? Comment { get; init; }
    public List<string> Notes { get; } = ["label generated"];
    public string Value { get; init; } = "";
}

/// <param name="Reason">Why the var is not in the profile.</param>
/// <param name="Kind">class = class or values undecided, group = panel group undecided, naming = not Fenix naming.</param>
sealed record Review(string Name, string Value, string Kind, string Reason, string? ProvisionalClass);

static class Rules
{
    public const string FenixPattern = @"^((S|E|A|N|I|B|CB)_[A-Z0-9][A-Za-z0-9_]*|FNX_[A-Za-z0-9_]+)$";
    static readonly Regex FenixName = new(FenixPattern);

    // Not Fenix naming, but the aircraft's own: sounds, animations, gauge host state. Never synced.
    static readonly Regex AircraftIgnore = new(@"^(A320_|A320:|A320\.|FenixLiveView_|FenixWasmInstrument_|EfbHost_)");

    // Momentary buttons (rising edge). _BUTTON/_PUSH are deliberately not here: fire buttons latch.
    static readonly Regex PulseName = new(@"_KEY_|^S_ECAM_|^S_MIP_MASTER_(CAUTION|WARNING)|_TEST|_RESET|_CALL|_RCL|_CLR|^S_DCDU[12]_(LSK|PGUP|PGDN|MSGUP|MSGDWN|PRINT)|^S_XPDR_IDENT$|^S_PED_DRAIMS\d_");

    // Could be momentary or latching, or a stepless/multi-position control: the scan cannot tell.
    static readonly Regex MomentaryOrLatching = new(@"_BUTTON|_PUSH|_UP$|_DOWN$|_BRT|_DIM|_BRIGHTNESS|CHRONO|_START|_STOP|_ENTER|_CLEAR|_ENT$|_RST|_CANCEL|_ALL$|_MAN_|_SHUTOFF|_PRINT|^S_FCU_|^S_FC_|^S_ASP|^S_PED_RMP|^S_MIP_|AMOUNT|HANDLE|_AGENT|DFDR_EVENT|RCRD|_ERASE|OVRD");
    static readonly Regex MultiPosition = new(@"MODE|_SEL|SELECT|KNOB|ZOOM|RANGE|ROTARY|_POS|LEVEL|FREQ|VOLUME|WIPER|SIGNAL|GCS|_SW$|SWITCHING|_TEMP|SCAN|GAIN|TILT|CHANNEL|PRED|SYS$|OPERATION|SPEED|SIDE|_ILS$|_MLS$|_ATC|_NAV|BKUP|_LT_|_LT$|LIGHT|SIGNS|COCKPIT_DOOR$|BLINDS|_LEVER|XFEED|HOT_AIR");

    // Binary levers the brief wants in the profile although the MIP rule above would hold them back.
    static readonly HashSet<string> KnownBinary = ["S_MIP_GEAR", "S_MIP_PARKING_BRAKE"];

    // Position counts proven in spike/VarTypes/TYPES.md.
    static readonly Dictionary<string, double[]> KnownPositions = new()
    {
        ["S_FCU_EFIS1_ND_MODE"] = [0, 1, 2, 3, 4],
        ["S_FCU_EFIS1_ND_ZOOM"] = [0, 1, 2, 3, 4, 5],
        ["S_OH_PNEUMATIC_PACK_FLOW"] = [0, 1, 2],
        ["S_OH_INT_LT_DOME"] = [0, 1, 2],
    };

    static readonly Regex Cosmetic = new(@"^(SEAT_|ARMREST_|JUMPSEAT|TRAY_TABLE|SUNSHADE_)");

    public static bool IsFenix(string name) => FenixName.IsMatch(name);

    static readonly (Regex Rx, string Group)[] GroupRules =
    [
        (new(@"^OH_"), "overhead"),
        (new(@"^FCU_EFIS"), "efis"),
        (new(@"^FCU_"), "fcu"),
        (new(@"^(MIP_|DCDU)"), "mip"),
        (new(@"^CDU1"), "cdu1"),
        (new(@"^CDU2"), "cdu2"),
        (new(@"^ECAM"), "ecam"),
        (new(@"^FC_"), "flightcontrols"),
        (new(@"^(PED_|ASP\d*_|XPDR_|TCAS_|WR_|ENG_(MASTER|MODE))"), "pedestal"),
        (new(@"^(FUEL_|HYD_PRESSURE|ELEC_VOLT|ELEC_BUS_)"), "systems"),
        (new(@"^EXT_"), "ground"),
        (new(@"^(SEAT_|ARMREST_|WINDOW_|SUNSHADE_|JUMPSEAT|TRAY_|GALLEY_|CURTAIN|CABIN_CURTAIN|COCKPIT_DOOR|STAIRS_|DOORS?_)"), "cabin"),
    ];

    public static string? GroupOf(string name)
    {
        if (name.StartsWith("E_CB_")) return "cb";
        var i = name.IndexOf('_');
        if (i < 0) return null;
        var rest = name[(i + 1)..];
        foreach (var (rx, group) in GroupRules)
            if (rx.IsMatch(rest)) return group;
        return null;
    }

    /// <summary>What the scan cannot decide for an S_ switch: null if it is safe to treat as on/off.</summary>
    static string? NotBinaryBecause(ScanVar v)
    {
        if (MomentaryOrLatching.IsMatch(v.Name)) return "momentary or latching unknown";
        if (v.Number is not (0 or 1)) return $"scan value {v.Value}, position count unknown";
        if (MultiPosition.IsMatch(v.Name)) return "name looks multi-position, position count unknown";
        return null;
    }

    /// <summary>Entry for the var, or a Review reason. Display/verify are filled in later.</summary>
    public static (Entry? Entry, Review? Review) Classify(ScanVar v)
    {
        var name = v.Name;

        if (!IsFenix(name))
            return AircraftIgnore.IsMatch(name)
                ? New(v, "ignore", "systems", note: "aircraft sound/animation/gauge var, not synced")
                : (null, new Review(name, v.Value, "naming", "not Fenix naming and no exclude pattern matches", null));

        var group = GroupOf(name);
        if (name.EndsWith("_Anim")) return WithGroup(v, group, "ignore", "animation position written by the model");
        if (name.StartsWith("FNX_")) return (null, new Review(name, v.Value, "class", "FNX_ var, no rule", null));

        var rest = name[2..];
        if (name[0] is 'S' or 'N' or 'A' && Cosmetic.IsMatch(rest))
            return WithGroup(v, group, "ignore", "cosmetic cabin item");

        switch (name[0])
        {
            case 'I':
                return WithGroup(v, group, "observe");
            case 'N':
            case 'B':
                if (v.ScanClass == "stream") return Undecided(v, group, "observe", "value changes without input, would raise false alarms");
                if (name.Contains("_CONFIG_")) return Undecided(v, group, "ignore", "per-pilot setup, not cockpit state");
                return WithGroup(v, group, "observe");
            case 'E' when name.StartsWith("E_CB_"):
                return Make(v, group, "verified", values: [0, 1], note: "semantics unverified");
            case 'E':
                return WithGroup(v, group, "delta"); // display var is paired in a second pass
            case 'A':
                // No GraphQL check: system.analog.* did not follow value x 1024 in gate test 1 (all analog
                // acks failed), so an analog write is confirmed by read-back of its own L: var. Scale: CRO-28.
                return Make(v, group, "analog", range: [0, 1],
                    note: "confirmed by own L: var read-back; GraphQL scale unverified (CRO-28)");
            case 'S':
                return ClassifySwitch(v, group);
        }
        return (null, new Review(name, v.Value, "class", "no rule", null));
    }

    static (Entry?, Review?) ClassifySwitch(ScanVar v, string? group)
    {
        var name = v.Name;
        if (name.EndsWith("_Cover"))
            return Make(v, group, "verified", values: [0, 1],
                valueLabels: new() { ["0"] = "closed", ["1"] = "open" });

        // Every Fenix S_ push button counts +1 on press and +1 on release (observe-report.md, CRO-29).
        if (PulseName.IsMatch(name))
            return Make(v, group, "pulse", press: "edge");

        if (KnownPositions.TryGetValue(name, out var known))
            return Make(v, group, "verified", values: known, note: "positions proven in spike/VarTypes");

        var why = KnownBinary.Contains(name) ? null : NotBinaryBecause(v);
        if (why is not null) return Undecided(v, group, "verified", why);
        return Make(v, group, "verified", values: [0, 1], note: "values [0,1] from name and scan value, confirm in step 2");
    }

    static (Entry?, Review?) New(ScanVar v, string cls, string group, string? note = null)
    {
        var e = new Entry { Name = v.Name, Class = cls, Group = group, Value = v.Value };
        if (note is not null) e.Notes.Add(note);
        return (e, null);
    }

    static (Entry?, Review?) WithGroup(ScanVar v, string? group, string cls, string? note = null) =>
        group is null ? Undecided(v, null, cls, null) : New(v, cls, group, note);

    static (Entry?, Review?) Make(ScanVar v, string? group, string cls,
        double[]? values = null, double[]? range = null, string? press = null,
        Verify? verify = null, Dictionary<string, string>? valueLabels = null, string? note = null)
    {
        if (group is null) return Undecided(v, null, cls, null);
        var e = new Entry
        {
            Name = v.Name, Class = cls, Group = group, Value = v.Value, Values = values, Range = range,
            Press = press, Verify = verify, ValueLabels = valueLabels,
        };
        if (note is not null) e.Notes.Add(note);
        return (e, null);
    }

    /// <summary>Review item. classReason null means the class is fine and only the group is unclear.</summary>
    static (Entry?, Review?) Undecided(ScanVar v, string? group, string provisional, string? classReason)
    {
        var parts = new List<string>();
        if (classReason is not null) parts.Add(classReason);
        if (group is null) parts.Add("panel group unclear");
        var kind = classReason is not null ? "class" : "group";
        return (null, new Review(v.Name, v.Value, kind, string.Join("; ", parts), provisional));
    }

    // ---- labels ----

    static readonly HashSet<string> PanelTokens = ["OH", "MIP", "PED", "FCU"];
    static readonly HashSet<string> SystemTokens =
        ["ELEC", "HYD", "FUEL", "PNEUMATIC", "ENG", "FIRE", "FLT", "NAV", "COM", "CALLS", "GPWS", "OXYGEN", "CARGO", "AFT", "EVAC", "PROBE"];

    /// <summary>Short label (system token dropped) and long label (system token kept) for the entry.</summary>
    public static (string Short, string Long) LabelOf(string name)
    {
        if (!IsFenix(name)) return (name, name);
        if (name.StartsWith("E_CB_")) { var l = "CB " + name[5..].Replace('_', ' '); return (l, l); }
        var kind = name[0];
        var tokens = name[2..].Split('_').ToList();
        var suffix = "";
        if (tokens[^1] == "Cover") { tokens.RemoveAt(tokens.Count - 1); suffix = " cover"; }
        else if (tokens[^1] == "Anim") { tokens.RemoveAt(tokens.Count - 1); suffix = " animation"; }
        if (kind == 'I')
        {
            if (tokens[^1] is "L" or "U") { suffix = tokens[^1] == "L" ? " light (lower)" : " light (upper)"; tokens.RemoveAt(tokens.Count - 1); }
            else suffix = " light";
        }
        if (kind == 'N') suffix = " display";
        if (kind == 'S' && tokens.Count > 1 && Regex.IsMatch(tokens[0], "^CDU[12]$") && tokens[1] == "KEY") tokens.RemoveAt(1);
        if (tokens.Count > 1 && Regex.IsMatch(tokens[0], "^CDU[12]$")) tokens[0] = "CDU " + tokens[0][3];
        if (tokens.Count > 1 && PanelTokens.Contains(tokens[0])) tokens.RemoveAt(0);
        var longText = string.Join(' ', tokens) + suffix;
        if (tokens.Count > 1 && SystemTokens.Contains(tokens[0])) tokens.RemoveAt(0);
        return (string.Join(' ', tokens) + suffix, longText);
    }
}
