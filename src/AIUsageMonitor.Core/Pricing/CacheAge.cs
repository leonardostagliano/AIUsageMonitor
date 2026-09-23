namespace AIUsageMonitor.Core.Pricing;

/// <summary>When a downloaded copy (price list, exchange rate) is due for a new download.</summary>
internal static class CacheAge
{
    /// <summary>How far in the future a copy may be dated before it counts as stale: small clock corrections only.</summary>
    public static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Older than <paramref name="maxAge"/>, or dated in the future: a copy written while the clock was ahead, or a
    /// damaged or hand-edited file, would otherwise block every download, silently, until that date.
    /// </summary>
    public static bool IsStale(DateTimeOffset fetchedAt, DateTimeOffset now, TimeSpan maxAge) =>
        now - fetchedAt >= maxAge || fetchedAt - now > FutureTolerance;
}
