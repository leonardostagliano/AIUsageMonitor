using System.Globalization;

namespace AIUsageMonitor.Core.Settings;

/// <summary>
/// The fallback rate (1 € = x $) as typed in the settings window. Either decimal separator is accepted — "1,14" as
/// Italian writes it, "1.14" as the ECB file and most rate sources do — and nothing else: no thousands separator (in
/// it-IT '.' is one, which turned "1.14" into 114), no sign, no exponent. A value outside
/// <see cref="AppSettings.MinUsdPerEur"/>–<see cref="AppSettings.MaxUsdPerEur"/> is refused rather than clamped, so
/// the window can say so instead of saving a rate nobody typed.
/// </summary>
public static class FallbackRateInput
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>The rate as the settings window shows it: "1,14", "1,1411".</summary>
    public static string Format(double usdPerEur) => usdPerEur.ToString("0.####", Italian);

    /// <summary>The typed rate, or null with an Italian <paramref name="error"/> for the window when it is not one.</summary>
    public static double? Parse(string? text, out string? error)
    {
        var trimmed = (text ?? "").Trim();
        // At most one separator: a second one could only be a thousands separator, which no rate in range needs.
        if (trimmed.Count(c => c is ',' or '.') > 1
            || !double.TryParse(trimmed.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate))
        {
            error = "Scrivi il tasso come numero, per esempio 1,14.";
            return null;
        }

        if (rate < AppSettings.MinUsdPerEur || rate > AppSettings.MaxUsdPerEur)
        {
            error = string.Create(Italian, $"Il tasso deve essere tra {AppSettings.MinUsdPerEur:0.0} e {AppSettings.MaxUsdPerEur:0.0}.");
            return null;
        }

        error = null;
        return rate;
    }
}
