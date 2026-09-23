namespace AIUsageMonitor.Core.Models;

/// <summary>Price variant a request was served with: Claude fast mode, OpenAI priority or flex processing.</summary>
public enum PriceTier { Standard, Fast, Priority, Flex }

/// <summary>
/// What a group of requests is priced by: the model id as the agent wrote it, the price variant, the inference
/// geography (Claude <c>inference_geo</c>, null when not restricted) and the long-context band — the highest
/// <see cref="PricingThresholds.Known"/> threshold the request's prompt exceeded, 0 when none.
/// </summary>
public sealed record UsageKey(string Model, PriceTier Tier, string? Geo, long ContextBand) : IComparable<UsageKey>
{
    public int CompareTo(UsageKey? other)
    {
        if (other is null) return 1;
        var byModel = string.CompareOrdinal(Model, other.Model);
        if (byModel != 0) return byModel;
        var byTier = Tier.CompareTo(other.Tier);
        if (byTier != 0) return byTier;
        var byGeo = string.CompareOrdinal(Geo ?? "", other.Geo ?? "");
        return byGeo != 0 ? byGeo : ContextBand.CompareTo(other.ContextBand);
    }
}

/// <summary>Tokens of one ledger entry, split the way the price lists split them.</summary>
public sealed record LedgerTokens(long Input, long Output, long CacheRead, long CacheWrite5m, long CacheWrite1h, long WebSearches = 0)
{
    public static readonly LedgerTokens Zero = new(0, 0, 0, 0, 0);

    public bool IsZero => Input == 0 && Output == 0 && CacheRead == 0 && CacheWrite5m == 0 && CacheWrite1h == 0 && WebSearches == 0;

    public static LedgerTokens operator +(LedgerTokens a, LedgerTokens b) => new(
        a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead,
        a.CacheWrite5m + b.CacheWrite5m, a.CacheWrite1h + b.CacheWrite1h, a.WebSearches + b.WebSearches);
}

public sealed record LedgerEntry(UsageKey Key, LedgerTokens Tokens);

/// <summary>
/// Token usage of a transcript or rollout split by <see cref="UsageKey"/>: what the cost is computed from, at display
/// time, so a new price list applies without re-reading any file. Immutable, entries sorted by key and never zero,
/// with value equality so the tracker raises Changed only when a ledger really moved.
/// </summary>
public sealed class UsageLedger : IEquatable<UsageLedger>
{
    public static readonly UsageLedger Empty = new([]);

    private UsageLedger(IReadOnlyList<LedgerEntry> entries) => Entries = entries;

    public IReadOnlyList<LedgerEntry> Entries { get; }

    public bool IsEmpty => Entries.Count == 0;

    public static UsageLedger From(IEnumerable<KeyValuePair<UsageKey, LedgerTokens>> entries)
    {
        var merged = new SortedDictionary<UsageKey, LedgerTokens>();
        foreach (var (key, tokens) in entries)
        {
            if (tokens.IsZero) continue;
            merged[key] = merged.TryGetValue(key, out var existing) ? existing + tokens : tokens;
        }
        return merged.Count == 0 ? Empty : new UsageLedger(merged.Select(kv => new LedgerEntry(kv.Key, kv.Value)).ToList());
    }

    public static UsageLedger operator +(UsageLedger a, UsageLedger b) =>
        a.IsEmpty ? b
        : b.IsEmpty ? a
        : From(a.Entries.Concat(b.Entries).Select(e => KeyValuePair.Create(e.Key, e.Tokens)));

    /// <summary>The same usage as the counters' <see cref="TokenUsage"/>: both cache-write durations go in CacheWrite.</summary>
    public TokenUsage ToTokenUsage() => Entries.Aggregate(TokenUsage.Zero, (acc, e) => acc + new TokenUsage(
        e.Tokens.Input, e.Tokens.Output, e.Tokens.CacheRead, e.Tokens.CacheWrite5m + e.Tokens.CacheWrite1h));

    public bool Equals(UsageLedger? other) =>
        other is not null && (ReferenceEquals(this, other) || Entries.SequenceEqual(other.Entries));

    public override bool Equals(object? obj) => Equals(obj as UsageLedger);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var entry in Entries) hash.Add(entry);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Mutable accumulator a counter keeps per file. <see cref="ToLedger"/> snapshots it and caches the snapshot until the
/// next non-zero <see cref="Add"/>, so asking for the ledger of an unchanged transcript allocates nothing.
/// </summary>
/// <remarks>Not thread-safe: the counters run on the pump thread only.</remarks>
public sealed class UsageLedgerBuilder
{
    private readonly Dictionary<UsageKey, LedgerTokens> _entries = new();
    private UsageLedger? _snapshot = UsageLedger.Empty;

    public void Add(UsageKey key, LedgerTokens tokens)
    {
        if (tokens.IsZero) return;
        _entries[key] = _entries.TryGetValue(key, out var existing) ? existing + tokens : tokens;
        _snapshot = null;
    }

    public void Clear()
    {
        _entries.Clear();
        _snapshot = UsageLedger.Empty;
    }

    public UsageLedger ToLedger() => _snapshot ??= UsageLedger.From(_entries);
}

/// <summary>
/// Long-context thresholds (prompt tokens) the ledgers record: the only ones the Anthropic and OpenAI entries of the
/// price list use today. A threshold the list introduces later is ignored (and logged) until it is added here.
/// </summary>
public static class PricingThresholds
{
    public static IReadOnlyList<long> Known { get; } = [200_000, 272_000];

    /// <summary>The highest known threshold the prompt EXCEEDS (prompt &gt; threshold), 0 when none.</summary>
    public static long BandFor(long promptTokens)
    {
        long band = 0;
        foreach (var threshold in Known)
            if (promptTokens > threshold && threshold > band) band = threshold;
        return band;
    }
}
