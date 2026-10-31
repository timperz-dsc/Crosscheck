// First-guess sync class per L: var, from the naming and what spike/VarTypes/TYPES.md found.
// Deliberately simple: the classes are curated by hand later (CRO-8).

using System.Text.RegularExpressions;

static class Classifier
{
    // Fenix names are PREFIX_UPPERCASE with an optional mixed-case suffix (_Cover, _Anim, _u).
    public const string FenixPattern = @"^((S|E|A|N|I|B|CB)_[A-Z0-9][A-Za-z0-9_]*|FNX_[A-Za-z0-9_]+)$";
    static readonly Regex FenixName = new(FenixPattern);
    static readonly Regex PulseName = new(@"_KEY_|^S_ECAM_|^S_MIP_MASTER_(CAUTION|WARNING)|_BUTTON|_PUSH|_TEST|_RESET|_CALL|_RCL|_CLR");

    public static bool IsFenix(string name) => FenixName.IsMatch(name);

    public static string Prefix(string name) =>
        name.StartsWith("CB_") ? "CB_" :
        name.Length > 2 && name[1] == '_' && "SEANI".Contains(name[0]) ? name[..2] :
        "other";

    /// <param name="changed">The value changed between scans without any input.</param>
    public static (string Class, string Reason) Classify(string name, bool changed)
    {
        if (!IsFenix(name)) return changed ? ("stream", "not Fenix naming; value changed between scans") : ("unknown", "not Fenix naming");
        if (name.EndsWith("_Anim")) return ("observe", "_Anim animation position, written by the model");
        if (name.StartsWith("I_")) return ("observe", "I_ indicator, read-only");
        if (name.StartsWith("E_CB_")) return ("verified", "E_CB_ circuit breaker (panel/position); semantics unverified");
        if (name.StartsWith("E_")) return ("delta", "E_ encoder, relative counter");
        if (name.StartsWith("A_")) return ("analog", "A_ knob, 0..1");
        if (name.EndsWith("_Cover")) return ("verified", "guard cover, absolute 0/1");
        if (name.StartsWith("S_") && PulseName.IsMatch(name)) return ("pulse", "S_ pushbutton naming, rising edge");
        if (name.StartsWith("S_")) return ("verified", "S_ switch, absolute position");
        if (name.StartsWith("CB_")) return ("verified", "circuit breaker, absolute 0/1");
        if (changed) return ("stream", "value changed between scans");
        if (name.StartsWith("N_") || name.StartsWith("B_")) return ("observe", "N_/B_ state written by Fenix");
        return ("unknown", "no rule");
    }

    /// <summary>Grouping key for names that are not Fenix: the leading token before the first '_'.</summary>
    public static string Group(string name)
    {
        int i = name.IndexOf('_');
        return i > 0 ? name[..i] + "_*" : name;
    }
}
