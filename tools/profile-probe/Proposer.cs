// Offline: turns the probe results into probe-results.csv, probe-report.md and
// profile.proposed.yaml. profile.yaml itself is never written.

using System.Globalization;
using System.Text;
using Crosscheck.Profiles;
using Crosscheck.Profiles.Model;
using ProfileGen;

namespace ProfileProbe;

sealed record Change(string Kind, string Var, string Before, string After, string Why);

sealed class NewEntry
{
    public required string Name { get; init; }
    public required string Class { get; init; }
    public required string Group { get; init; }
    public string Label { get; set; } = "";
    public double[]? Values { get; set; }
    public int? PulseMs { get; init; }
    public bool Counter { get; init; }
    public string? Display { get; init; }
    public string? Verify { get; set; }
    public Dictionary<string, string>? ValueLabels { get; set; }
    public string Note { get; set; } = "";
}

static class Proposer
{
    const string SectionBegin = "  # >>> proposed by tools/profile-probe (CRO-8 step 2), review in step 3 <<<";
    const string SectionEnd = "  # >>> end proposed <<<";

    static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    static string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    static string ListText(IEnumerable<double> a) => "[" + string.Join(", ", a.Select(N)) + "]";
    static string LampVerify(string lamp) => $"{{ lvar: {lamp}, settle_ms: 150 }}";
    static string GqlVerify(string path) => $"{{ graphql: {path}, settle_ms: 100 }}";

    public static int Run(RepoPaths paths, TextWriter output)
    {
        var inputs = Inputs.Load(paths);
        var results = new ResultLog(paths.Details).ReadLatest();
        var window = CrashWindowOf(results);
        // AIRAC is no longer read automatically (dropped after the first run); its old rows go.
        results = results.Where(r => r.Test != "graphql-airac").Select(DropNoiseEvidence).Select(r => AfterCrash(r, window)).Select(PositionUnknown).ToList();
        var checkpoint = File.Exists(paths.Checkpoint) ? new Checkpoint(paths.Checkpoint) : null;
        // The probe could not restore these, but they are no longer listed as not restored: the
        // pilot put them back by hand or checked the cockpit (see the manual rows).
        results = results.Select(r => !r.Restored && checkpoint is not null && !checkpoint.Data.NotRestored.Contains(r.Var)
            ? r with { Notes = r.Notes.Replace("NOT RESTORED: ", "the probe could not restore it, the pilot did or confirmed the cockpit afterwards: ") }
            : r).ToList();
        WriteCsv(paths.ResultsCsv, results);
        var (text, changes, unresolved) = Propose(inputs, results);
        File.WriteAllText(paths.Proposed, text);
        var check = ProfileLoader.LoadText(text, "profile.proposed.yaml");
        File.WriteAllText(paths.Report, Report(inputs, results, changes, unresolved, check, checkpoint?.Data.NotRestored ?? [], window));
        output.WriteLine($"{results.Count} results, {changes.Count} proposed changes, {unresolved.Count} unresolved");
        output.WriteLine(check.IsValid ? "profile.proposed.yaml validates" : "profile.proposed.yaml has validation issues:\n" + string.Join("\n", check.FormatIssues()));
        return check.IsValid ? 0 : 1;
    }

    static void WriteCsv(string path, List<ProbeResult> results)
    {
        var sb = new StringBuilder("var,test,result,stored_ok,effect_ok,latency_ms,notes\n");
        foreach (var r in results)
            sb.Append(string.Join(",", Csv.Field(r.Var), r.Test, r.Result, Bool(r.StoredOk), Bool(r.EffectOk),
                r.LatencyMs is { } l ? l.ToString("0.0", CultureInfo.InvariantCulture) : "", Csv.Field(r.Notes))).Append('\n');
        File.WriteAllText(path, sb.ToString());
        static string Bool(bool? b) => b is null ? "" : b.Value ? "yes" : "no";
    }

    // ------------------------------------------------------------------ profile text patching

    sealed class Doc(string text)
    {
        public List<string> Lines { get; } = text.Replace("\r\n", "\n").Split('\n').ToList();

