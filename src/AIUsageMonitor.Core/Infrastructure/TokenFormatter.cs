using System.Globalization;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Infrastructure;

public static class TokenFormatter
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    /// <summary>Compact token count: "950", "12,3k", "1,4M", "80,0M" (Italian decimal comma).</summary>
    public static string Compact(long tokens)
    {
        if (tokens < 1_000) return tokens.ToString(CultureInfo.InvariantCulture);
        if (tokens < 1_000_000) return string.Create(Italian, $"{tokens / 1000.0:0.0}k");
        return string.Create(Italian, $"{tokens / 1_000_000.0:0.0}M");
    }

    /// <summary>Breakdown tooltip: "in 11,0k · out 434,9k · cache 26,4M letti / 2,7M scritti".</summary>
    public static string Breakdown(TokenUsage u) =>
        $"in {Compact(u.Input)} · out {Compact(u.Output)} · cache {Compact(u.CacheRead)} letti / {Compact(u.CacheWrite)} scritti";
}
