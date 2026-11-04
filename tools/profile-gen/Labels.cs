// Readable labels for the pilots (CRO-39): "S_FC_RUDDER_TRIM_RESET" -> "Rudder trim reset",
// "S_EXT_CARGO_BAY_AFT_LT" -> "Cargo bay light (aft)", "I_OH_ELEC_IDG1_L" -> "IDG 1 light (lower)".
//   - panel prefixes go (OH, MIP, PED, FC, EXT, FCU), as do panel names like EXT LT / INT LT;
//   - real abbreviations stay uppercase (FCU, ECAM, APU, IDG, ...), a few short forms become words
//     (LT -> light, TEMP -> temperature), everything else is lowercase, first letter capitalised;
//   - sides and positions go to one parenthesis at the end: "(Capt)", "(F/O)", "(left)", "(aft)".
//     On I_ lamps a final L / U is the button's lower / upper legend; anywhere else L / R mean left / right.
//   - MCDU and DCDU keys keep their legends as printed on the key.

using System.Text.RegularExpressions;

namespace ProfileGen;

static class Labels
{
    static readonly HashSet<string> PanelTokens = ["OH", "MIP", "PED", "FCU", "FC", "EXT"];
    /// <summary>Panel names in front of a lighting switch: "EXT LT BEACON" -> "Beacon light".</summary>
    static readonly HashSet<string> LightPanels = ["EXT", "INT", "IN"];
    public static readonly HashSet<string> SystemTokens =
        ["FLT_CTL", "ELEC", "HYD", "FUEL", "PNEUMATIC", "ENG", "FIRE", "FLT", "NAV", "COM", "CALLS", "GPWS", "OXYGEN", "CARGO", "AFT", "EVAC", "PROBE"];

    /// <summary>Kept uppercase: the abbreviations printed in the cockpit and used by pilots.</summary>
    static readonly HashSet<string> Abbreviations =
    [
        "FCU", "ECAM", "MCDU", "DCDU", "RMP", "ADIRS", "ADIRU", "ADR", "IR", "APU", "IDG", "ND", "PFD", "WX", "ASP",
        "ATC", "TCAS", "VHF", "HF", "ADF", "VOR", "ILS", "LS", "MLS", "GPWS", "FD", "AP", "ELAC", "SEC", "FAC",
        "FADEC", "PTU", "BAT", "GEN", "AC", "DC", "ESS", "CB", "GPU", "ELT", "EFIS", "EFB", "ISFD", "XPDR", "BFO",
        "AM", "PA", "INT", "N1", "FM", "FMGC", "DMC", "EIS", "LMV", "DRAIMS", "CSTR", "WPT", "VORD", "NDB", "ARPT",
        "BARO", "STD", "QNH", "ALT", "HDG", "SPD", "VS", "FPA", "LOC", "APPR", "EXPED", "ATHR", "UTC", "CHR", "ET",
        "RAT", "ACT", "IFE", "CI", "GS", "LO", "MED", "MAX", "CLR", "ENT", "TO", "HPA", "NAV", "CVR", "TERR", "SVCE",
        "MSG", "RCL", "STS", "DDRMI", "SELCAL", "PRED", "ECON", "WS", "ATT",
    ];

    /// <summary>Short forms written out.</summary>
    static readonly Dictionary<string, string> Words = new()
    {
        ["LT"] = "light", ["LTS"] = "lights", ["SEL"] = "selector", ["TEMP"] = "temperature", ["ENG"] = "engine",
        ["PWR"] = "power", ["EXT"] = "external", ["EMER"] = "emergency", ["EMERG"] = "emergency", ["MAN"] = "manual",
        ["MANSTART"] = "manual start", ["AUTOEXTING"] = "auto exting", ["STDBY"] = "standby", ["XFEED"] = "crossfeed",
        ["XBLEED"] = "crossbleed", ["XFER"] = "transfer", ["XFR"] = "transfer", ["TMR"] = "timer", ["RCRD"] = "record",
        ["AVNCS"] = "avionics", ["CKPT"] = "cockpit", ["GALY"] = "galley", ["SPKR"] = "speaker", ["CABPR"] = "cabin pressure",
        ["ANN"] = "annunciator", ["BRT"] = "bright", ["GND"] = "ground", ["ISOL"] = "isolation", ["OVRD"] = "override",
        ["OVD"] = "override", ["DISP"] = "display", ["RST"] = "reset", ["QTY"] = "quantity", ["SYS"] = "system",
        ["WR"] = "weather radar", ["ALTREPORTING"] = "altitude reporting", ["TRKFPA"] = "TRK/FPA", ["HDGVS"] = "HDG/V/S",
        ["PFDND"] = "PFD/ND", ["FPLN"] = "F-PLN", ["BUSTIE"] = "bus tie", ["REFUELINGL"] = "refueling", ["LDG"] = "landing",
        ["ELEV"] = "elevation", ["PRESEL"] = "preselect", ["COMPT"] = "compartment", ["CONSOLEFLOOR"] = "console floor",
        ["MAINT"] = "maintenance", ["MECH"] = "mechanic", ["BATT"] = "battery", ["VERTICALSPEED"] = "vertical speed",
        ["CTL"] = "control", ["CTR"] = "center", ["COL1"] = "column 1", ["COL2"] = "column 2", ["IND"] = "indicator",
        ["OVFLY"] = "overfly", ["CAB"] = "cabin", ["FWD"] = "forward", ["STAT"] = "static", ["INV"] = "inverter",
        ["REF"] = "reference", ["PRESS"] = "pressurization", ["RWY"] = "runway", ["MACH"] = "Mach", ["FLT_CTL"] = "flight control", ["ELEC"] = "electric", ["HYD"] = "hydraulic",
    };

