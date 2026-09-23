using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Pricing;

public sealed record ParsedPrices(IReadOnlyDictionary<string, ModelPrice> Models, IReadOnlyCollection<long> UnknownThresholds);

/// <summary>
/// Reads the LiteLLM price list (<c>model_prices_and_context_window.json</c>, model id → entry). <see cref="Trim"/>
/// reduces the full document to the Anthropic and OpenAI chat models and the fields used here — the format of the
/// local cache and of the embedded snapshot — and <see cref="Parse"/> turns such an object into prices.
/// </summary>
public static partial class LiteLlmPriceParser
{
    private static readonly HashSet<string> Modes = new(StringComparer.Ordinal) { "chat", "responses" };

    // Plausibility bounds: a value outside them comes from a damaged or hostile list (the cache, a download or
    // prices-override.json) and is ignored like a missing field. They also keep every cost well inside the decimal range.
    /// <summary>Largest price per token: 1 USD, over a thousand times the dearest one listed today (o1-pro output, 0.0006).</summary>
    private const decimal MaxRate = 1m;

    /// <summary>Largest multiplier (fast mode and the geographies stay well below 10× today).</summary>
    private const decimal MaxMultiplier = 100m;

    /// <summary>Largest price of one web search (0.025 USD at most today).</summary>
    private const decimal MaxWebSearch = 100m;

    private static readonly HashSet<string> KeptFields = new(StringComparer.Ordinal)
    {
        "litellm_provider", "mode", "source", "provider_specific_entry", "search_context_cost_per_query"
    };

    [GeneratedRegex(@"^(input_cost_per_token|output_cost_per_token|cache_read_input_token_cost|cache_creation_input_token_cost)(_above_1hr)?(?:_above_(\d+)k_tokens)?(?:_(priority|flex))?$", RegexOptions.CultureInvariant)]
    private static partial Regex CostField();

    private enum RateField { Input, Output, CacheRead, CacheWrite5m, CacheWrite1h }

    /// <summary>The Anthropic and OpenAI chat entries with a price, reduced to the fields <see cref="Parse"/> reads.</summary>
    public static JsonObject Trim(JsonElement document, out int anthropic, out int openai)
    {
        anthropic = 0;
        openai = 0;
        var models = new JsonObject();
        if (document.ValueKind != JsonValueKind.Object) return models;

        foreach (var property in document.EnumerateObject())
        {
            var entry = property.Value;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var provider = String(entry, "litellm_provider");
            if (provider is not ("anthropic" or "openai")) continue;
            if (String(entry, "mode") is { } mode && !Modes.Contains(mode)) continue;
            if (!IsNumber(entry, "input_cost_per_token") || !IsNumber(entry, "output_cost_per_token")) continue;

            var trimmed = new JsonObject();
            foreach (var field in entry.EnumerateObject())
                if (KeptFields.Contains(field.Name) || CostField().IsMatch(field.Name))
                    trimmed[field.Name] = JsonNode.Parse(field.Value.GetRawText());
            models[property.Name] = trimmed;
            if (provider == "anthropic") anthropic++;
            else openai++;
        }
        return models;
    }

    /// <summary>Every entry of <paramref name="models"/> that carries a price. Never throws on odd content.</summary>
    public static ParsedPrices Parse(JsonElement models)
    {
        var prices = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        var unknown = new SortedSet<long>();
        if (models.ValueKind == JsonValueKind.Object)
            foreach (var property in models.EnumerateObject())
                if (ParseEntry(property.Name, property.Value, unknown) is { } price) prices[property.Name] = price;
        return new ParsedPrices(prices, unknown);
    }

    /// <summary>The price of one entry, or null when it has no standard input and output price.</summary>
    public static ModelPrice? ParseEntry(string id, JsonElement entry, ISet<long>? unknownThresholds = null)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;

