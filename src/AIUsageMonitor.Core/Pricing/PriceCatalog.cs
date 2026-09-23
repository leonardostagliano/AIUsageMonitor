using System.Text.RegularExpressions;

namespace AIUsageMonitor.Core.Pricing;

public enum PriceListOrigin { None, Snapshot, Downloaded }

/// <summary>Immutable price list: model id → <see cref="ModelPrice"/>, with where it came from.</summary>
public sealed partial class PriceCatalog
{
    public static readonly PriceCatalog Empty = new(new Dictionary<string, ModelPrice>(), PriceListOrigin.None, null, false);

    private readonly Dictionary<string, ModelPrice> _models;

    public PriceCatalog(IReadOnlyDictionary<string, ModelPrice> models, PriceListOrigin origin, DateTimeOffset? fetchedAt, bool overrideActive)
    {
        _models = new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, price) in models) _models[id] = price;
        Origin = origin;
        FetchedAt = fetchedAt;
        OverrideActive = overrideActive;
    }

    public int Count => _models.Count;
    public PriceListOrigin Origin { get; }

    /// <summary>When the list was downloaded (or, for the embedded snapshot, generated).</summary>
    public DateTimeOffset? FetchedAt { get; }

    /// <summary>Whether <c>prices-override.json</c> was applied on top of the list.</summary>
    public bool OverrideActive { get; }

    /// <summary>The price of <paramref name="model"/> through <see cref="Candidates"/>, null when unpriced.</summary>
    public ModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        foreach (var candidate in Candidates(model))
            if (_models.TryGetValue(candidate, out var price)) return price;
        return null;
    }

    /// <summary>
    /// The ids tried for a model name, in order: as written; lower-cased, cut at the first <c>[</c> (<c>[1m]</c>) and
    /// without an <c>anthropic/</c> or <c>openai/</c> prefix; the same without a date suffix. Never a partial match.
    /// </summary>
    public static IEnumerable<string> Candidates(string model)
    {
        var exact = model.Trim();
        yield return exact;

        var normalized = exact.ToLowerInvariant();
        var bracket = normalized.IndexOf('[');
        if (bracket > 0) normalized = normalized[..bracket];
        foreach (var prefix in (string[])["anthropic/", "openai/"])
            if (normalized.StartsWith(prefix, StringComparison.Ordinal)) normalized = normalized[prefix.Length..];
        if (normalized != exact) yield return normalized;

        var undated = DateSuffix().Replace(normalized, "");
        if (undated != normalized) yield return undated;
    }

    [GeneratedRegex(@"-(\d{8}|\d{4}-\d{2}-\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex DateSuffix();
}
