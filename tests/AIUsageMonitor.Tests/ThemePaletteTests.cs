using System.Xml.Linq;
using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Tests;

/// <summary>
/// Pins the palette asserted by <see cref="ColorContrastTests"/> to the brushes actually shipped in
/// <c>src/AIUsageMonitor.App/Assets/Theme.xaml</c>, so a divergence between the two fails the build
/// instead of passing silently.
/// </summary>
public class ThemePaletteTests
{
    /// <summary>Brush key to the colour the contrast tests assume. Keys not listed here are not pinned.</summary>
    private static readonly Dictionary<string, string> Pinned = new(StringComparer.Ordinal)
    {
        ["NotchBackground"] = "#F5121216",
        ["WindowBackground"] = "#FF121216",
        ["Card"] = "#FF1B1B21",
        ["Tile"] = "#0AFFFFFF",
        ["Surface"] = "#FF26262E",
        ["SurfaceHover"] = "#FF30303A",
        ["SurfacePressed"] = "#FF3A3A45",
        ["NotchBorder"] = "#12FFFFFF",
        ["Hairline"] = "#0FFFFFFF",
        ["TextPrimary"] = "#FFF5F5F7",
        ["TextMuted"] = "#FFA1A1AA",
        ["TextDisabled"] = "#FF71717A",
        ["Accent"] = "#FFF5F5F7",
        ["AccentText"] = "#FF121216",
        ["Toggle"] = "#FF22C55E",
        ["Focus"] = "#FF7AA2F7",
        ["BrandClaude"] = "#FFD97757",
        ["BrandCodex"] = "#FF10A37F",
        ["Success"] = "#FF4ADE80",
        ["SuccessText"] = "#FF86EFAC",
        ["Warning"] = "#FFFBBF24",
        ["WarningText"] = "#FFFCD34D",
        ["Danger"] = "#FFF87171",
        ["DangerText"] = "#FFFCA5A5",
        ["Idle"] = "#FF71717A",
        ["Green"] = "#FF4ADE80",
        ["Amber"] = "#FFFBBF24",
        ["Red"] = "#FFF87171",
        ["Grey"] = "#FF71717A",
        ["RingTrack"] = "#4D71717A",
    };

    /// <summary>Bar tone gradients (spec 4.1) as their stops from left to right.</summary>
    private static readonly Dictionary<string, string[]> PinnedGradients = new(StringComparer.Ordinal)
    {
        ["ToneNormalBrush"] = ["#FF22C55E", "#FF86EFAC"],
        ["ToneWarningBrush"] = ["#FFF59E0B", "#FFFCD34D"],
        ["ToneCriticalBrush"] = ["#FFEF4444", "#FFFCA5A5"],
        ["ToneStaleBrush"] = ["#FF68686F", "#FF71717A"],
    };

