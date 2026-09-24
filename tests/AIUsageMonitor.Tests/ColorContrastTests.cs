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
    [InlineData("#F5F5F7", "#121216")]   // text on background
    [InlineData("#F5F5F7", "#1B1B21")]   // text on cards
    [InlineData("#F5F5F7", "#26262E")]   // button text on surface
    [InlineData("#F5F5F7", "#30303A")]   // button text on hover
    [InlineData("#F5F5F7", "#3A3A45")]   // button text on pressed
    [InlineData("#A1A1AA", "#121216")]   // muted text
    [InlineData("#A1A1AA", "#1B1B21")]   // muted text on cards
    [InlineData("#121216", "#F5F5F7")]   // primary button text on accent
    [InlineData("#FCD34D", "#1B1B21")]   // warning text
    [InlineData("#86EFAC", "#1B1B21")]   // working text
    [InlineData("#FCA5A5", "#1B1B21")]   // error text
    public void Palette_pairs_meet_aa(string fg, string bg) =>
        Assert.True(ColorContrast.Ratio(fg, bg) >= 4.5, $"{fg} on {bg} = {ColorContrast.Ratio(fg, bg):0.00}");

    // I riquadri delle sessioni sono Tile (#0AFFFFFF) sopra la card: il testo si misura sul composito.
    [Theory]
    [InlineData("#F5F5F7")]
    [InlineData("#A1A1AA")]
    [InlineData("#86EFAC")]
    [InlineData("#FCD34D")]
    [InlineData("#FCA5A5")]
    public void Text_on_a_session_tile_meets_aa(string fg)
    {
        var tile = ColorContrast.Over("#0AFFFFFF", "#1B1B21");
        Assert.True(ColorContrast.Ratio(fg, tile) >= 4.5, $"{fg} on {tile} = {ColorContrast.Ratio(fg, tile):0.00}");
    }

    // TextDisabled on Surface.
    [Fact]
    public void Disabled_text_meets_3_to_1() =>
        Assert.True(ColorContrast.Ratio("#71717A", "#26262E") >= 3.0);

    // WCAG 1.4.11: l'interruttore si riconosce dal pomello bianco (spento) e dalla traccia verde (acceso).
    [Theory]
    [InlineData("#FFFFFF")]   // pomello
    [InlineData("#22C55E")]   // Toggle: traccia accesa
    public void Switch_parts_meet_the_non_text_3_to_1(string part) =>
        Assert.True(ColorContrast.Ratio(part, "#1B1B21") >= 3.0, $"{part} on the card = {ColorContrast.Ratio(part, "#1B1B21"):0.00}");

    // il bordo delle card non basta da solo a identificare un controllo.
    [Fact]
    public void The_notch_border_would_not_meet_it() =>
        Assert.True(ColorContrast.Ratio(ColorContrast.Over("#12FFFFFF", "#1B1B21"), "#1B1B21") < 3.0);

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