    /// <summary>Trailing tokens that say where: they go into the parenthesis.</summary>
    static readonly Dictionary<string, string> Positions = new()
    {
        ["L"] = "left", ["LEFT"] = "left", ["R"] = "right", ["RIGHT"] = "right", ["AFT"] = "aft", ["FWD"] = "forward",
        ["UPPER"] = "upper", ["LOWER"] = "lower",
    };

    /// <summary>Units that belong to one seat: 1 is the captain's, 2 the first officer's.</summary>
    static readonly Regex SideUnit = new(@"^(EFIS|CDU|DCDU)([12])$");
    /// <summary>"ENG1" -> "ENG 1"; door ids (L1, R4), LSK ids and N1 stay as they are.</summary>
    static readonly Regex Numbered = new(@"^([A-Z]{2,})(\d)$");
    static readonly Regex KeepAsIs = new(@"^(N1|[LR]\d|LSK\d[LRB]|VU\d*|\d+[A-Z]*)$");

    static readonly HashSet<string> TwoLetterWords = ["ON", "IN", "UP", "NO", "OF", "AT", "BY", "OR"];

    public const string Capt = "Capt", Fo = "F/O";

    /// <summary>Short label (system token dropped) and long label (system token kept) for a Fenix var name.</summary>
    public static (string Short, string Long) Of(string name)
    {
        if (name.StartsWith("E_CB_")) { var l = "CB " + name[5..].Replace('_', ' '); return (l, l); }
        var kind = name[0];
        var tokens = name[2..].Split('_').ToList();
        var noun = "";            // " cover", " animation", " light", " display"
        var legend = (string?)null;
        if (tokens[^1] == "Cover") { tokens.RemoveAt(tokens.Count - 1); noun = "cover"; }
        else if (tokens[^1] == "Anim") { tokens.RemoveAt(tokens.Count - 1); noun = "animation"; }
        if (kind == 'I')
        {
            // A lamp's L / U is the legend of the button: lower / upper, never left.
            if (tokens.Count > 1 && tokens[^1] is "L" or "U" or "u") { legend = tokens[^1] == "L" ? "lower" : "upper"; tokens.RemoveAt(tokens.Count - 1); }
            noun = "light";
        }
        if (kind == 'N') noun = "display";

        // Side: CAPT / FO anywhere, or the unit number of a seat's own unit (EFIS1, CDU2, DCDU1).
        string? side = null;
        bool keyLegends = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is "CAPT" or "FO" && tokens.Count > 1) { side = tokens[i] == "CAPT" ? Capt : Fo; tokens.RemoveAt(i--); continue; }
            var m = SideUnit.Match(tokens[i]);
            if (!m.Success) continue;
            side ??= m.Groups[2].Value == "1" ? Capt : Fo;
            if (m.Groups[1].Value == "EFIS") tokens.RemoveAt(i--);   // "EFIS1 LS" -> "LS (Capt)"
            else { tokens[i] = m.Groups[1].Value == "CDU" ? "MCDU" : "DCDU"; keyLegends = true; }
        }
        if (keyLegends && tokens.Count > 1 && tokens[1] == "KEY") tokens.RemoveAt(1);

        var panel = tokens.Count > 1 && PanelTokens.Contains(tokens[0]) ? tokens[0] : null;
        if (panel is not null) tokens.RemoveAt(0);
        // "FLT CTL ELAC 1": one system name, dropped in the short label like ELEC or HYD.
        for (var i = 0; i + 1 < tokens.Count; i++)
            if (tokens[i] == "FLT" && tokens[i + 1] == "CTL") { tokens[i] = "FLT_CTL"; tokens.RemoveAt(i + 1); }
        // The ECAM control panel keys keep their legends (ECAM HYD, ECAM CAB PRESS).
        if (tokens.Count > 1 && tokens[0] == "ECAM") keyLegends = true;
        // "EXT LT BEACON" -> "BEACON light": the lighting panel's name becomes the noun.
        if (tokens.Count > 2 && LightPanels.Contains(tokens[0]) && tokens[1] == "LT")
        {
            tokens.RemoveRange(0, 2);
            noun = noun is "" or "light" ? "light" : "light " + noun;
        }
        if (tokens.Count > 1 && tokens[0] == "LT") { tokens.RemoveAt(0); if (noun == "") noun = "light"; }

        // "ECAM L" / "ECAM U" is the lower / upper ECAM display (DISPLAY BRIGHTNESS ECAM L), never left.
        string? display = null;
        if (tokens.Count > 1 && tokens[^2] == "ECAM" && tokens[^1] is "L" or "U") { display = tokens[^1] == "L" ? "lower" : "upper"; tokens.RemoveAt(tokens.Count - 1); }

        // Trailing positions, as long as a word stays in front of them.
        var where = new List<string>();
        var cdu = tokens.Count > 0 && tokens[0] is "MCDU" or "DCDU";
        if (!cdu)
            while (tokens.Count > 1 && Positions.TryGetValue(tokens[^1], out var pos) && HasWord(tokens.Take(tokens.Count - 1)))
            {
                where.Insert(0, pos);
                tokens.RemoveAt(tokens.Count - 1);
            }
        // "CARGO BAY AFT LT" -> "CARGO BAY light (aft)": a light word after the position.
        if (!cdu && tokens.Count > 2 && tokens[^1] == "LT" && Positions.TryGetValue(tokens[^2], out var before) && HasWord(tokens.Take(tokens.Count - 2)))
        {
            where.Insert(0, before);
            tokens.RemoveAt(tokens.Count - 2);
        }

        var paren = new List<string>();
        if (side is not null) paren.Add(side);
        paren.AddRange(where);
        if (display is not null) paren.Add(display);
        if (legend is not null) paren.Add(legend);

        var longText = Compose(tokens, noun, paren, keyLegends);
        // After EXT the next word is what it is (EXT CARGO DOOR), not a system panel.
        if (panel != "EXT" && tokens.Count > 1 && SystemTokens.Contains(tokens[0]) && HasWord(tokens.Skip(1)))
            tokens.RemoveAt(0);
        return (Compose(tokens, noun, paren, keyLegends), longText);
    }

    static bool HasWord(IEnumerable<string> tokens) => tokens.Any(t => !Regex.IsMatch(t, @"^\d+$"));

    static string Compose(List<string> tokens, string noun, List<string> paren, bool keyLegends)
    {
        var words = tokens.Select((t, i) => keyLegends && i > 0 ? KeyWord(t) : Word(t)).ToList();
        // No "light light": the lamp of a light switch is just the light.
        if (noun.StartsWith("light") && words.Count > 0 && words[^1] == "light") noun = noun[5..].Trim();
        if (noun != "") words.Add(noun);
        var text = string.Join(' ', words);
        if (paren.Count > 0) text += $" ({string.Join(", ", paren)})";
        return Capitalise(text);
    }

    /// <summary>One token in pilot words.</summary>
    public static string Word(string t)
    {
        if (KeepAsIs.IsMatch(t)) return t;
        if (Words.TryGetValue(t, out var w)) return w;
        if (Abbreviations.Contains(t)) return t;
        var m = Numbered.Match(t);
        if (m.Success) return Word(m.Groups[1].Value) + " " + m.Groups[2].Value;
        if (t.Length == 1) return t;                         // single letters: keys, positions, ids
        if (t.Length == 2 && !TwoLetterWords.Contains(t)) return t;   // CO, FI: unknown short forms stay as printed
        if (Regex.IsMatch(t, @"^[a-z]")) return t;           // already a word (scan names like "u")
        return t.ToLowerInvariant();
    }

    /// <summary>MCDU / DCDU keys keep the legend printed on the key; only the arrow keys are words.</summary>
    static string KeyWord(string t) => t switch
    {
        "ARROW" or "UP" or "DOWN" or "LEFT" or "RIGHT" or "BRIGHTNESS" => t.ToLowerInvariant(),
        "FPLN" => "F-PLN",
        _ => t,
    };

    public static string Capitalise(string s) => s.Length == 0 || !char.IsLower(s[0]) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>Adds a word before the parenthesis: "Landing light (left)" + "switch" -> "Landing light switch (left)".</summary>
    public static string WithWord(string label, string word)
    {
        var i = label.IndexOf(" (", StringComparison.Ordinal);
        return i < 0 ? $"{label} {word}" : $"{label[..i]} {word}{label[i..]}";
    }
}
