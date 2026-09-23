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
                band = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) * 1000;
                if (!PricingThresholds.Known.Contains(band))
                {
                    unknownThresholds?.Add(band);
                    continue;
                }
            }

            var rateField = match.Groups[1].Value switch
            {
                "input_cost_per_token" => RateField.Input,
                "output_cost_per_token" => RateField.Output,
                "cache_read_input_token_cost" => RateField.CacheRead,
                _ => match.Groups[2].Success ? RateField.CacheWrite1h : RateField.CacheWrite5m
            };
            var variant = match.Groups[4].Success ? match.Groups[4].Value : "";
            if (!raw.TryGetValue((variant, band), out var rates)) raw[(variant, band)] = rates = new Dictionary<RateField, decimal>();
            rates[rateField] = Rate(field.Value);
        }

        if (!raw.TryGetValue(("", 0), out var standardRaw)
            || !standardRaw.ContainsKey(RateField.Input) || !standardRaw.ContainsKey(RateField.Output)) return null;

        var standardBase = Complete(standardRaw, fallback: null);
        var standard = new VariantPrices(standardBase, Bands(raw, "", standardBase));

        VariantPrices? Variant(string name)
        {
            if (!raw.Keys.Any(k => k.Variant == name)) return null;
            var baseRates = raw.TryGetValue((name, 0), out var variantRaw) ? Complete(variantRaw, standardBase) : standardBase;
            return new VariantPrices(baseRates, Bands(raw, name, baseRates));
        }

        return new ModelPrice(id, standard, Variant("priority"), Variant("flex"), Multipliers(entry), WebSearch(entry), String(entry, "source"));
    }

    /// <summary>
    /// Fills the missing fields: from <paramref name="fallback"/> when given (a band from its variant's base, a
    /// variant's base from the standard one), otherwise cache read and 5 minute write from input, 1 hour write from
    /// the 5 minute one.
    /// </summary>
    private static PriceRates Complete(Dictionary<RateField, decimal> rates, PriceRates? fallback)
    {
        decimal Get(RateField field, decimal otherwise) => rates.TryGetValue(field, out var value) ? value : otherwise;
        var input = Get(RateField.Input, fallback?.Input ?? 0m);
        var output = Get(RateField.Output, fallback?.Output ?? 0m);
        var cacheRead = Get(RateField.CacheRead, fallback?.CacheRead ?? input);
        var write5m = Get(RateField.CacheWrite5m, fallback?.CacheWrite5m ?? input);
        var write1h = Get(RateField.CacheWrite1h, fallback?.CacheWrite1h ?? write5m);
        return new PriceRates(input, output, cacheRead, write5m, write1h);
    }

    private static IReadOnlyDictionary<long, PriceRates> Bands(
        Dictionary<(string Variant, long Band), Dictionary<RateField, decimal>> raw, string variant, PriceRates baseRates) =>
        raw.Where(kv => kv.Key.Variant == variant && kv.Key.Band > 0)
           .ToDictionary(kv => kv.Key.Band, kv => Complete(kv.Value, baseRates));

    private static IReadOnlyDictionary<string, decimal> Multipliers(JsonElement entry)
    {
        var multipliers = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (entry.TryGetProperty("provider_specific_entry", out var specific) && specific.ValueKind == JsonValueKind.Object)
            foreach (var property in specific.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Number)
                    multipliers[property.Name.ToLowerInvariant()] = Rate(property.Value);
        return multipliers;
    }

    private static decimal WebSearch(JsonElement entry)
    {
        if (!entry.TryGetProperty("search_context_cost_per_query", out var cost)) return 0m;
        if (cost.ValueKind == JsonValueKind.Number) return Rate(cost);
        return cost.ValueKind == JsonValueKind.Object && cost.TryGetProperty("search_context_size_medium", out var medium)
               && medium.ValueKind == JsonValueKind.Number
            ? Rate(medium)
            : 0m;
    }

    /// <summary>Prices are written in exponent notation (2.5e-7): read as double, then kept as decimal (15 significant digits).</summary>
    private static decimal Rate(JsonElement value) => (decimal)value.GetDouble();

    private static bool IsNumber(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number;

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