        var raw = new Dictionary<(string Variant, long Band), Dictionary<RateField, decimal>>();
        foreach (var field in entry.EnumerateObject())
        {
            var match = CostField().Match(field.Name);
            if (!match.Success || field.Value.ValueKind != JsonValueKind.Number) continue;

            var isCacheWrite = match.Groups[1].Value == "cache_creation_input_token_cost";
            // "_above_1hr" exists only on cache writes: anything else is not a field this parser understands.
            if (match.Groups[2].Success && !isCacheWrite) continue;

            long band = 0;
            if (match.Groups[3].Success)
            {
                // \d also matches non-ASCII digits, which NumberStyles.None rejects; a band that does not fit a long
                // once in tokens is no threshold either.
                if (!long.TryParse(match.Groups[3].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var thousands)
                    || thousands > long.MaxValue / 1000) continue;
                band = thousands * 1000;
                if (!PricingThresholds.Known.Contains(band))
                {
                    unknownThresholds?.Add(band);
                    continue;
                }
            }

            // Checked before the variant's rates are created: a variant exists only through a usable field.
            if (!TryRate(field.Value, out var rate)) continue;

            var rateField = match.Groups[1].Value switch
            {
                "input_cost_per_token" => RateField.Input,
                "output_cost_per_token" => RateField.Output,
                "cache_read_input_token_cost" => RateField.CacheRead,
                _ => match.Groups[2].Success ? RateField.CacheWrite1h : RateField.CacheWrite5m
            };
            var variant = match.Groups[4].Success ? match.Groups[4].Value : "";
            if (!raw.TryGetValue((variant, band), out var rates)) raw[(variant, band)] = rates = new Dictionary<RateField, decimal>();
            rates[rateField] = rate;
        }

        if (!raw.TryGetValue(("", 0), out var standardRaw)
            || !standardRaw.ContainsKey(RateField.Input) || !standardRaw.ContainsKey(RateField.Output)) return null;

        var standardBase = Complete(standardRaw, fallback: null);
        var standardBands = raw.Where(kv => kv.Key.Variant == "" && kv.Key.Band > 0)
                               .ToDictionary(kv => kv.Key.Band, kv => Complete(kv.Value, standardBase));
        var standard = new VariantPrices(standardBase, standardBands);

        VariantPrices? Variant(string name)
        {
            if (!raw.Keys.Any(k => k.Variant == name)) return null;
            var baseRates = raw.TryGetValue((name, 0), out var variantRaw) ? Complete(variantRaw, standardBase) : standardBase;
            // A band of a variant is its base with the surcharge of the standard band, field by field, then whatever the
            // variant lists for that band: every variant band price the list gives (72 on 2026-09-24) is exactly that.
            // A priority prompt above 272k on a model with no priority band is thus priced neither like a short
            // priority prompt nor like a standard one, which would cost less than a short priority prompt.
            var bands = new Dictionary<long, PriceRates>();
            var thresholds = standardBands.Keys.Concat(raw.Keys.Where(k => k.Variant == name && k.Band > 0).Select(k => k.Band));
            foreach (var threshold in thresholds.Distinct())
            {
                var derived = standardBands.TryGetValue(threshold, out var standardBand)
                    ? Surcharged(baseRates, standardBase, standardBand)
                    : baseRates;
                bands[threshold] = raw.TryGetValue((name, threshold), out var bandRaw) ? Complete(bandRaw, derived) : derived;
            }
            return new VariantPrices(baseRates, bands);
        }