        public (int Start, int End)? Block(string name)
        {
            var start = Lines.IndexOf("  - name: " + name);
            if (start < 0) return null;
            var end = start + 1;
            while (end < Lines.Count && Lines[end].StartsWith("    ", StringComparison.Ordinal)) end++;
            return (start, end);
        }

        public string? Get(string name, string key)
        {
            if (Block(name) is not var (s, e)) return null;
            for (var i = s + 1; i < e; i++)
                if (Lines[i].StartsWith($"    {key}: ", StringComparison.Ordinal)) return Lines[i][(key.Length + 6)..];
            return null;
        }

        static readonly string[] Order = ["label", "value_labels", "class", "group", "values", "range", "pulse_ms", "press", "display", "verify", "note"];

        public void Set(string name, string key, string value)
        {
            if (Block(name) is not var (s, e)) return;
            for (var i = s + 1; i < e; i++)
                if (Lines[i].StartsWith($"    {key}: ", StringComparison.Ordinal)) { Lines[i] = $"    {key}: {value}"; return; }
            // insert in the usual field order
            var rank = Array.IndexOf(Order, key);
            var at = e;
            for (var i = s + 1; i < e; i++)
            {
                var k = Lines[i].Trim().Split(':')[0];
                if (Array.IndexOf(Order, k) > rank) { at = i; break; }
            }
            Lines.Insert(at, $"    {key}: {value}");
        }

        public void Remove(string name, string key)
        {
            if (Block(name) is not var (s, e)) return;
            for (var i = s + 1; i < e; i++)
                if (Lines[i].StartsWith($"    {key}: ", StringComparison.Ordinal)) { Lines.RemoveAt(i); return; }
        }

        public void Note(string name, string text, string? replace = null)
        {
            var note = Get(name, "note");
            var current = note is null ? "" : note.Trim().Trim('"');
            if (replace is not null && current.Contains(replace)) current = current.Replace(replace, text);
            else current = current.Length == 0 ? text : current + "; " + text;
            Set(name, "note", Q(current));
        }

        public void ReplaceLine(string startsWith, string replacement)
        {
            var i = Lines.FindIndex(l => l.TrimStart().StartsWith(startsWith, StringComparison.Ordinal));
            if (i < 0) return;
            var indent = Lines[i][..(Lines[i].Length - Lines[i].TrimStart().Length)];
            Lines[i] = indent + replacement;
        }

        public void InsertSection(IEnumerable<string> lines)
        {
            var at = Lines.IndexOf("datarefs:");
            if (at < 0) at = Lines.Count;
            Lines.InsertRange(at, lines.Append(""));
        }

        public override string ToString() => string.Join("\n", Lines);
    }

