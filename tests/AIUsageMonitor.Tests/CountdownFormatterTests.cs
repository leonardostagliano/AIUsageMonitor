using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Tests;

public class CountdownFormatterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "adesso")]
    [InlineData(-5, "adesso")]
    [InlineData(45, "45m")]
    [InlineData(130, "2h 10m")]
    [InlineData(60 * 24 * 3 + 240, "3g 4h")]
    [InlineData(60 * 24 * 7, "7g 0h")]
    public void Until(int minutes, string expected) => Assert.Equal(expected, CountdownFormatter.Until(Now.AddMinutes(minutes), Now));

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(42, "42s")]
    [InlineData(90, "1m")]
    [InlineData(3900, "1h 5m")]
    [InlineData(90000, "1g 1h")]
    public void Since(int seconds, string expected) => Assert.Equal(expected, CountdownFormatter.Since(Now.AddSeconds(-seconds), Now));
}
