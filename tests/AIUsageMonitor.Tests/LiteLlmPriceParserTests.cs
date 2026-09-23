using System.Text.Json;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Pricing;

namespace AIUsageMonitor.Tests;

public class LiteLlmPriceParserTests
{
    private static JsonDocument Fixture() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "litellm-prices.json")));

    private static ParsedPrices ParseTrimmed()
    {
        using var doc = Fixture();
        var trimmed = LiteLlmPriceParser.Trim(doc.RootElement, out _, out _);
        using var trimmedDoc = JsonDocument.Parse(trimmed.ToJsonString());
        return LiteLlmPriceParser.Parse(trimmedDoc.RootElement);
    }

    [Fact]
    public void Trim_keeps_only_priced_Anthropic_and_OpenAI_chat_entries_and_the_fields_it_uses()
    {
        using var doc = Fixture();

        var trimmed = LiteLlmPriceParser.Trim(doc.RootElement, out var anthropic, out var openai);

        Assert.Equal(["claude-fable-5-1", "claude-haiku-4-5-20251001", "claude-opus-5-5", "claude-sonnet-4-5", "claude-weird", "gpt-6-luna"],
            trimmed.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(5, anthropic);
        Assert.Equal(1, openai);
        var opus = trimmed["claude-opus-5-5"]!.AsObject();
        Assert.False(opus.ContainsKey("max_tokens"));
        Assert.False(opus.ContainsKey("supports_fast_mode"));
        Assert.True(opus.ContainsKey("provider_specific_entry"));
        Assert.True(opus.ContainsKey("cache_creation_input_token_cost_above_1hr"));
        Assert.False(trimmed["gpt-6-luna"]!.AsObject().ContainsKey("input_cost_per_token_batches"));
    }

    [Fact]
    public void Parses_an_Anthropic_entry_with_the_1_hour_cache_price_fast_multiplier_and_web_search()
    {
        var opus = ParseTrimmed().Models["claude-opus-5-5"];

        Assert.Equal(new PriceRates(0.000004m, 0.00002m, 0.0000002m, 0.000005m, 0.000008m), opus.Standard.Base);
        Assert.Empty(opus.Standard.Bands);
        Assert.Null(opus.Priority);
        Assert.Equal(2m, opus.Multipliers["fast"]);
        Assert.Equal(0.01m, opus.WebSearch);
        Assert.Equal("https://platform.claude.com/docs/en/about-claude/pricing", opus.Source);
        Assert.Equal(1.1m, ParseTrimmed().Models["claude-fable-5-1"].Multipliers["us"]);
    }

    [Fact]
    public void Parses_the_200k_band_including_its_1_hour_cache_price()
    {
        var sonnet = ParseTrimmed().Models["claude-sonnet-4-5"];

        Assert.Equal(new PriceRates(0.000006m, 0.0000225m, 0.0000006m, 0.0000075m, 0.000012m), sonnet.Standard.Bands[200_000]);
        Assert.Equal(sonnet.Standard.Base, sonnet.Standard.For(0));
        Assert.Equal(sonnet.Standard.Bands[200_000], sonnet.Standard.For(272_000));
    }

    [Fact]
    public void Parses_the_OpenAI_272k_band_and_the_priority_and_flex_variants_ignoring_batches()
    {
        var luna = ParseTrimmed().Models["gpt-6-luna"];

        Assert.Equal(new PriceRates(0.0000001m, 0.0000005m, 0.00000001m, 0.000000125m, 0.000000125m), luna.Standard.Base);
        Assert.Equal(0.0000002m, luna.Standard.Bands[272_000].Input);
        Assert.Equal(0.0000002m, luna.Priority!.Base.Input);
        Assert.Equal(0.000001m, luna.Priority.Base.Output);
        Assert.Equal(0.0000004m, luna.Priority.Bands[272_000].Input);
        Assert.Equal(0.00000005m, luna.Flex!.Base.Input);
        Assert.Same(luna.Priority, luna.For(PriceTier.Priority));
        Assert.Same(luna.Standard, luna.For(PriceTier.Fast));
    }

    [Fact]
    public void Missing_cache_prices_fall_back_to_input_and_the_1_hour_write_to_the_5_minute_one()
    {
        var haiku = ParseTrimmed().Models["claude-haiku-4-5-20251001"];

        Assert.Equal(new PriceRates(0.000001m, 0.000005m, 0.000001m, 0.000001m, 0.000001m), haiku.Standard.Base);
        Assert.Equal(0m, haiku.WebSearch);
        Assert.Empty(haiku.Multipliers);
    }

    [Fact]
    public void An_unknown_threshold_is_reported_and_ignored()
    {
        var parsed = ParseTrimmed();

        Assert.Equal([128_000L], parsed.UnknownThresholds);
        Assert.Empty(parsed.Models["claude-weird"].Standard.Bands);
    }

    [Fact]
    public void An_entry_without_input_and_output_prices_is_not_a_price()
    {
        using var doc = JsonDocument.Parse("""{"cache_read_input_token_cost": 1e-7}""");
        Assert.Null(LiteLlmPriceParser.ParseEntry("x", doc.RootElement));
    }

    [Theory]
    [InlineData("claude-opus-5-5")]
    [InlineData("CLAUDE-OPUS-5-5")]
    [InlineData("claude-opus-5-5[1m]")]
    [InlineData("anthropic/claude-opus-5-5")]
    public void Find_resolves_case_context_suffix_and_provider_prefix(string query)
    {
        var catalog = new PriceCatalog(ParseTrimmed().Models, PriceListOrigin.Snapshot, null, false);
        Assert.Equal("claude-opus-5-5", catalog.Find(query)!.Id);
    }

    [Fact]
    public void Find_drops_a_date_suffix_but_never_matches_partially()
    {
        var catalog = new PriceCatalog(ParseTrimmed().Models, PriceListOrigin.Snapshot, null, false);

        Assert.Equal("claude-sonnet-4-5", catalog.Find("claude-sonnet-4-5-20250929")!.Id);
        Assert.Equal("claude-sonnet-4-5", catalog.Find("claude-sonnet-4-5-2025-09-29")!.Id);
        Assert.Null(catalog.Find("claude-opus-5"));
        Assert.Null(catalog.Find("claude-opus-5-5-5"));
        Assert.Null(catalog.Find("gpt-6"));
        Assert.Null(catalog.Find("codex-auto-review"));
        Assert.Null(catalog.Find(""));
        Assert.Null(catalog.Find(null));
        Assert.Equal(6, catalog.Count);
    }
}