    public static (string Text, List<Change> Changes, List<(string Var, string Phase, string Why)> Unresolved) Propose(Inputs inputs, List<ProbeResult> results)
    {
        var known = KnownValues.Build(inputs, results);
        var window = CrashWindowOf(results);
        results = results.Select(DropNoiseEvidence).Select(r => AfterCrash(r, window)).Select(PositionUnknown).ToList();
        var doc = new Doc(inputs.ProfileText);
        var changes = new List<Change>();
        var unresolved = new List<(string, string, string)>();
        var added = new Dictionary<string, NewEntry>(StringComparer.Ordinal);
        bool InProfile(string n) => doc.Block(n) is not null;
        ProbeResult? Find(string var, string test) => results.FirstOrDefault(r => r.Var == var && r.Test == test);

        void Promote(ProbeResult r, NewEntry e, string kind)
        {
            if (Rules.GroupOf(r.Var) is null)
            {
                unresolved.Add((r.Var, r.Phase.ToString(), $"{kind} found ({r.Result}), but the panel group is unclear, so it stays on the review list"));
                return;
            }
            e.Label = Rules.LabelOf(r.Var).Short;
            added[e.Name] = e;
            changes.Add(new Change(kind, r.Var, "review list", Describe(e), r.Notes));
        }

        foreach (var r in results)
        {
            switch (r.Phase, r.Test)
            {
                // 1: verified [0,1] guesses
                case (Phase.Verified, "verified-binary"):
                    var oldVerify = doc.Get(r.Var, "verify");
                    if (r.Result == "confirmed")
                    {
                        if (r.Var == Targets.ApuMaster) doc.Note(r.Var, "Effect lamp confirmed in CRO-8 step 2.", "Effect lamp is not proven yet, confirm in CRO-8.");
                        else doc.Note(r.Var, "values [0,1] confirmed in step 2", "values [0,1] from name and scan value, confirm in step 2");
                        changes.Add(new Change("[0,1] confirmed", r.Var, "values [0, 1] (guess)", "values [0, 1]", r.Notes));
                        // A lamp that reacted wins; GraphQL only where there is no effect source yet.
                        var lampHalf = Find(r.Var, "lamp-half");
                        var lamp = lampHalf?.ProposedVerifyLvar ?? r.ProposedVerifyLvar;
                        var newVerify = lamp is not null ? LampVerify(lamp)
                            : oldVerify is null && r.ProposedVerifyGraphql is { } gp ? GqlVerify(gp) : null;
                        if (newVerify is not null && VerifyTarget(oldVerify) != (lamp ?? r.ProposedVerifyGraphql))
                        {
                            doc.Set(r.Var, "verify", newVerify);
                            changes.Add(new Change("effect source", r.Var, oldVerify ?? "none", newVerify, lampHalf?.Notes ?? r.Notes));
                        }
                    }
                    else if (r.Result == "rejected")
                    {
                        var pos = Find(r.Var, "positions");
                        if (pos is { Result: "positions", ProposedValues: { } pv })
                        {
                            doc.Set(r.Var, "values", ListText(pv));
                            doc.Note(r.Var, $"positions {string.Join(",", pv.Select(N))} found in step 2", "values [0,1] from name and scan value, confirm in step 2");
                            if (pos.ProposedVerifyGraphql is { } gp2 && doc.Get(r.Var, "verify") is null) doc.Set(r.Var, "verify", GqlVerify(gp2));
                            changes.Add(new Change("position count", r.Var, "values [0, 1] (guess)", "values " + ListText(pv), pos.Notes));
                        }
                        else
                        {
                            // Range rule: only the values actually seen, never a guessed range.
                            var seen = known.Seen(r.Var);
                            doc.Set(r.Var, "values", ListText(seen));
                            doc.Note(r.Var, $"step 2: 2 is a distinct state; values {ListText(seen)} are only those seen; {Targets.PositionsOff}",
                                "values [0,1] from name and scan value, confirm in step 2");
                            changes.Add(new Change("values seen (position count unknown)", r.Var, "values [0, 1] (guess)", "values " + ListText(seen), r.Notes));
                            unresolved.Add((r.Var, "Positions", $"{Targets.PositionsOff}; [0,1] rejected ({r.Notes}); values seen: {ListText(seen)}"));
                        }
                    }
                    else if (r.Result == "counter-suspected")
                    {
                        doc.Note(r.Var, "step 2: behaves like a press counter, [0,1] wrong", "values [0,1] from name and scan value, confirm in step 2");
                        unresolved.Add((r.Var, "Verified", "press counter suspected: " + r.Notes));
                    }
                    else if (r.Result is "unresolved")
                    {
                        doc.Note(r.Var, "step 2: " + r.Notes + "; [0,1] unconfirmed", "values [0,1] from name and scan value, confirm in step 2");
                        unresolved.Add((r.Var, "Verified", r.Notes));
                    }
                    break;

                // 4: lamp halves where the lower half did not react
                case (Phase.Verified, "lamp-half"):
                    if (r.Result == "neither") unresolved.Add((r.Var, "Lamp half", "neither half changed at the gate; lower-half rule kept"));
                    break;

                // 2: buttons
                case (Phase.Buttons, "momentary-or-latching"):
                    if (r.Result is "pulse" or "counter")
                        Promote(r, new NewEntry
                        {
                            Name = r.Var, Class = "pulse", Group = Rules.GroupOf(r.Var) ?? "", PulseMs = 150, Counter = r.ProposedCounter,
                            Verify = r.ProposedVerifyLvar is { } l ? LampVerify(l) : null, Note = "class from step 2: " + r.Notes,
                        }, r.Result == "counter" ? "press counter" : "momentary (pulse)");
                    else if (r.Result == "follows-value")
                        Promote(r, new NewEntry
                        {
                            Name = r.Var, Class = "verified", Group = Rules.GroupOf(r.Var) ?? "", Values = [0, 1],
                            Verify = r.ProposedVerifyLvar is { } l2 ? LampVerify(l2) : null, Note = "class from step 2: " + r.Notes,
                        }, "latching (verified)");
                    else if (r.Result != "skipped") unresolved.Add((r.Var, "Buttons", r.Notes));
                    break;

                // 3: positions. The stepping test is switched off (it most likely crashed the
                // Fenix WASM); these switches are handled after the loop.
                case (Phase.Positions, "positions"):
                    break;

                // 5: encoders
                case (Phase.Encoders, "encoder-display"):
                    if (r.Result == "display" && r.ProposedDisplay is { } display)
                    {
                        Promote(r, new NewEntry { Name = r.Var, Class = "delta", Group = Rules.GroupOf(r.Var) ?? "", Display = display, Note = "display from step 2: " + r.Notes }, "encoder display");
                        if (added.ContainsKey(r.Var) && !InProfile(display) && !added.ContainsKey(display) && Rules.GroupOf(display) is { } dg)
                            added[display] = new NewEntry { Name = display, Class = "observe", Group = dg, Label = Rules.LabelOf(display).Short, Note = $"display of {r.Var}, added in step 2" };
                    }
                    else if (r.Result != "skipped") unresolved.Add((r.Var, "Encoders", r.Notes));
                    break;

                // 6: breakers
                case (Phase.Breakers, "breaker"):
                    if (r.Result == "invalid-crash") { unresolved.Add((r.Var, "Breakers", r.Notes)); break; }
                    var what = r.Result switch
                    {
                        "breaker-01" => "step 2: 0/1 breaker, 0 = pulled",
                        "breaker-counter" => "step 2: behaves like a counter, not 0/1",
                        _ => "step 2: no visible effect when pulled at the gate",
                    };
                    doc.Note(r.Var, what, "semantics unverified");
                    changes.Add(new Change("breaker semantics", r.Var, "semantics unverified", what, r.Notes));
                    if (r.Result != "breaker-01") unresolved.Add((r.Var, "Breakers", r.Notes));
                    break;

                // 7: noise
                case (Phase.Readonly, "noise-30s"):
                    if (InProfile(r.Var))
                    {
                        if (r.Result == "noisy" && doc.Get(r.Var, "class") == "observe")
                        {
                            doc.Set(r.Var, "class", "ignore");
                            doc.Note(r.Var, "step 2: changes with no input");
                            changes.Add(new Change("observe -> ignore", r.Var, "observe", "ignore", r.Notes));
                        }
                    }
                    else
                        Promote(r, new NewEntry { Name = r.Var, Class = r.ProposedClass ?? "ignore", Group = Rules.GroupOf(r.Var) ?? "", Note = "step 2: " + r.Notes }, "noisy var decided");
                    break;
            }
        }

        // 3: switches with an unknown position count. Range rule: each gets only the values seen
        // in the scan and in valid probe results, never a guessed range, until real pilot input
        // shows the positions (CRO-24).
        var rejected = results.Where(r => r.Test == "verified-binary" && r.Result == "rejected").Select(r => r.Var).ToHashSet();
        // Only the switches the first run actually stepped; the ones never probed (deny list,
        // safety skips) keep their skip reason.
        foreach (var name in results.Where(r => r.Result == "position-unknown").Select(r => r.Var).Distinct())
        {
            if (InProfile(name) || rejected.Contains(name)) continue;
            var seen = known.Seen(name);
            var why = Targets.PositionsOff;
            if (seen.Count == 0 || Rules.GroupOf(name) is not { } group)
            {
                unresolved.Add((name, "Positions", why + (seen.Count == 0 ? "; no value seen" : "; panel group unclear, stays on the review list")));
                continue;
            }
            added[name] = new NewEntry
            {
                Name = name, Class = "verified", Group = group, Label = Rules.LabelOf(name).Short, Values = seen.ToArray(),
                Note = $"values {ListText(seen)} are only those seen in the scan and probe; {Targets.PositionsOff}",
            };
            changes.Add(new Change("values seen (position count unknown)", name, "review list", "verified " + ListText(seen), "seen in the scan and probe results"));
            unresolved.Add((name, "Positions", $"{why}; values seen: {ListText(seen)}"));
        }

        // supervised a: takeover buttons
        var takeover = Targets.TakeoverVars.Select(v => Find(v, "takeover-hold-1s")).ToList();
        if (takeover.All(t => t?.Result == "momentary-held"))
        {
            doc.ReplaceLine("# TODO(CRO-8): confirm names and values in the sim", "# Confirmed in CRO-8 step 2: 1 while held, 0 after release.");
            doc.ReplaceLine("# TODO(CRO-8): confirm these are the takeover pushbuttons", "# Confirmed in CRO-8 step 2 (1 while held, 0 after release).");
            changes.Add(new Change("takeover confirmed", string.Join(", ", Targets.TakeoverVars), "TODO", "confirmed", string.Join(" | ", takeover.Select(t => t!.Notes))));
        }
        else foreach (var t in Targets.TakeoverVars)
            unresolved.Add((t, "Takeover", Find(t, "takeover-hold-1s")?.Notes ?? "supervised check not run"));

        // supervised e: aircraft title
        if (results.FirstOrDefault(r => r.Var == "TITLE" && r.Test == "simvar") is { } title && title.Notes is { Length: > 0 } tt && !tt.StartsWith('('))
        {
            doc.ReplaceLine("# TODO(CRO-8): add `titles:`", $"titles: [{Q(tt)}]");
            changes.Add(new Change("match.titles", "aircraft.match", "TODO", $"titles: [{Q(tt)}]", "TITLE simvar read in step 2"));
        }
        else unresolved.Add(("match.titles", "Readonly", "TITLE simvar not read"));

        // supervised b: ND labels
        foreach (var g in results.Where(r => r.Result == "label" && r.ProposedValueLabels is not null).GroupBy(r => r.Var))
        {
            var labels = g.SelectMany(r => r.ProposedValueLabels!).GroupBy(kv => kv.Key).ToDictionary(x => x.Key, x => x.Last().Value);
            var ordered = labels.OrderBy(kv => double.Parse(kv.Key, CultureInfo.InvariantCulture)).ToList();
            var text = "{ " + string.Join(", ", ordered.Select(kv => $"\"{kv.Key}\": {Q(kv.Value)}")) + " }";
            if (added.TryGetValue(g.Key, out var ne))
            {
                ne.ValueLabels = ordered.ToDictionary(kv => kv.Key, kv => kv.Value);
                ne.Values = known.For(g.Key).ToArray();
            }
            else if (InProfile(g.Key)) doc.Set(g.Key, "value_labels", text);
            else if (inputs.Review.Any(r => r.Name == g.Key) && Rules.GroupOf(g.Key) is { } lg)
            {
                // A review var (FO ND MODE/RANGE): the pilot read every value on the ND.
                added[g.Key] = new NewEntry
                {
                    Name = g.Key, Class = "verified", Group = lg, Label = Rules.LabelOf(g.Key).Short, Values = known.For(g.Key).ToArray(),
                    ValueLabels = ordered.ToDictionary(kv => kv.Key, kv => kv.Value),
                    Note = "values and labels read from the ND by the pilot in step 2",
                };
                unresolved.RemoveAll(u => u.Item1 == g.Key);
            }
            else continue;
            changes.Add(new Change("value labels", g.Key, "none", text, "read from the ND by the pilot"));
        }

        if (added.Count > 0)
        {
            var lines = new List<string> { SectionBegin };
            foreach (var e in added.Values.OrderBy(e => e.Group).ThenBy(e => e.Name, StringComparer.Ordinal)) lines.AddRange(EntryLines(e));
            lines.Add(SectionEnd);
            doc.InsertSection(lines);
        }
        return (doc.ToString(), changes, unresolved);
    }

