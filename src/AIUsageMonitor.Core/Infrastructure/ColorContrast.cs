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
        var h = hex.TrimStart('#');
        if (h.Length == 8) h = h[2..];
        if (h.Length != 6) throw new ArgumentException($"Expected #RRGGBB or #AARRGGBB, got '{hex}'", nameof(hex));
        double C(int i) { var c = int.Parse(h.Substring(i, 2), NumberStyles.HexNumber) / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * C(0) + 0.7152 * C(2) + 0.0722 * C(4);
    }
}
