using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Tests.Helpers;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset now) => UtcNow = now;
    public DateTimeOffset UtcNow { get; set; }
    public void Advance(TimeSpan by) => UtcNow += by;
}
