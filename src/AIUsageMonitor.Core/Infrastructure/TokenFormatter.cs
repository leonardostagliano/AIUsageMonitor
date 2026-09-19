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

    /// <summary>Input includes cache, matching the total processed by the provider.</summary>
    public static string InputOutput(TokenUsage u) => $"↑ {Compact(u.TotalInput)} · ↓ {Compact(u.Output)}";

    /// <summary>Distinguishes processed input from the cache buckets that make long sessions grow quickly.</summary>
    public static string Breakdown(TokenUsage u) =>
        $"Token cumulativi della conversazione\n↑ Input {Compact(u.TotalInput)} · ↓ Output {Compact(u.Output)}\n" +
        $"Input senza cache {Compact(u.Input)}\nCache inclusa nell'input: {Compact(u.CacheRead)} letti / {Compact(u.CacheWrite)} scritti\n" +
        "Il contesto riletto a ogni richiesta viene contato di nuovo.";
}
