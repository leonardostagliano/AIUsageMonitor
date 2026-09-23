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

    [Fact]
    public void Trim_keeps_entries_without_a_mode_or_in_responses_mode()
    {
        using var doc = JsonDocument.Parse("""
            {
              "gpt-no-mode": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "litellm_provider": "openai" },
              "gpt-responses": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "litellm_provider": "openai", "mode": "responses" },
              "gpt-embedding": { "input_cost_per_token": 1e-6, "output_cost_per_token": 0, "litellm_provider": "openai", "mode": "embedding" }
            }
            """);

        var trimmed = LiteLlmPriceParser.Trim(doc.RootElement, out var anthropic, out var openai);

        Assert.Equal(["gpt-no-mode", "gpt-responses"], trimmed.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(0, anthropic);
        Assert.Equal(2, openai);
        Assert.Equal("responses", (string?)trimmed["gpt-responses"]!["mode"]);
    }

    [Fact]
    public void Missing_fields_come_from_the_group_they_fall_back_on_with_its_cache_discount_and_band_surcharge()
    {
        using var doc = JsonDocument.Parse("""
            {
              "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "cache_read_input_token_cost": 1e-7,
              "cache_creation_input_token_cost": 3e-6, "input_cost_per_token_above_200k_tokens": 5e-6,
              "input_cost_per_token_priority": 4e-6, "output_cost_per_token_priority": 6e-6,
              "input_cost_per_token_above_200k_tokens_priority": 8e-6
            }
            """);

        var price = LiteLlmPriceParser.ParseEntry("x", doc.RootElement)!;

        Assert.Equal(new PriceRates(0.000001m, 0.000002m, 0.0000001m, 0.000003m, 0.000003m), price.Standard.Base);
        // The band lists only its input: output as the base, cache prices with the base's ratio to input (0.1 and 3).
        Assert.Equal(200_000L, Assert.Single(price.Standard.Bands).Key);
        Assert.Equal(new PriceRates(0.000005m, 0.000002m, 0.0000005m, 0.000015m, 0.000015m), price.Standard.Bands[200_000]);
        // Same for the priority base on the standard base.
        Assert.Equal(new PriceRates(0.000004m, 0.000006m, 0.0000004m, 0.000012m, 0.000012m), price.Priority!.Base);
        // Its band lists only its input: the rest is its base with the standard band's surcharge (×1 on output, ×5 on
        // cache), with the same cache ratio to input.
        Assert.Equal(200_000L, Assert.Single(price.Priority.Bands).Key);
        Assert.Equal(new PriceRates(0.000008m, 0.000006m, 0.0000008m, 0.000024m, 0.000024m), price.Priority.Bands[200_000]);
        Assert.Null(price.Flex);
    }

    [Fact]
    public void A_variant_without_the_band_of_a_long_prompt_gets_the_standard_band_surcharge_on_its_own_base()
    {
        // gpt-5.5 as the list gives it on 2026-09-24: a 272k band for standard and flex, none for priority.
        using var doc = JsonDocument.Parse("""
            {
              "input_cost_per_token": 0.000005, "output_cost_per_token": 0.00003, "cache_read_input_token_cost": 5e-7,
              "input_cost_per_token_above_272k_tokens": 0.00001, "output_cost_per_token_above_272k_tokens": 0.000045,
              "cache_read_input_token_cost_above_272k_tokens": 0.000001,
              "input_cost_per_token_priority": 0.0000125, "output_cost_per_token_priority": 0.000075,
              "cache_read_input_token_cost_priority": 0.00000125,
              "input_cost_per_token_flex": 0.0000025, "output_cost_per_token_flex": 0.000015, "cache_read_input_token_cost_flex": 2.5e-7,
              "input_cost_per_token_above_272k_tokens_flex": 0.000005, "output_cost_per_token_above_272k_tokens_flex": 0.0000225,
              "cache_read_input_token_cost_above_272k_tokens_flex": 5e-7
            }
            """);

        var price = LiteLlmPriceParser.ParseEntry("gpt-5.5", doc.RootElement)!;

        // The rule reproduces the band the list does give for flex...
        var flex = price.Flex!.For(272_000);
        Assert.Equal((0.000005m, 0.0000225m, 0.0000005m), (flex.Input, flex.Output, flex.CacheRead));
        // ...and gives priority ×2 on input and cache, ×1.5 on output: never the short-prompt priority price, nor the
        // standard band, which would make a long priority prompt cheaper than a short one.
        var priority = price.Priority!.For(272_000);
        Assert.Equal((0.000025m, 0.0001125m, 0.0000025m), (priority.Input, priority.Output, priority.CacheRead));
        Assert.Equal(price.Priority.Base, price.Priority.For(0));
    }

    [Fact]
    public void A_variant_without_a_cache_price_keeps_the_standard_ratio_to_input()
    {
        using var doc = JsonDocument.Parse("""
            {
              "pro": {
                "input_cost_per_token": 0.00003, "output_cost_per_token": 0.00018,
                "input_cost_per_token_flex": 0.000015, "output_cost_per_token_flex": 0.00009
              },
              "discounted": {
                "input_cost_per_token": 0.000002, "output_cost_per_token": 0.00001, "cache_read_input_token_cost": 2e-7,
                "input_cost_per_token_priority": 0.000004, "output_cost_per_token_priority": 0.00002
              }
            }
            """);

        var models = LiteLlmPriceParser.Parse(doc.RootElement).Models;

        // gpt-5.4-pro lists no cache price at all: a cached flex token costs the flex input, not the standard 3e-5.
        Assert.Equal(0.000015m, models["pro"].Flex!.Base.CacheRead);
        // With a listed discount (10%) the variant gets the same discount on its own input.
        Assert.Equal(0.0000004m, models["discounted"].Priority!.Base.CacheRead);
    }

    [Fact]
    public void Reads_a_bare_web_search_price_lower_cases_multiplier_keys_and_ignores_1_hour_fields_other_than_cache_writes()
    {
        using var doc = JsonDocument.Parse("""
            {
              "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "input_cost_per_token_above_1hr": 9e-6,
              "search_context_cost_per_query": 0.02, "provider_specific_entry": { "FAST": 2 }
            }
            """);

        var price = LiteLlmPriceParser.ParseEntry("x", doc.RootElement)!;

        Assert.Equal(0.02m, price.WebSearch);
        Assert.Equal("fast", Assert.Single(price.Multipliers.Keys));
        Assert.Equal(2m, price.Multipliers["fast"]);
        Assert.Equal(new PriceRates(0.000001m, 0.000002m, 0.000001m, 0.000001m, 0.000001m), price.Standard.Base);
    }

    [Fact]
    public void Parse_skips_out_of_range_values_instead_of_throwing()
    {
        // ٢٠٠ is "200" in Arabic-Indic digits: \d matches it, but it is not a threshold.
        using var doc = JsonDocument.Parse("""
            {
              "good": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6 },
              "huge-input": { "input_cost_per_token": 1e30, "output_cost_per_token": 2e-6 },
              "infinite-cache-read": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "cache_read_input_token_cost": 1e400 },
              "huge-multiplier": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "provider_specific_entry": { "fast": 1e30, "us": 1.1 } },
              "huge-web-search": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "search_context_cost_per_query": 1e30 },
              "huge-priority": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "input_cost_per_token_priority": 1e30 },
              "negative-input": { "input_cost_per_token": -1e-6, "output_cost_per_token": 2e-6 },
              "implausible-output": { "input_cost_per_token": 1e-6, "output_cost_per_token": 1.5 },
              "odd-multipliers": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "provider_specific_entry": { "fast": 1000, "us": 0, "eu": -2, "au": 1.2 } },
              "negative-cache-read": { "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6, "cache_read_input_token_cost": -1e-7 },
              "odd-bands": {
                "input_cost_per_token": 1e-6, "output_cost_per_token": 2e-6,
                "input_cost_per_token_above_99999999999999999999k_tokens": 5e-6,
                "input_cost_per_token_above_9223372036854776k_tokens": 5e-6,
                "input_cost_per_token_above_٢٠٠k_tokens": 5e-6
              }
            }
            """);

        var parsed = LiteLlmPriceParser.Parse(doc.RootElement);

        var standard = new PriceRates(0.000001m, 0.000002m, 0.000001m, 0.000001m, 0.000001m);
        Assert.Equal(standard, parsed.Models["good"].Standard.Base);
        Assert.False(parsed.Models.ContainsKey("huge-input"));
        Assert.Equal(standard, parsed.Models["infinite-cache-read"].Standard.Base);
        Assert.Equal("us", Assert.Single(parsed.Models["huge-multiplier"].Multipliers).Key);
        Assert.Equal(1.1m, parsed.Models["huge-multiplier"].Multipliers["us"]);
        Assert.Equal(0m, parsed.Models["huge-web-search"].WebSearch);
        Assert.Null(parsed.Models["huge-priority"].Priority);
        // Negative or implausible prices (above 1 USD per token) and multipliers outside 0–100 are ignored too.
        Assert.False(parsed.Models.ContainsKey("negative-input"));
        Assert.False(parsed.Models.ContainsKey("implausible-output"));
        Assert.Equal("au", Assert.Single(parsed.Models["odd-multipliers"].Multipliers).Key);
        Assert.Equal(standard, parsed.Models["negative-cache-read"].Standard.Base);
        Assert.Empty(parsed.Models["odd-bands"].Standard.Bands);
        Assert.Empty(parsed.UnknownThresholds);
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
    public void Find_resolves_an_openai_prefix_and_a_prefix_context_suffix_and_date_together()
    {
        var catalog = new PriceCatalog(ParseTrimmed().Models, PriceListOrigin.Snapshot, null, false);

        Assert.Equal("gpt-6-luna", catalog.Find("openai/gpt-6-luna")!.Id);
        Assert.Equal("claude-sonnet-4-5", catalog.Find("Anthropic/Claude-Sonnet-4-5-20250929[1m]")!.Id);
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
