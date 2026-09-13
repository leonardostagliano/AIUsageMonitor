using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class FileLoggerTests
{
    [Fact]
    public void Writes_daily_file_and_prunes_old_ones()
    {
        using var dir = new TempDir();
        var logs = dir.Sub("logs");
        var now = new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
        File.WriteAllText(Path.Combine(logs, "app-20260901.log"), "old");
        File.WriteAllText(Path.Combine(logs, "app-20260910.log"), "recent");

        var logger = new FileLogger(logs, new FakeClock(now));
        logger.Info("hello");
        logger.Error("bad", new InvalidOperationException("boom"));

        Assert.False(File.Exists(Path.Combine(logs, "app-20260901.log")));
        Assert.True(File.Exists(Path.Combine(logs, "app-20260910.log")));
        var today = File.ReadAllText(Path.Combine(logs, $"app-{now.ToLocalTime():yyyyMMdd}.log"));
        Assert.Contains("[INFO] hello", today);
        Assert.Contains("[ERROR] bad: System.InvalidOperationException: boom", today);
    }
}
