using Crosscheck.Profiles.Model;

namespace Crosscheck.Profiles.Tests;

public class ProfileLoaderTests
{
    // A small valid profile. Tests change one thing in it and look at the line number of the error.
    private static readonly string Valid = ValidRaw.Replace("\r\n", "\n");

    private const string ValidRaw = """
        format: 1
        aircraft:
          id: test-aircraft
          name: Test Aircraft
          match:
            packages: [test-package]
          builds: ["1.0.0"]
        groups: [overhead, fcu, cdu1, systems]
        exclude: ["FSDT_*", "p42_*"]
        vars:
          - name: S_SWITCH
            class: verified
            group: overhead
            values: [0, 1]
            verify: { lvar: I_LAMP, settle_ms: 150 }
          - name: I_LAMP
            class: observe
            group: overhead
          - name: E_ENCODER
            class: delta
            group: fcu
            display: N_DISPLAY
          - name: N_DISPLAY
            class: observe
            group: fcu
          - name: S_KEY
            class: pulse
            group: cdu1
            pulse_ms: 150
            verify: { mcdu: 1, settle_ms: 600 }
          - name: A_KNOB
            class: analog
            group: overhead
            range: [0, 1]
            verify: { graphql: system.analog.A_KNOB, scale: 1024, settle_ms: 100 }
        datarefs:
          - path: aircraft.fuel.left.amount.kg
            class: owned
            group: systems
            poll_hz: 1
        """;

    private static ProfileLoadResult Load(string yaml) => ProfileLoader.LoadText(yaml, "test.yaml");

