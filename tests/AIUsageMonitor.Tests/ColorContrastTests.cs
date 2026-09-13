using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Tests;

public class ColorContrastTests
{
    [Theory]
    [InlineData("#FFFFFF", "#000000", 21.0)]
    [InlineData("#000000", "#FFFFFF", 21.0)]
    [InlineData("#FFFFFF", "#FFFFFF", 1.0)]
    public void Ratio_matches_wcag_reference(string a, string b, double expected) =>
        Assert.Equal(expected, ColorContrast.Ratio(a, b), 2);

    [Theory]
    [InlineData("#FFFFFF", "#1B1B1F")]   // text on background
    [InlineData("#FFFFFF", "#2A2A30")]   // button text on surface
    [InlineData("#FFFFFF", "#3A3A42")]   // button text on hover
    [InlineData("#FFFFFF", "#45454E")]   // button text on pressed
    [InlineData("#A0A0A8", "#1B1B1F")]   // muted text
    [InlineData("#A0A0A8", "#2A2A30")]   // muted text on cards
    [InlineData("#0B1A10", "#3FB950")]   // primary button text on accent
    [InlineData("#D29922", "#1B1B1F")]   // amber status text
    public void Palette_pairs_meet_aa(string fg, string bg) =>
        Assert.True(ColorContrast.Ratio(fg, bg) >= 4.5, $"{fg} on {bg} = {ColorContrast.Ratio(fg, bg):0.00}");

    // #6E6E76 (the first draft of this colour) measures 2.82:1 on #2A2A30 and fails the 3:1 floor.
    // ThemePaletteTests keeps Theme.xaml's TextDisabled brush equal to the value pinned here.
    [Fact]
    public void Disabled_text_meets_3_to_1() =>
        Assert.True(ColorContrast.Ratio("#7A7A82", "#2A2A30") >= 3.0);

    [Fact]
    public void Alpha_prefix_is_ignored() =>
        Assert.Equal(ColorContrast.Ratio("#FFFFFFFF", "#FF1B1B1F"), ColorContrast.Ratio("#FFFFFF", "#1B1B1F"));
}
