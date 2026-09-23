using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Pricing;

namespace AIUsageMonitor.App.Notch;

/// <summary>Testo dei tooltip di costo del notch: stesso formato per card, sessioni e agenti.</summary>
internal static class CostTooltips
{
    public const string Disclaimer = "Stima ai prezzi API di listino: non è la spesa dell'abbonamento.";

    /// <summary>Totale della card: avvertenza, costo per modello, fonte del listino e tasso.</summary>
    public static string Card(CostResult cost, PricingSnapshot pricing)
    {
        var lines = new List<string> { Disclaimer };
        lines.AddRange(CostFormatter.ModelLines(cost));
        lines.Add(pricing.CatalogLine());
        lines.Add(pricing.RateLine());
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Blocco da accodare al tooltip dei token di una riga: il costo dei token della riga per modello e, per una
    /// sessione con agenti, il totale compresi gli agenti. Null quando non c'e' nulla da prezzare.
    /// </summary>
    public static string? Row(CostResult own, CostResult? withSubagents, PricingSnapshot pricing)
    {
        if (CostFormatter.Short(own) is not { } text) return null;
        var lines = new List<string> { $"Costo API equivalente {text}" };
        lines.AddRange(CostFormatter.ModelLines(own));
        if (withSubagents is not null && CostFormatter.Short(withSubagents) is { } total) lines.Add($"Con agenti: {total}");
        lines.Add(Disclaimer);
        lines.Add(pricing.CatalogLine());
        lines.Add(pricing.RateLine());
        return string.Join("\n", lines);
    }
}