    /// <summary>
    /// Time after which Fenix no longer reacted (recorded with `info --var crash-cutoff --text
    /// &lt;UTC time&gt;`). In the first run the Fenix WASM crashed; afterwards writes were stored but
    /// nothing reacted and GraphQL answered nothing, so those results say nothing about Fenix.
    /// </summary>
    internal static DateTime? CrashCutoff(IEnumerable<ProbeResult> results) => Time(results, "crash-cutoff");

    /// <summary>
    /// When the aircraft was reloaded after the crash (`info --var crash-end --text &lt;UTC time&gt;`).
    /// Results after it are valid again.
    /// </summary>
    internal static DateTime? CrashEnd(IEnumerable<ProbeResult> results) => Time(results, "crash-end");

    internal static (DateTime Start, DateTime? End)? CrashWindowOf(IEnumerable<ProbeResult> results)
    {
        var list = results as IList<ProbeResult> ?? results.ToList();
        return CrashCutoff(list) is { } start ? (start, CrashEnd(list)) : null;
    }

    static DateTime? Time(IEnumerable<ProbeResult> results, string var) =>
        results.LastOrDefault(r => r.Var == var) is { } c
        && DateTime.TryParse(c.Notes, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    /// <summary>
    /// The first run's position results (all after the crash) become "position-unknown": the test
    /// is switched off and the positions are learned from pilot input instead.
    /// </summary>
    internal static ProbeResult PositionUnknown(ProbeResult r) =>
        r is { Phase: Phase.Positions, Test: "positions" } && r.Result is not ("skipped" or "position-unknown")
            ? r with
            {
                Result = "position-unknown", ProposedClass = null, ProposedValues = null, ProposedVerifyGraphql = null,
                Notes = Targets.PositionsOff + "; first run: " + r.Notes,
            }
            : r;

    internal static ProbeResult AfterCrash(ProbeResult r, (DateTime Start, DateTime? End)? window) =>
        window is { } w && r.At.ToUniversalTime() > w.Start && (w.End is null || r.At.ToUniversalTime() < w.End)
        && r.Phase is Phase.Verified or Phase.Buttons or Phase.Positions or Phase.Encoders or Phase.Breakers or Phase.Takeover
        && r.Result is not ("skipped" or "info" or "invalid-crash" or "position-unknown")
            ? r with
            {
                Result = "invalid-crash", StoredOk = null, EffectOk = null, ProposedClass = null, ProposedValues = null,
                ProposedVerifyLvar = null, ProposedVerifyGraphql = null, ProposedDisplay = null, ProposedCounter = false,
                Notes = "not tested: probed after the Fenix WASM crash, nothing could react; " + r.Notes,
            }
            : r;

    /// <summary>A var list from the raw data that names only model animation/sound vars (A320_*).</summary>
    static bool OnlyNoise(Dictionary<string, string>? data, string key) =>
        data is not null && data.TryGetValue(key, out var list) && !list.Contains("(+")
        && list.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } names && names.All(ProbeSession.NotEvidence);

