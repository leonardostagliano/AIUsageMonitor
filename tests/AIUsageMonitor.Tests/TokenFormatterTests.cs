using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Tests;

public class TokenFormatterTests
{
    [Theory]
    [InlineData(950, "950")]
    [InlineData(12_345, "12,3k")]
    [InlineData(1_400_000, "1,4M")]
    [InlineData(80_020_587, "80,0M")]
    [InlineData(1_000, "1,0k")]
    public void Compact_formats_with_italian_decimal_comma(long tokens, string expected) =>
        Assert.Equal(expected, TokenFormatter.Compact(tokens));

    [Fact]
    public void Breakdown_formats_all_four_buckets()
    {
        var usage = new TokenUsage(11_000, 434_900, 26_400_000, 2_700_000);
        Assert.Equal("↑ 29,1M · ↓ 434,9k", TokenFormatter.InputOutput(usage));
        var detail = TokenFormatter.Breakdown(usage);
        Assert.Contains("Input senza cache 11,0k", detail);
        Assert.Contains("Cache inclusa nell'input: 26,4M letti / 2,7M scritti", detail);
        Assert.Contains("cumulativi della conversazione", detail);
    }
}