    [Fact]
    public void Theme_brushes_match_the_pinned_palette()
    {
        var theme = ThemeBrushes();
        foreach (var (key, expected) in Pinned)
        {
            if (!theme.TryGetValue(key, out var actual)) continue; // key not shipped yet
            Assert.True(
                string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase),
                $"Theme.xaml brush '{key}' is {actual} but the contrast tests pin it to {expected}. " +
                "Change both together or the shipped palette stops being the tested one.");
        }
    }

    [Fact]
    public void Theme_bar_gradients_match_the_pinned_stops()
    {
        var gradients = ThemeGradients();
        foreach (var (key, expected) in PinnedGradients)
        {
            Assert.True(gradients.TryGetValue(key, out var actual), $"Theme.xaml has no gradient '{key}'");
            Assert.Equal(expected.Select(c => c.ToUpperInvariant()), actual.Select(c => c.ToUpperInvariant()));
        }
    }

    // Spec 10: i toni delle barre su Card >= 3:1 (WCAG 1.4.11). Ogni stop, perche' una barra corta mostra quasi solo
    // l'inizio del gradiente e una piena arriva alla fine.
    [Theory]
    [InlineData("ToneNormalBrush")]
    [InlineData("ToneWarningBrush")]
    [InlineData("ToneCriticalBrush")]
    [InlineData("ToneStaleBrush")]
    public void Every_bar_tone_stop_meets_3_to_1_on_Card(string key)
    {
        var card = Resolve(ThemeBrushes(), "Card");
        Assert.True(ThemeGradients().TryGetValue(key, out var stops), $"Theme.xaml has no gradient '{key}'");
        Assert.NotEmpty(stops);
        foreach (var stop in stops)
        {
            var ratio = ColorContrast.Ratio(stop, card);
            Assert.True(ratio >= 3.0, $"{key} stop {stop} on Card ({card}) = {ratio:0.00}, below 3.0");
        }
    }

    [Theory]
    [InlineData("TextPrimary", "WindowBackground", 4.5)]
    [InlineData("TextPrimary", "NotchBackground", 4.5)]
    [InlineData("TextPrimary", "Card", 4.5)]
    [InlineData("TextPrimary", "Surface", 4.5)]
    [InlineData("TextPrimary", "SurfaceHover", 4.5)]
    [InlineData("TextPrimary", "SurfacePressed", 4.5)]
    [InlineData("TextMuted", "WindowBackground", 4.5)]
    [InlineData("TextMuted", "Card", 4.5)]
    [InlineData("TextMuted", "Surface", 4.5)]
    [InlineData("AccentText", "Accent", 4.5)]
    [InlineData("SuccessText", "Card", 4.5)]
    [InlineData("WarningText", "Card", 4.5)]
    [InlineData("DangerText", "Card", 4.5)]
    [InlineData("Amber", "WindowBackground", 4.5)]
    [InlineData("WarningText", "WindowBackground", 4.5)]
    [InlineData("TextDisabled", "Surface", 3.0)]
    // WCAG 1.4.11: colori che identificano uno stato o un controllo (anelli, pallini, barre, interruttore acceso).
    [InlineData("Success", "Card", 3.0)]
    [InlineData("Warning", "Card", 3.0)]
    [InlineData("Danger", "Card", 3.0)]
    [InlineData("Idle", "Card", 3.0)]
    [InlineData("Toggle", "Card", 3.0)]
    [InlineData("Grey", "Card", 3.0)]
    public void Theme_pairs_meet_their_floor(string foreground, string background, double floor)
    {
        var theme = ThemeBrushes();
        var fg = Resolve(theme, foreground);
        var bg = Resolve(theme, background);
        var ratio = ColorContrast.Ratio(fg, bg);
        Assert.True(ratio >= floor, $"{foreground} ({fg}) on {background} ({bg}) = {ratio:0.00}, below {floor:0.0}");
    }

    /// <summary>
    /// The shipped colour when the brush exists, otherwise the pinned one it must be added as. A translucent brush
    /// (Card) is flattened over WindowBackground first: a WCAG ratio is only defined between opaque colours, and that
    /// composite is what the eye actually sees behind the cards.
    /// </summary>
    private static string Resolve(IReadOnlyDictionary<string, string> theme, string key)
    {
        var colour =
            theme.TryGetValue(key, out var shipped) ? shipped
            : Pinned.TryGetValue(key, out var pinned) ? pinned
            : throw new InvalidOperationException($"'{key}' is neither in Theme.xaml nor pinned here");
        var opaque = colour.TrimStart('#').Length != 8 || colour.StartsWith("#FF", StringComparison.OrdinalIgnoreCase);
        return opaque ? colour : ColorContrast.Over(colour, Resolve(theme, "WindowBackground"));
    }

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static Dictionary<string, string> ThemeBrushes()
    {
        var brushes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var brush in LoadTheme().Descendants().Where(e => e.Name.LocalName == "SolidColorBrush"))
        {
            var key = (string?)brush.Attribute(Xaml + "Key");
            var color = (string?)brush.Attribute("Color");
            if (key is not null && color is not null) brushes[key] = color;
        }
        return brushes;
    }

    /// <summary>Every keyed LinearGradientBrush of Theme.xaml with the literal colours of its stops, in offset order.</summary>
    private static Dictionary<string, string[]> ThemeGradients()
    {
        var gradients = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var brush in LoadTheme().Descendants().Where(e => e.Name.LocalName == "LinearGradientBrush"))
        {
            if ((string?)brush.Attribute(Xaml + "Key") is not { } key) continue;
            gradients[key] = brush.Descendants()
                .Where(e => e.Name.LocalName == "GradientStop")
                .OrderBy(e => double.Parse((string?)e.Attribute("Offset") ?? "0", System.Globalization.CultureInfo.InvariantCulture))
                .Select(e => (string?)e.Attribute("Color") ?? "")
                .ToArray();
        }
        return gradients;
    }

    private static XDocument LoadTheme()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "AIUsageMonitor.App", "Assets", "Theme.xaml");
        Assert.True(File.Exists(path), $"Theme.xaml not found at {path}");
        return XDocument.Load(path);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIUsageMonitor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
