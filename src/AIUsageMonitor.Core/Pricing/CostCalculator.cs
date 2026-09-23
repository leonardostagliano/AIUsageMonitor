using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Pricing;

/// <summary>Cost of one model within a result; <see cref="Priced"/> is false when the list has no price for it.</summary>
public sealed record ModelCost(string Model, decimal Eur, bool Priced);

/// <summary>Cost of a ledger in euro, with one line per model (most expensive first).</summary>
public sealed record CostResult(decimal Eur, IReadOnlyList<ModelCost> ByModel)
{
    public static readonly CostResult None = new(0m, []);

    /// <summary>False for an empty ledger: nothing to show at all.</summary>
    public bool HasUsage => ByModel.Count > 0;

    public bool AnyPriced => ByModel.Any(m => m.Priced);

    public bool AnyUnpriced => ByModel.Any(m => !m.Priced);

    public IEnumerable<string> UnpricedModels => ByModel.Where(m => !m.Priced).Select(m => m.Model);

    public static CostResult operator +(CostResult a, CostResult b)
    {
        if (!b.HasUsage) return a;
        if (!a.HasUsage) return b;
        var merged = new Dictionary<string, (decimal Eur, bool Priced)>(StringComparer.Ordinal);
        foreach (var model in a.ByModel.Concat(b.ByModel)) Add(merged, model.Model, model.Eur, model.Priced);
        return Build(merged);
    }

    /// <summary>
    /// Adds <paramref name="eur"/> to the line of <paramref name="model"/>. A sum a decimal cannot hold turns the line
    /// into an unpriced one: a cost must never throw into the view models that show it.
    /// </summary>
    internal static void Add(Dictionary<string, (decimal Eur, bool Priced)> byModel, string model, decimal eur, bool priced)
    {
        if (!byModel.TryGetValue(model, out var existing))
        {
            byModel[model] = (eur, priced);
            return;
        }
        try
        {
            byModel[model] = (existing.Eur + eur, existing.Priced && priced);
        }
        catch (OverflowException)
        {
            byModel[model] = (0m, false);
        }
    }

    internal static CostResult Build(Dictionary<string, (decimal Eur, bool Priced)> byModel)
    {
        var models = byModel
            .OrderByDescending(kv => kv.Value.Eur)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ModelCost(kv.Key, kv.Value.Eur, kv.Value.Priced))
            .ToList();
        var total = 0m;
        for (var i = 0; i < models.Count; i++)
        {
            try
            {
                total += models[i].Eur;
            }
            catch (OverflowException)
            {
                // Same rule as Add: left out of the total and shown as unpriced.
                models[i] = models[i] with { Eur = 0m, Priced = false };
            }
        }
        return new CostResult(total, models);
    }
}

/// <summary>Prices a <see cref="UsageLedger"/> with a <see cref="PriceCatalog"/>: a pure function, run at display time.</summary>
public static class CostCalculator
{
    /// <summary>Name shown for usage whose transcript did not record a model.</summary>
    public const string UnknownModel = "modello sconosciuto";

    /// <summary>
    /// USD of one ledger entry: tokens × the rates of its variant and band, times the <c>fast</c> multiplier (Fast
    /// tier only) and the geography multiplier (when the list has one for its geo), plus the web searches, which no
    /// multiplier touches.
    /// </summary>
    public static decimal Usd(UsageKey key, LedgerTokens tokens, ModelPrice price)
    {
        var rates = price.For(key.Tier).For(key.ContextBand);
        var tokenUsd = tokens.Input * rates.Input
                       + tokens.Output * rates.Output
                       + tokens.CacheRead * rates.CacheRead
                       + tokens.CacheWrite5m * rates.CacheWrite5m
                       + tokens.CacheWrite1h * rates.CacheWrite1h;
        var multiplier = 1m;
        if (key.Tier == PriceTier.Fast && price.Multipliers.TryGetValue("fast", out var fast)) multiplier *= fast;
        if (key.Geo is { } geo && price.Multipliers.TryGetValue(geo, out var geoMultiplier)) multiplier *= geoMultiplier;
        return tokenUsd * multiplier + tokens.WebSearches * price.WebSearch;
    }

    /// <summary>Euro cost of <paramref name="ledger"/>; <see cref="CostResult.None"/> for a null or empty ledger.</summary>
    public static CostResult Compute(UsageLedger? ledger, PriceCatalog catalog, decimal usdPerEur)
    {
        if (ledger is null || ledger.IsEmpty) return CostResult.None;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(usdPerEur);

        var byModel = new Dictionary<string, (decimal Eur, bool Priced)>(StringComparer.Ordinal);
        foreach (var entry in ledger.Entries)
        {
            var name = string.IsNullOrWhiteSpace(entry.Key.Model) ? UnknownModel : entry.Key.Model;
            var price = catalog.Find(entry.Key.Model);
            var eur = 0m;
            var priced = price is not null;
            if (price is not null)
            {
                try
                {
                    eur = Usd(entry.Key, entry.Tokens, price) / usdPerEur;
                }
                catch (OverflowException)
                {
                    // Rates no decimal can multiply out (a damaged or hostile price list): unpriced, never an
                    // exception in the notch refresh. LiteLlmPriceParser already refuses implausible rates.
                    priced = false;
                }
            }
            CostResult.Add(byModel, name, eur, priced);
        }
        return CostResult.Build(byModel);
    }
}
