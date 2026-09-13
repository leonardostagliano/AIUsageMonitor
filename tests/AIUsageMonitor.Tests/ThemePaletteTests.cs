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
        ["NotchBackground"] = "#EB1B1B1F",
        ["NotchBorder"] = "#1FFFFFFF",
        ["TextPrimary"] = "#FFFFFFFF",
        ["TextMuted"] = "#FFA0A0A8",
        ["Green"] = "#FF3FB950",
        ["Amber"] = "#FFD29922",
        // Added by Task 2; the disabled text is #7A7A82 (3.35:1 on Surface), not the plan's #6E6E76 (2.82:1).
        ["WindowBackground"] = "#FF1B1B1F",
        ["Surface"] = "#FF2A2A30",
        ["SurfaceHover"] = "#FF3A3A42",
        ["SurfacePressed"] = "#FF45454E",
        ["Accent"] = "#FF3FB950",
        ["AccentText"] = "#FF0B1A10",
        ["TextDisabled"] = "#FF7A7A82",
        ["Focus"] = "#FF7AA2F7",
        ["Grey"] = "#FF8B8B93",
        ["Card"] = "#14FFFFFF",
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

    [Theory]
    [InlineData("TextPrimary", "WindowBackground", 4.5)]
    [InlineData("TextPrimary", "NotchBackground", 4.5)]
    [InlineData("TextPrimary", "Surface", 4.5)]
    [InlineData("TextPrimary", "SurfaceHover", 4.5)]
    [InlineData("TextPrimary", "SurfacePressed", 4.5)]
    [InlineData("TextMuted", "WindowBackground", 4.5)]
    [InlineData("TextMuted", "Surface", 4.5)]
    [InlineData("AccentText", "Accent", 4.5)]
    [InlineData("Amber", "WindowBackground", 4.5)]
    [InlineData("TextDisabled", "Surface", 3.0)]
    [InlineData("TextPrimary", "Card", 4.5)]
    [InlineData("TextMuted", "Card", 4.5)]
    // WCAG 1.4.11: il contorno della CheckBox a riposo (Grey) e in hover (TextMuted) deve staccarsi dalla card.
    // Con NotchBorder, che il template usava prima, il rapporto era 1.48:1 e la casella non spuntata spariva.
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

    private static Dictionary<string, string> ThemeBrushes()
    {
        var path = Path.Combine(FindRepoRoot(), "src", "AIUsageMonitor.App", "Assets", "Theme.xaml");
        Assert.True(File.Exists(path), $"Theme.xaml not found at {path}");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var brushes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var brush in XDocument.Load(path).Descendants().Where(e => e.Name.LocalName == "SolidColorBrush"))
        {
            var key = (string?)brush.Attribute(x + "Key");
            var color = (string?)brush.Attribute("Color");
            if (key is not null && color is not null) brushes[key] = color;
        }
        return brushes;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIUsageMonitor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
