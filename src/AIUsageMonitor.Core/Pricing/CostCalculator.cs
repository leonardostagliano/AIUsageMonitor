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
        foreach (var model in a.ByModel.Concat(b.ByModel))
            merged[model.Model] = merged.TryGetValue(model.Model, out var existing)
                ? (existing.Eur + model.Eur, existing.Priced && model.Priced)
                : (model.Eur, model.Priced);
        return Build(merged);
    }

    internal static CostResult Build(Dictionary<string, (decimal Eur, bool Priced)> byModel)
    {
        var models = byModel
            .OrderByDescending(kv => kv.Value.Eur)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new ModelCost(kv.Key, kv.Value.Eur, kv.Value.Priced))
            .ToList();
        return new CostResult(models.Sum(m => m.Eur), models);
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
            var eur = price is null ? 0m : Usd(entry.Key, entry.Tokens, price) / usdPerEur;
            byModel[name] = byModel.TryGetValue(name, out var existing)
                ? (existing.Eur + eur, existing.Priced && price is not null)
                : (eur, price is not null);
        }
        return CostResult.Build(byModel);
    }
}
