using System.Globalization;
using AIUsageMonitor.Core.Pricing;

namespace AIUsageMonitor.Core.Infrastructure;

/// <summary>Italian formatting of a cost in euro, for the notch rows and their tooltips.</summary>
public static class CostFormatter
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>
    /// "3,21 €" under 100 €, "1.234 €" from 100 €, "&lt; 0,01 €" under one cent. The 100 € threshold applies to the
    /// amount rounded to the cent, so 99,996 € reads "100 €" rather than "100,00 €".
    /// </summary>
    public static string Amount(decimal eur)
    {
        if (eur < 0.01m) return "< 0,01 €";
        return Math.Round(eur, 2, MidpointRounding.AwayFromZero) >= 100m
            ? string.Create(Italian, $"{eur:N0} €")
            : string.Create(Italian, $"{eur:N2} €");
    }

    /// <summary>
    /// "≈ 3,21 €"; "≥ 3,21 €" when some model has no price (the amount is a minimum); "costo n/d" when none has;
    /// null when there is no usage to price.
    /// </summary>
    public static string? Short(CostResult cost)
    {
        if (!cost.HasUsage) return null;
        if (!cost.AnyPriced) return "costo n/d";
        if (cost.Eur < 0.01m) return Amount(cost.Eur);
        return (cost.AnyUnpriced ? "≥ " : "≈ ") + Amount(cost.Eur);
    }

    /// <summary>
    /// One line per model, most expensive first: "claude-fable-5-1 ≈ 2,80 €", "claude-haiku-x &lt; 0,01 €" (no prefix
    /// under one cent) or "codex-auto-review: prezzo non disponibile".
    /// </summary>
    public static IReadOnlyList<string> ModelLines(CostResult cost) =>
        cost.ByModel.Select(m =>
            !m.Priced ? $"{m.Model}: prezzo non disponibile"
            : m.Eur < 0.01m ? $"{m.Model} {Amount(m.Eur)}"
            : $"{m.Model} ≈ {Amount(m.Eur)}").ToList();
}
