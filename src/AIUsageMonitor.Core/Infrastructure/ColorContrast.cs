using System.Globalization;

namespace AIUsageMonitor.Core.Infrastructure;

/// <summary>WCAG 2.x relative luminance and contrast ratio for opaque sRGB colours.</summary>
public static class ColorContrast
{
    public static double Ratio(string hexA, string hexB)
    {
        var la = Luminance(hexA);
        var lb = Luminance(hexB);
        var (light, dark) = la >= lb ? (la, lb) : (lb, la);
        return (light + 0.05) / (dark + 0.05);
    }

    public static double Luminance(string hex)
    {
        var h = Rgb(hex);
        double C(int i) { var c = int.Parse(h.Substring(i, 2), NumberStyles.HexNumber) / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(0) + 0.7152 * C(2) + 0.0722 * C(4);
    }

    /// <summary>
    /// Alpha-composites <paramref name="hex"/> (<c>#AARRGGBB</c>) over the opaque <paramref name="backdrop"/> and
    /// returns the resulting opaque <c>#RRGGBB</c>. WCAG ratios are defined for opaque colours only, so a
    /// translucent surface (the settings cards are <c>#14FFFFFF</c> over the window) has to be flattened first.
    /// </summary>
    public static string Over(string hex, string backdrop)
    {
        var h = hex.TrimStart('#');
        var alpha = h.Length == 8 ? int.Parse(h.Substring(0, 2), NumberStyles.HexNumber) / 255.0 : 1.0;
        var (fr, fg, fb) = Channels(hex);
        var (br, bg, bb) = Channels(backdrop);
        int Mix(int f, int b) => (int)Math.Round(f * alpha + b * (1 - alpha));
        return $"#{Mix(fr, br):X2}{Mix(fg, bg):X2}{Mix(fb, bb):X2}";
    }

    private static (int R, int G, int B) Channels(string hex)
    {
        var h = Rgb(hex);
        return (int.Parse(h.Substring(0, 2), NumberStyles.HexNumber),
                int.Parse(h.Substring(2, 2), NumberStyles.HexNumber),
                int.Parse(h.Substring(4, 2), NumberStyles.HexNumber));
    }

    private static string Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];
        if (h.Length != 6) throw new ArgumentException($"Expected #RRGGBB or #AARRGGBB, got '{hex}'", nameof(hex));
        return h;
    }
}
