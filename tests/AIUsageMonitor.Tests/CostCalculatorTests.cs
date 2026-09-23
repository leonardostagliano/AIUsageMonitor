using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Pricing;

namespace AIUsageMonitor.Tests;

public class CostCalculatorTests
{
    private static readonly PriceRates OpusRates = new(0.000004m, 0.00002m, 0.0000002m, 0.000005m, 0.000008m);

    private static ModelPrice Price(string id, PriceRates rates, IReadOnlyDictionary<long, PriceRates>? bands = null,
        VariantPrices? priority = null, IReadOnlyDictionary<string, decimal>? multipliers = null, decimal webSearch = 0m,
        VariantPrices? flex = null) =>
        new(id, new VariantPrices(rates, bands ?? new Dictionary<long, PriceRates>()), priority, flex,
            multipliers ?? new Dictionary<string, decimal>(), webSearch, null);

    private static PriceCatalog Catalog(params ModelPrice[] prices) =>
        new(prices.ToDictionary(p => p.Id), PriceListOrigin.Snapshot, null, false);

    private static UsageLedger Ledger(params (UsageKey Key, LedgerTokens Tokens)[] entries) =>
        UsageLedger.From(entries.Select(e => KeyValuePair.Create(e.Key, e.Tokens)));

    private static UsageKey Key(string model, PriceTier tier = PriceTier.Standard, string? geo = null, long band = 0) => new(model, tier, geo, band);

    [Fact]
    public void Prices_every_token_bucket_and_converts_to_euro()
    {
        var catalog = Catalog(Price("claude-opus-5-5", OpusRates));
        // 1M input = 4 $, 100k output = 2 $, 1M cache read = 0.2 $, 100k 5m write = 0.5 $, 100k 1h write = 0.8 $ → 7.5 $
        var ledger = Ledger((Key("claude-opus-5-5"), new LedgerTokens(1_000_000, 100_000, 1_000_000, 100_000, 100_000)));

        var cost = CostCalculator.Compute(ledger, catalog, usdPerEur: 1.25m);

        Assert.Equal(6m, cost.Eur);
        var model = Assert.Single(cost.ByModel);
        Assert.Equal(new ModelCost("claude-opus-5-5", 6m, true), model);
    }

    [Fact]
    public void The_band_of_the_key_selects_the_long_context_rates()
    {
        var bands = new Dictionary<long, PriceRates> { [272_000] = OpusRates with { Input = 0.00001m } };
        var price = Price("gpt-6-astra", OpusRates, bands);

        Assert.Equal(10m, CostCalculator.Usd(Key("gpt-6-astra", band: 272_000), new LedgerTokens(1_000_000, 0, 0, 0, 0), price));
        Assert.Equal(4m, CostCalculator.Usd(Key("gpt-6-astra", band: 200_000), new LedgerTokens(1_000_000, 0, 0, 0, 0), price));
    }

    [Fact]
    public void Fast_and_geography_multiply_the_tokens_but_not_the_web_searches()
    {
        var price = Price("claude-opus-5-5", OpusRates, multipliers: new Dictionary<string, decimal> { ["fast"] = 2m, ["us"] = 1.1m }, webSearch: 0.01m);
        var tokens = new LedgerTokens(1_000_000, 0, 0, 0, 0, WebSearches: 10);

        Assert.Equal(4.1m, CostCalculator.Usd(Key("claude-opus-5-5"), tokens, price));
        Assert.Equal(8.1m, CostCalculator.Usd(Key("claude-opus-5-5", PriceTier.Fast), tokens, price));
        Assert.Equal(8.9m, CostCalculator.Usd(Key("claude-opus-5-5", PriceTier.Fast, "us"), tokens, price));
        Assert.Equal(4.1m, CostCalculator.Usd(Key("claude-opus-5-5", geo: "eu"), tokens, price));
    }

    [Fact]
    public void Fast_without_a_multiplier_and_priority_without_its_rates_use_the_standard_price()
    {
        var plain = Price("m", OpusRates);
        var tokens = new LedgerTokens(1_000_000, 0, 0, 0, 0);

        Assert.Equal(4m, CostCalculator.Usd(Key("m", PriceTier.Fast), tokens, plain));
        Assert.Equal(4m, CostCalculator.Usd(Key("m", PriceTier.Priority), tokens, plain));

        var withPriority = Price("m", OpusRates, priority: new VariantPrices(OpusRates with { Input = 0.000008m }, new Dictionary<long, PriceRates>()));
        Assert.Equal(8m, CostCalculator.Usd(Key("m", PriceTier.Priority), tokens, withPriority));
    }