    /// <summary>
    /// Early runs counted model animations (engine blades turning in the wind) as an effect.
    /// A result whose only evidence is such a var is not evidence of anything.
    /// </summary>
    internal static ProbeResult DropNoiseEvidence(ProbeResult r)
    {
        const string why = "only model animation vars (A320_*) moved, no lamp, GraphQL or system var: no evidence";
        return r switch
        {
            { Test: "verified-binary", Result: "confirmed" } when r.Notes.StartsWith("other vars changed") && OnlyNoise(r.Data, "changed")
                => r with { Result = "unresolved", EffectOk = false, ProposedClass = null, ProposedValues = null, Notes = why + "; " + r.Notes },
            { Test: "momentary-or-latching", Result: "pulse" or "follows-value" } when r.ProposedVerifyLvar is null && OnlyNoise(r.Data, "on_press")
                => r with { Result = "unresolved", EffectOk = false, ProposedClass = null, ProposedValues = null, Notes = why + "; " + r.Notes },
            { Test: "positions", Result: "positions" } when r.Notes.Contains("effect/animation vars") && OnlyNoise(r.Data, "effect_vars")
                => r with { Result = "unresolved", EffectOk = false, ProposedClass = null, ProposedValues = null, Notes = why + "; " + r.Notes },
            { Test: "breaker", Result: "breaker-01" } when OnlyNoise(r.Data, "effects")
                => r with { Result = "no-effect", EffectOk = false, Notes = why + "; " + r.Notes },
            { Test: "breaker", Result: "breaker-counter" } when OnlyNoise(r.Data, "counter_effects")
                => r with { Result = "no-effect", EffectOk = false, Notes = why + "; " + r.Notes },
            _ => r,
        };
    }