    private static int LineOf(string yaml, string needle)
    {
        var lines = yaml.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(needle)) return i + 1;
        throw new InvalidOperationException($"'{needle}' not in test yaml");
    }

    private static ValidationIssue Single(ProfileLoadResult result)
    {
        Assert.False(result.IsValid);
        return Assert.Single(result.Issues);
    }

    // --- valid ---

    [Fact]
    public void Valid_profile_loads()
    {
        var result = Load(Valid);

        Assert.True(result.IsValid, string.Join("\n", result.FormatIssues()));
        var p = result.Profile!;
        Assert.Equal("test-aircraft", p.Aircraft.Id);
        Assert.Equal(6, p.Vars.Count);
        Assert.Equal(SyncClass.Delta, p.Vars[2].Class);
        Assert.Equal("N_DISPLAY", p.Vars[2].Display);
        Assert.Equal(150, p.Vars[4].PulseMs);
        Assert.Equal(1, p.Vars[4].Verify!.Mcdu);
        Assert.Equal(1024, p.Vars[5].Verify!.Scale);
        Assert.Equal(VerifyExpect.Same, p.Vars[0].Verify!.Expect);
        Assert.Equal(SyncClass.Owned, p.Datarefs![0].Class);
    }

    [Fact]
    public void Summary_counts_per_class_and_group()
    {
        var summary = ProfileSummary.Of(Load(Valid).Profile!);

        Assert.Equal(6, summary.VarCount);
        Assert.Equal(1, summary.DatarefCount);
        Assert.Equal(2, summary.PerClass[SyncClass.Observe]);
        Assert.Equal(1, summary.PerClass[SyncClass.Owned]);
        Assert.Equal(0, summary.PerClass[SyncClass.Ignore]);
        Assert.Equal(3, summary.PerGroup["overhead"]);
        Assert.Equal(1, summary.PerGroup["systems"]);
    }

    [Fact]
    public void Real_fenix_profile_is_valid()
    {
        var dir = AppContext.BaseDirectory;
        string? file = null;
        while (dir is not null && file is null)
        {
            var candidate = Path.Combine(dir, "profiles", "fenix-a320", "profile.yaml");
            if (File.Exists(candidate)) file = candidate;
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(file);
        var result = ProfileLoader.LoadFile(file!);
        Assert.True(result.IsValid, string.Join("\n", result.FormatIssues()));
    }

    // --- format version and YAML problems ---

    [Fact]
    public void Unknown_format_version_is_refused_before_anything_else()
    {
        var yaml = Valid.Replace("format: 1", "format: 2").Replace("name: Test Aircraft", "nonsense: true");
        var issue = Single(Load(yaml));

        Assert.Equal(1, issue.Line);
        Assert.Contains("format 2 is not supported", issue.Message);
        Assert.Contains("newer", issue.Message);
    }

    [Fact]
    public void Broken_yaml_reports_a_line()
    {
        var yaml = Valid.Replace("name: S_KEY", "name: \"S_KEY");
        var issue = Single(Load(yaml));

        Assert.StartsWith("YAML error", issue.Message);
        Assert.True(issue.Line >= LineOf(yaml, "name: \"S_KEY"));
    }

    [Fact]
    public void Broken_yaml_without_a_position_is_still_a_clear_error()
    {
        var yaml = Valid.Replace("class: delta", "class: [delta");
        var issue = Single(Load(yaml));

        Assert.StartsWith("YAML error", issue.Message);
    }

    [Fact]
    public void Empty_file_is_refused()
    {
        var issue = Single(Load(""));
        Assert.Equal(1, issue.Line);
    }

    // --- schema errors ---

    [Fact]
    public void Unknown_key_is_reported_on_its_line()
    {
        var yaml = Valid.Replace("pulse_ms: 150", "pulse_ms: 150\n    pulse_lenght: 5");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "pulse_lenght"), issue.Line);
        Assert.Contains("unknown key 'pulse_lenght'", issue.Message);
        Assert.Contains("S_KEY", issue.Path);
    }

    [Fact]
    public void Bad_class_is_a_schema_error_on_the_class_line()
    {
        var yaml = Valid.Replace("class: analog", "class: knob");
        var issue = Assert.Single(Load(yaml).Issues);

        Assert.Equal(LineOf(yaml, "class: knob"), issue.Line);
        Assert.Contains("A_KNOB", issue.Path);
    }

    [Fact]
    public void Missing_required_field_is_reported_on_the_entry()
    {
        var yaml = Valid.Replace("  - name: I_LAMP\n    class: observe\n    group: overhead", "  - name: I_LAMP\n    class: observe");
        var result = Load(yaml);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(LineOf(yaml, "name: I_LAMP"), issue.Line);
        Assert.Contains("group", issue.Message);
    }

    [Fact]
    public void Wrong_type_is_reported()
    {
        var yaml = Valid.Replace("pulse_ms: 150", "pulse_ms: fast");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "pulse_ms: fast"), issue.Line);
    }

    [Fact]
    public void Quoted_numbers_are_strings_and_rejected_where_a_number_is_needed()
    {
        var yaml = Valid.Replace("range: [0, 1]", "range: [\"0\", 1]");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "range:"), issue.Line);
    }

    // --- semantic errors ---

    [Fact]
    public void Duplicate_var_name()
    {
        var yaml = Valid.Replace("name: N_DISPLAY", "name: I_LAMP");
        var result = Load(yaml);

        var dup = Assert.Single(result.Issues, i => i.Message.StartsWith("duplicate var name"));
        Assert.True(dup.Line > LineOf(yaml, "name: I_LAMP"));
        Assert.Contains($"line {LineOf(yaml, "name: I_LAMP")}", dup.Message);
    }

    [Fact]
    public void Duplicate_dataref_path()
    {
        var yaml = Valid + "\n  - path: aircraft.fuel.left.amount.kg\n    class: observe\n    group: systems\n    poll_hz: 1";
        var issue = Single(Load(yaml));

        Assert.Contains("duplicate dataref path", issue.Message);
        Assert.Equal(yaml.Split('\n').Length - 3, issue.Line);
    }

    [Fact]
    public void Unknown_group_on_a_var()
    {
        var yaml = Valid.Replace("group: cdu1", "group: cdu9");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "group: cdu9"), issue.Line);
        Assert.Contains("unknown group 'cdu9'", issue.Message);
    }

    [Fact]
    public void Unknown_group_on_a_dataref()
    {
        var yaml = Valid.Replace("group: systems\n    poll_hz", "group: nowhere\n    poll_hz");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "group: nowhere"), issue.Line);
    }

    [Fact]
    public void Verify_lvar_that_is_not_in_the_profile()
    {
        var yaml = Valid.Replace("lvar: I_LAMP", "lvar: I_MISSING");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "lvar: I_MISSING"), issue.Line);
        Assert.Contains("'I_MISSING' is not defined", issue.Message);
    }

    [Fact]
    public void Verify_graphql_system_path_must_mirror_a_profile_var()
    {
        var yaml = Valid.Replace("system.analog.A_KNOB", "system.analog.A_OTHER");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "A_OTHER"), issue.Line);
        Assert.Contains("A_OTHER", issue.Message);
    }

    [Fact]
    public void Verify_graphql_other_path_must_be_a_listed_dataref()
    {
        var yaml = Valid.Replace("system.analog.A_KNOB", "aircraft.fuel.right.amount.kg");
        var issue = Single(Load(yaml));

        Assert.Contains("not listed in 'datarefs'", issue.Message);
    }

    [Fact]
    public void Verify_graphql_can_use_a_listed_dataref()
    {
        var yaml = Valid.Replace("system.analog.A_KNOB", "aircraft.fuel.left.amount.kg");
        Assert.True(Load(yaml).IsValid);
    }

    // --- roles ---

    private static readonly string WithTakeover = Valid.Replace("  - name: I_LAMP\n",
        "  - name: S_TAKEOVER_L\n    class: verified\n    group: overhead\n    values: [0, 1]\n" +
        "  - name: S_TAKEOVER_R\n    class: pulse\n    group: overhead\n    pulse_ms: 100\n  - name: I_LAMP\n")
        + "\nroles:\n  takeover: { captain: S_TAKEOVER_L, fo: S_TAKEOVER_R }\n";

    [Fact]
    public void Takeover_role_loads()
    {
        var result = Load(WithTakeover);
        Assert.True(result.IsValid, string.Join("\n", result.FormatIssues()));
        Assert.Equal(new TakeoverRole("S_TAKEOVER_L", "S_TAKEOVER_R"), result.Profile!.Roles!.Takeover);
    }

    [Fact]
    public void Roles_are_optional()
    {
        Assert.Null(Load(Valid).Profile!.Roles);
    }

    [Fact]
    public void Takeover_var_must_be_in_the_profile()
    {
        var yaml = WithTakeover.Replace("fo: S_TAKEOVER_R", "fo: S_NOWHERE");
        var issue = Single(Load(yaml));
        Assert.Contains("'S_NOWHERE' is not defined", issue.Message);
        Assert.Equal(LineOf(yaml, "takeover:"), issue.Line);
    }

    [Fact]
    public void Takeover_var_must_be_a_pilot_input()
    {
        var issue = Single(Load(WithTakeover.Replace("captain: S_TAKEOVER_L", "captain: I_LAMP")));
        Assert.Contains("must be a pilot input", issue.Message);
    }

    [Fact]
    public void Takeover_needs_both_seats_and_different_vars()
    {
        Assert.Contains("fo", Single(Load(WithTakeover.Replace(", fo: S_TAKEOVER_R", ""))).Message);
        Assert.Contains("different", Single(Load(WithTakeover.Replace("fo: S_TAKEOVER_R", "fo: S_TAKEOVER_L"))).Message);
    }

    [Fact]
    public void Unknown_role_is_a_schema_error()
    {
        Assert.False(Load(WithTakeover.Replace("  takeover:", "  takeoff:")).IsValid);
    }

    [Fact]
    public void Verify_cannot_point_at_the_var_itself()
    {
        var yaml = Valid.Replace("lvar: I_LAMP", "lvar: S_SWITCH");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "lvar: S_SWITCH"), issue.Line);
    }

    [Fact]
    public void Verify_needs_exactly_one_source()
    {
        var none = Valid.Replace("verify: { mcdu: 1, settle_ms: 600 }", "verify: { settle_ms: 600 }");
        var two = Valid.Replace("verify: { mcdu: 1, settle_ms: 600 }", "verify: { mcdu: 1, lvar: I_LAMP, settle_ms: 600 }");

        Assert.Contains("exactly one source", Single(Load(none)).Message);
        Assert.Equal(LineOf(none, "settle_ms: 600"), Single(Load(none)).Line);
        Assert.Contains("more than one source", Single(Load(two)).Message);
    }

    [Fact]
    public void Verify_mcdu_must_be_1_or_2()
    {
        var yaml = Valid.Replace("mcdu: 1", "mcdu: 3");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "mcdu: 3"), issue.Line);
    }

    [Fact]
    public void Contains_is_only_for_mcdu()
    {
        var yaml = Valid.Replace("lvar: I_LAMP, settle_ms: 150", "lvar: I_LAMP, contains: X, settle_ms: 150");
        Assert.Contains("only used with 'mcdu'", Single(Load(yaml)).Message);
    }

    [Fact]
    public void Mcdu_contains_is_allowed_for_local_tests()
    {
        var yaml = Valid.Replace("mcdu: 1, settle_ms: 600", "mcdu: 1, contains: \"F-PLN\", settle_ms: 600");
        Assert.True(Load(yaml).IsValid);
    }

    [Fact]
    public void Pulse_without_length()
    {
        var yaml = Valid.Replace("    pulse_ms: 150\n", "");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "name: S_KEY"), issue.Line);
        Assert.Contains("pulse needs 'pulse_ms'", issue.Message);
    }

    [Fact]
    public void Delta_without_display_var()
    {
        var yaml = Valid.Replace("    display: N_DISPLAY\n", "");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "name: E_ENCODER"), issue.Line);
        Assert.Contains("delta needs 'display'", issue.Message);
    }

    [Fact]
    public void Delta_display_var_that_does_not_exist()
    {
        var yaml = Valid.Replace("display: N_DISPLAY", "display: N_NOPE");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "display: N_NOPE"), issue.Line);
        Assert.Contains("'N_NOPE' is not defined", issue.Message);
    }

    [Fact]
    public void Delta_display_var_must_be_observe()
    {
        var yaml = Valid.Replace("display: N_DISPLAY", "display: S_SWITCH");
        Assert.Contains("must have class observe", Single(Load(yaml)).Message);
    }

    [Fact]
    public void Verified_needs_values_or_range()
    {
        var yaml = Valid.Replace("    values: [0, 1]\n", "");
        Assert.Contains("verified needs 'values' or 'range'", Single(Load(yaml)).Message);
    }

    [Fact]
    public void Analog_needs_range()
    {
        var yaml = Valid.Replace("    range: [0, 1]\n", "");
        Assert.Contains("analog needs 'range'", Single(Load(yaml)).Message);
    }

    [Fact]
    public void Values_and_range_together()
    {
        var yaml = Valid.Replace("values: [0, 1]", "values: [0, 1]\n    range: [0, 1]");
        Assert.Contains("not both", Single(Load(yaml)).Message);
    }

    [Fact]
    public void Range_must_have_min_below_max()
    {
        var yaml = Valid.Replace("range: [0, 1]", "range: [1, 0]");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "range: [1, 0]"), issue.Line);
    }

    [Fact]
    public void Observe_takes_no_write_fields()
    {
        var yaml = Valid.Replace("  - name: I_LAMP\n    class: observe", "  - name: I_LAMP\n    class: observe\n    pulse_ms: 150");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "pulse_ms: 150"), issue.Line);
        Assert.Contains("'pulse_ms' is not used by class observe", issue.Message);
    }

    [Fact]
    public void Var_that_matches_an_exclude_pattern()
    {
        var yaml = Valid.Replace("name: I_LAMP", "name: FSDT_Something").Replace("lvar: I_LAMP", "lvar: FSDT_Something");
        var issue = Single(Load(yaml));

        Assert.Equal(LineOf(yaml, "name: FSDT_Something"), issue.Line);
        Assert.Contains("exclude pattern 'FSDT_*'", issue.Message);
    }

    [Fact]
    public void Owned_dataref_needs_poll_rate()
    {
        var yaml = Valid.Replace("\n    poll_hz: 1", "");
        Assert.Contains("needs 'poll_hz'", Single(Load(yaml)).Message);
    }

    [Fact]
    public void Several_problems_are_all_reported_in_line_order()
    {
        var yaml = Valid.Replace("group: cdu1", "group: cdu9").Replace("lvar: I_LAMP", "lvar: I_MISSING");
        var result = Load(yaml);

        Assert.Equal(2, result.Issues.Count);
        Assert.True(result.Issues[0].Line < result.Issues[1].Line);
        Assert.StartsWith("test.yaml:", result.FormatIssues().First());
    }

    // --- exclude filter ---

    [Theory]
    [InlineData("FSDT_Deice_Available", true)]
    [InlineData("p42_cp_a_x", true)]
    [InlineData("S_OH_ELEC_APU_MASTER", false)]
    [InlineData("fsdt_lower", false)]
    [InlineData("xFSDT_", false)]
    public void Exclude_filter_matches_globs(string name, bool excluded)
    {
        var filter = new ExcludeFilter(["FSDT_*", "p42_*"]);
        Assert.Equal(excluded, filter.IsExcluded(name));
    }

    [Fact]
    public void Exclude_filter_question_mark_matches_one_character()
    {
        var filter = new ExcludeFilter(["A?_TEST"]);
        Assert.True(filter.IsExcluded("A1_TEST"));
        Assert.False(filter.IsExcluded("A12_TEST"));
    }

    [Fact]
    public void Exclude_filter_treats_regex_characters_literally()
    {
        var filter = new ExcludeFilter(["A320.audio.*"]);
        Assert.True(filter.IsExcluded("A320.audio.1.tone"));
        Assert.False(filter.IsExcluded("A320Xaudio.1.tone"));
    }
}
