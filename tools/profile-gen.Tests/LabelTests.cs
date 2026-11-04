using ProfileGen;

namespace ProfileGen.Tests;

/// <summary>The readable label rule of tools/profile-gen (CRO-39).</summary>
public class LabelTests
{
    [Theory]
    // Panel prefixes go, normal case, positions in one parenthesis.
    [InlineData("S_FC_RUDDER_TRIM_RESET", "Rudder trim reset")]
    [InlineData("S_EXT_CARGO_BAY_AFT_LT", "Cargo bay light (aft)")]
    [InlineData("S_EXT_CARGO_BAY_FWD_LT", "Cargo bay light (forward)")]
    [InlineData("S_OH_EXT_LT_BEACON", "Beacon light")]
    // A lamp's L / U is the lower / upper legend of the button.
    [InlineData("I_OH_ELEC_IDG1_L", "IDG 1 light (lower)")]
    [InlineData("I_OH_ELEC_EXT_PWR_U", "External power light (upper)")]
    [InlineData("I_MIP_MASTER_CAUTION_CAPT_L", "Master caution light (Capt, lower)")]
    // Anywhere else L / R mean left / right.
    [InlineData("S_OH_EXT_LT_LANDING_L", "Landing light (left)")]
    [InlineData("A_MIP_LIGHTING_MAP_R", "Lighting map (right)")]
    [InlineData("N_OH_ADIRS_LEFT", "ADIRS display (left)")]
    // Except after ECAM: the lower / upper ECAM display.
    [InlineData("A_DISPLAY_BRIGHTNESS_ECAM_L", "Display brightness ECAM (lower)")]
    // Sides.
    [InlineData("S_FCU_EFIS1_LS", "LS (Capt)")]
    [InlineData("S_MIP_MASTER_CAUTION_FO", "Master caution (F/O)")]
    [InlineData("S_CDU2_KEY_FPLN", "MCDU F-PLN (F/O)")]
    [InlineData("S_CDU1_KEY_L", "MCDU L (Capt)")]
    // Abbreviations stay, short forms become words.
    [InlineData("S_OH_PNEUMATIC_ENG1_BLEED", "Engine 1 bleed")]
    [InlineData("S_ECAM_HYD", "ECAM HYD")]
    [InlineData("A_WR_TILT", "Weather radar tilt")]
    [InlineData("I_OH_FLT_CTL_ELAC_1_U", "ELAC 1 light (upper)")]
    [InlineData("I_EXT_CARGO_DOOR_OPEN_FWD", "Cargo door open light (forward)")]
    public void Generated_labels_read_like_the_cockpit(string name, string label) =>
        Assert.Equal(label, Rules.LabelOf(name).Short);

    [Fact]
    public void L_is_left_or_lower_and_never_anything_else()
    {
        foreach (var name in new[] { "S_OH_EXT_LT_LANDING_L", "I_OH_ELEC_IDG1_L", "A_MIP_LIGHTING_MAP_L", "I_OH_CARGO_SMOKE_AFT_L" })
        {
            var label = Rules.LabelOf(name).Short;
            Assert.Matches(@"\((?:[^)]*, )?(left|lower)\)$", label);
            Assert.DoesNotContain("(aft)", label);
        }
    }

    [Fact]
    public void Other_aircraft_names_are_kept() => Assert.Equal("A320_Audio:Pitch", Rules.LabelOf("A320_Audio:Pitch").Short);

    [Fact]
    public void The_kind_word_goes_before_the_parenthesis() =>
        Assert.Equal("Landing light switch (left)", Labels.WithWord("Landing light (left)", "switch"));
}