    /// <summary>The lvar or graphql name in a verify text like "{ lvar: I_X_L, settle_ms: 150 }".</summary>
    internal static string? VerifyTarget(string? verify)
    {
        if (verify is null) return null;
        var m = System.Text.RegularExpressions.Regex.Match(verify, @"(?:lvar|graphql):\s*([^,\s}]+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    static string Describe(NewEntry e) =>
        e.Class + (e.Values is { } v ? " " + ListText(v) : "") + (e.Counter ? " (counter)" : "") + (e.Display is { } d ? $" display {d}" : "") + (e.Verify is { } vf ? $" verify {vf}" : "");

    static IEnumerable<string> EntryLines(NewEntry e)
    {
        yield return "  - name: " + e.Name;
        yield return "    label: " + Q(e.Label);
        if (e.ValueLabels is { Count: > 0 } vl) yield return "    value_labels: { " + string.Join(", ", vl.Select(p => $"\"{p.Key}\": {Q(p.Value)}")) + " }";
        yield return "    class: " + e.Class;
        yield return "    group: " + e.Group;
        if (e.Values is { } v) yield return "    values: " + ListText(v);
        if (e.Class == "pulse") yield return "    press: edge"; // every Fenix push button (CRO-29)
        if (e.Display is { } d) yield return "    display: " + d;
        if (e.Verify is { } vf) yield return "    verify: " + vf;
        yield return "    note: " + Q("label generated; " + e.Note);
    }

    // ------------------------------------------------------------------ report

    static string Report(Inputs inputs, List<ProbeResult> results, List<Change> changes, List<(string Var, string Phase, string Why)> unresolved,
        ProfileLoadResult check, List<string> notRestored, (DateTime Start, DateTime? End)? window)
    {
        var sb = new StringBuilder();
        sb.Append("# Fenix A320 profile probe report (CRO-8 step 2)\n\n");
        sb.Append("Generated by `tools/profile-probe propose` from the probe runs in the sim. `profile.yaml` was not changed; ");
        sb.Append("`profile.proposed.yaml` is `profile.yaml` with every proposal below applied, for step 3 to diff. Raw rows: `probe-results.csv`.\n\n");

        sb.Append("## Summary\n\n");
        var probed = results.Where(r => r.Result is not ("info" or "found" or "not found" or "skipped" or "label")).ToList();
        sb.Append($"- vars probed: {probed.Select(r => r.Var).Distinct().Count()} ({probed.Count} results)\n");
        sb.Append($"- skipped (deny list, extra skips, original value outside the test values): {results.Count(r => r.Result == "skipped")}\n");
        sb.Append($"- not restored: {(notRestored.Count == 0 ? "none" : string.Join(", ", notRestored))}\n");
        sb.Append($"- proposed changes: {changes.Count}; unresolved: {unresolved.Count}\n");
        sb.Append($"- profile.proposed.yaml: {(check.IsValid ? "validates with Crosscheck.Profiles" : "**does not validate**")}\n\n");
        if (window is { } w)
            sb.Append($"**Validity:** the Fenix WASM crashed during the first run. Results from {w.Start:u} " +
                      (w.End is { } end ? $"until the aircraft was reloaded ({end:u})" : "on") +
                      " are marked `invalid-crash` (not tested): Fenix stored the writes but nothing reacted. Everything else is valid. See Surprises.\n\n");
        sb.Append("**Range rule:** the probe only writes values already known to exist for a var: its scan value, values seen in valid probe results, " +
                  "or a range the owner confirmed (ND MODE 0-4, ND ZOOM 0-5, breakers 0/1, takeover 0/1). Counters (encoders, MASTER CAUTION/WARNING) only get +1/-1 steps. " +
                  "The position test (stepping 0..7) is switched off: a switch with an unknown position count gets only the values seen so far; " +
                  "its positions are to be learned from real pilot input (CRO-24).\n\n");
        if (!check.IsValid) sb.Append("```\n").Append(string.Join("\n", check.FormatIssues())).Append("\n```\n\n");

        sb.Append("| phase | test | result | count |\n|---|---|---|---|\n");
        foreach (var g in results.GroupBy(r => (r.Phase, r.Test, r.Result)).OrderBy(g => g.Key.Phase).ThenBy(g => g.Key.Test))
            if (g.Key.Test is not "graphql-fuel")
                sb.Append($"| {g.Key.Phase} | {g.Key.Test} | {g.Key.Result} | {g.Count()} |\n");

        sb.Append("\n## Proposed changes\n\n");
        foreach (var g in changes.GroupBy(c => c.Kind))
        {
            sb.Append($"### {g.Key} ({g.Count()})\n\n| var | before | after | evidence |\n|---|---|---|---|\n");
            foreach (var c in g) sb.Append($"| `{c.Var}` | {Cell(c.Before)} | {Cell(c.After)} | {Cell(c.Why)} |\n");
            sb.Append('\n');
        }

        sb.Append("## Still unresolved\n\n");
        foreach (var g in unresolved.GroupBy(u => u.Phase))
        {
            sb.Append($"### {g.Key} ({g.Count()})\n\n| var | why |\n|---|---|\n");
            foreach (var u in g) sb.Append($"| `{u.Var}` | {Cell(u.Why)} |\n");
            sb.Append('\n');
        }
        var skippedRows = results.Where(r => r.Result == "skipped").GroupBy(r => r.Notes).ToList();
        if (skippedRows.Count > 0)
        {
            sb.Append("### Not probed (skipped)\n\n| reason | vars |\n|---|---|\n");
            foreach (var g in skippedRows) sb.Append($"| {Cell(g.Key)} | {string.Join(" ", g.Select(r => $"`{r.Var}`").Distinct())} |\n");
            sb.Append('\n');
        }

        sb.Append("## Supervised checks and lookups\n\n");
        foreach (var r in results.Where(r => r.Phase is Phase.Takeover or Phase.Hold))
            sb.Append($"- `{r.Var}` {r.Test}: {r.Result}. {r.Notes}\n");
        foreach (var r in results.Where(r => r.Phase == Phase.Readonly && r.Test is "simvar" or "graphql" or "graphql-fuel" or "manual" && !r.Var.StartsWith("surprise", StringComparison.Ordinal) && r.Var is not ("crash-cutoff" or "crash-end")))
            sb.Append($"- `{r.Var}` ({r.Test}): {r.Notes}\n");
        sb.Append('\n');

        sb.Append("## Surprises\n\n");
        var surprises = new List<string>();
        foreach (var r in results.Where(r => r.Test == "momentary-or-latching" && r.Notes.Contains("Fenix reset it to")))
            surprises.Add($"`{r.Var}`: Fenix resets it itself ({r.Notes})");
        foreach (var r in results.Where(r => r.Notes.Contains("GraphQL uses other numbers")))
            surprises.Add($"`{r.Var}`: {r.Notes}");
        foreach (var r in results.Where(r => r.Test == "lamp-half" && r.Result is "upper" or "both"))
            surprises.Add(r.Result == "upper" ? $"`{r.Var}`: lower-half rule wrong, only the upper lamp changed ({r.Notes})" : $"`{r.Var}`: both lamp halves changed ({r.Notes}); the lower half was kept"); 
        foreach (var r in results.Where(r => r.Test == "verified-binary" && r.Result == "rejected"))
            surprises.Add($"`{r.Var}`: binary-looking switch has a third state ({r.Notes})");
        foreach (var r in results.Where(r => r.Result == "breaker-counter"))
            surprises.Add($"`{r.Var}`: breaker behaves like a counter");
        foreach (var r in results.Where(r => r.Result is "error" or "cancelled" or "unexpected"))
            surprises.Add($"`{r.Var}` ({r.Test}): {r.Result}, {r.Notes}");
        foreach (var r in results.Where(r => r.Test == "manual" && r.Var.StartsWith("surprise", StringComparison.Ordinal)))
            surprises.Add(r.Notes);
        sb.Append(surprises.Count == 0 ? "None found automatically.\n" : string.Join("", surprises.Select(s => "- " + s + "\n")));
        return sb.ToString();
    }

    static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
