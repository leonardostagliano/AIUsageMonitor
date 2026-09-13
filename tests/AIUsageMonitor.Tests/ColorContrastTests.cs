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

    // WCAG 1.4.11: i bordi che identificano un controllo vogliono 3:1, non 4.5:1, ma un floor c'e'.
    // La casella non spuntata vive su una card #14FFFFFF sopra #1B1B1F, cioe' #2D2D31.
    [Theory]
    [InlineData("#8B8B93")]   // Grey: contorno della CheckBox a riposo
    [InlineData("#A0A0A8")]   // TextMuted: contorno in hover
    public void Checkbox_outline_meets_the_non_text_3_to_1(string outline) =>
        Assert.True(ColorContrast.Ratio(outline, "#2D2D31") >= 3.0,
            $"{outline} on the card = {ColorContrast.Ratio(outline, "#2D2D31"):0.00}");

    // Il colore che il bordo NON puo' avere: e' quello che rendeva invisibile la casella non spuntata.
    [Fact]
    public void The_notch_border_would_not_meet_it() =>
        Assert.True(ColorContrast.Ratio(ColorContrast.Over("#1FFFFFFF", "#2D2D31"), "#2D2D31") < 3.0);

    [Theory]
    [InlineData("#14FFFFFF", "#1B1B1F", "#2D2D31")]   // card delle impostazioni
    [InlineData("#FF123456", "#000000", "#123456")]   // opaco: resta se stesso
    [InlineData("#00FFFFFF", "#1B1B1F", "#1B1B1F")]   // trasparente: resta lo sfondo
    [InlineData("#123456", "#FFFFFF", "#123456")]     // senza alpha: opaco
    public void Over_composites_translucent_colours(string hex, string backdrop, string expected) =>
        Assert.Equal(expected, ColorContrast.Over(hex, backdrop), ignoreCase: true);

    [Fact]
    public void Alpha_prefix_is_ignored() =>
        Assert.Equal(ColorContrast.Ratio("#FFFFFFFF", "#FF1B1B1F"), ColorContrast.Ratio("#FFFFFF", "#1B1B1F"));
}
