using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Pricing;

/// <summary>USD per token of one price variant and context band.</summary>
public sealed record PriceRates(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite5m, decimal CacheWrite1h);

/// <summary>Rates of one variant (standard, priority, flex): the base ones and, by threshold, those for prompts above it.</summary>
public sealed record VariantPrices(PriceRates Base, IReadOnlyDictionary<long, PriceRates> Bands)
{
    /// <summary>Rates for a request in <paramref name="contextBand"/>: the highest band whose threshold is ≤ it, else Base.</summary>
    public PriceRates For(long contextBand)
    {
        PriceRates? best = null;
        long bestThreshold = -1;
        foreach (var (threshold, rates) in Bands)
        {
            if (threshold > contextBand || threshold <= bestThreshold) continue;
            best = rates;
            bestThreshold = threshold;
        }
        return best ?? Base;
    }
}

/// <summary>
/// Published price of one model: standard rates, the optional OpenAI priority/flex variants, the multipliers of
/// <c>provider_specific_entry</c> (<c>fast</c>, geo keys such as <c>us</c>), the price of one web search and the page
/// the price list cites as its source.
/// </summary>
public sealed record ModelPrice(
    string Id,
    VariantPrices Standard,
    VariantPrices? Priority,
    VariantPrices? Flex,
    IReadOnlyDictionary<string, decimal> Multipliers,
    decimal WebSearch,
    string? Source)
{
    /// <summary>The variant a tier is priced with; Fast is the standard variant times the <c>fast</c> multiplier.</summary>
    public VariantPrices For(PriceTier tier) => tier switch
    {
        PriceTier.Priority => Priority ?? Standard,
        PriceTier.Flex => Flex ?? Standard,
        _ => Standard
    };
}