    [Fact]
    public void Flex_uses_its_own_rates_and_falls_back_to_the_standard_price_without_them()
    {
        var tokens = new LedgerTokens(1_000_000, 0, 0, 0, 0);

        Assert.Equal(4m, CostCalculator.Usd(Key("m", PriceTier.Flex), tokens, Price("m", OpusRates)));

        var flexBands = new Dictionary<long, PriceRates> { [272_000] = OpusRates with { Input = 0.000003m } };
        var withFlex = Price("m", OpusRates, flex: new VariantPrices(OpusRates with { Input = 0.000002m }, flexBands));
        Assert.Equal(2m, CostCalculator.Usd(Key("m", PriceTier.Flex), tokens, withFlex));
        Assert.Equal(3m, CostCalculator.Usd(Key("m", PriceTier.Flex, band: 272_000), tokens, withFlex));
        Assert.Equal(4m, CostCalculator.Usd(Key("m"), tokens, withFlex));
    }

    [Fact]
    public void Unpriced_and_unknown_models_are_listed_but_add_nothing()
    {
        var catalog = Catalog(Price("claude-opus-5-5", OpusRates));
        var ledger = Ledger(
            (Key("claude-opus-5-5"), new LedgerTokens(1_000_000, 0, 0, 0, 0)),
            (Key("codex-auto-review"), new LedgerTokens(5, 5, 0, 0, 0)),
            (Key(""), new LedgerTokens(1, 0, 0, 0, 0)));

        var cost = CostCalculator.Compute(ledger, catalog, 1m);

        Assert.Equal(4m, cost.Eur);
        Assert.True(cost.AnyPriced);
        Assert.True(cost.AnyUnpriced);
        Assert.Equal(["codex-auto-review", CostCalculator.UnknownModel], cost.UnpricedModels.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Entries_of_the_same_model_are_one_line_and_results_add_up()
    {
        var catalog = Catalog(Price("m", OpusRates));
        var cost = CostCalculator.Compute(Ledger(
            (Key("m"), new LedgerTokens(1_000_000, 0, 0, 0, 0)),
            (Key("m", PriceTier.Fast), new LedgerTokens(1_000_000, 0, 0, 0, 0))), catalog, 1m);

        Assert.Equal(new ModelCost("m", 8m, true), Assert.Single(cost.ByModel));
        var sum = cost + CostCalculator.Compute(Ledger((Key("n"), new LedgerTokens(1, 0, 0, 0, 0))), catalog, 1m);
        Assert.Equal(8m, sum.Eur);
        Assert.Equal(2, sum.ByModel.Count);
        Assert.Same(cost, cost + CostResult.None);
    }

    [Fact]
    public void An_empty_or_missing_ledger_has_no_cost()
    {
        Assert.False(CostCalculator.Compute(null, PriceCatalog.Empty, 1.14m).HasUsage);
        Assert.False(CostCalculator.Compute(UsageLedger.Empty, PriceCatalog.Empty, 1.14m).HasUsage);
        Assert.Null(CostFormatter.Short(CostResult.None));
    }

    [Theory]
    [InlineData("0.004", "< 0,01 €")]
    [InlineData("0.01", "0,01 €")]
    [InlineData("3.214", "3,21 €")]
    [InlineData("99.5", "99,50 €")]
    [InlineData("99.994", "99,99 €")]
    [InlineData("99.995", "100 €")]
    [InlineData("99.996", "100 €")]
    [InlineData("100", "100 €")]
    [InlineData("1234.4", "1.234 €")]
    public void Amount_uses_Italian_formatting(string eur, string expected) =>
        Assert.Equal(expected, CostFormatter.Amount(decimal.Parse(eur, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void Short_marks_approximate_partial_and_unavailable_costs()
    {
        var priced = new CostResult(3.21m, [new ModelCost("m", 3.21m, true)]);
        var partial = new CostResult(3.21m, [new ModelCost("m", 3.21m, true), new ModelCost("codex-auto-review", 0m, false)]);
        var none = new CostResult(0m, [new ModelCost("codex-auto-review", 0m, false)]);
        var tiny = new CostResult(0.001m, [new ModelCost("m", 0.001m, true)]);

        Assert.Equal("≈ 3,21 €", CostFormatter.Short(priced));
        Assert.Equal("≥ 3,21 €", CostFormatter.Short(partial));
        Assert.Equal("costo n/d", CostFormatter.Short(none));
        Assert.Equal("< 0,01 €", CostFormatter.Short(tiny));
        Assert.Equal(["m ≈ 3,21 €", "codex-auto-review: prezzo non disponibile"], CostFormatter.ModelLines(partial));
        Assert.Equal(["m < 0,01 €"], CostFormatter.ModelLines(tiny));
    }
}