        return new ModelPrice(id, standard, Variant("priority"), Variant("flex"), Multipliers(entry), WebSearch(entry), String(entry, "source"));
    }

    /// <summary>
    /// Fills the missing fields of a group of rates. Without <paramref name="fallback"/> (the standard base), as the
    /// mapping table says: cache read and 5 minute write cost the input, the 1 hour write the 5 minute one. With it (a
    /// variant's base falls back on the standard base, a band on its variant's base): input and output are the
    /// fallback's, and a cache price keeps the fallback's ratio to input, i.e. the same discount on this group's own
    /// input. 140 of the 144 cache prices the list gives for a variant or a band follow that ratio (2026-09-24), the
    /// other 4 within 4%; taking the fallback's price as is could price a cached token above an uncached one (flex
    /// gpt-5.4-pro: 3e-5 against a flex input of 1.5e-5).
    /// </summary>
    private static PriceRates Complete(Dictionary<RateField, decimal> rates, PriceRates? fallback)
    {
        decimal Get(RateField field, decimal otherwise) => rates.TryGetValue(field, out var value) ? value : otherwise;
        if (fallback is null)
        {
            var standardInput = Get(RateField.Input, 0m);
            var standardWrite5m = Get(RateField.CacheWrite5m, standardInput);
            return new PriceRates(standardInput, Get(RateField.Output, 0m), Get(RateField.CacheRead, standardInput),
                standardWrite5m, Get(RateField.CacheWrite1h, standardWrite5m));
        }

        var input = Get(RateField.Input, fallback.Input);
        decimal Cache(RateField field, decimal price) =>
            Get(field, fallback.Input == 0m ? price : Scaled(input, fallback.Input, price));
        return new PriceRates(input, Get(RateField.Output, fallback.Output), Cache(RateField.CacheRead, fallback.CacheRead),
            Cache(RateField.CacheWrite5m, fallback.CacheWrite5m), Cache(RateField.CacheWrite1h, fallback.CacheWrite1h));
    }

    /// <summary><paramref name="rates"/> with the surcharge that takes <paramref name="from"/> to <paramref name="to"/>, field by field.</summary>
    private static PriceRates Surcharged(PriceRates rates, PriceRates from, PriceRates to) => new(
        Scaled(rates.Input, from.Input, to.Input),
        Scaled(rates.Output, from.Output, to.Output),
        Scaled(rates.CacheRead, from.CacheRead, to.CacheRead),
        Scaled(rates.CacheWrite5m, from.CacheWrite5m, to.CacheWrite5m),
        Scaled(rates.CacheWrite1h, from.CacheWrite1h, to.CacheWrite1h));

    /// <summary>
    /// <paramref name="value"/> × <paramref name="to"/> / <paramref name="from"/> (<paramref name="value"/> itself when
    /// <paramref name="from"/> is zero), capped at <see cref="MaxRate"/>. Every rate stays within 0–1, so the product
    /// never overflows and the quotient stays far inside the decimal range.
    /// </summary>
    private static decimal Scaled(decimal value, decimal from, decimal to) =>
        from == 0m ? value : Math.Min(MaxRate, value * to / from);

    private static IReadOnlyDictionary<string, decimal> Multipliers(JsonElement entry)
    {
        var multipliers = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (entry.TryGetProperty("provider_specific_entry", out var specific) && specific.ValueKind == JsonValueKind.Object)
            foreach (var property in specific.EnumerateObject())
                if (TryNumber(property.Value, MaxMultiplier, out var multiplier) && multiplier > 0m)
                    multipliers[property.Name.ToLowerInvariant()] = multiplier;
        return multipliers;
    }

    private static decimal WebSearch(JsonElement entry)
    {
        if (!entry.TryGetProperty("search_context_cost_per_query", out var cost)) return 0m;
        if (cost.ValueKind == JsonValueKind.Object && cost.TryGetProperty("search_context_size_medium", out var medium)) cost = medium;
        return TryNumber(cost, MaxWebSearch, out var price) ? price : 0m;
    }

    /// <summary>A price per token between 0 and <see cref="MaxRate"/>.</summary>
    private static bool TryRate(JsonElement value, out decimal rate) => TryNumber(value, MaxRate, out rate);

    /// <summary>
    /// Prices are written in exponent notation (2.5e-7): read as double, then kept as decimal (15 significant digits).
    /// False for anything else, for a negative number and for one above <paramref name="max"/>.
    /// </summary>
    private static bool TryNumber(JsonElement value, decimal max, out decimal number)
    {
        number = 0m;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var raw) || !(raw >= 0 && raw <= (double)max)) return false;
        number = (decimal)raw;
        return true;
    }

    private static bool IsNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number;

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
