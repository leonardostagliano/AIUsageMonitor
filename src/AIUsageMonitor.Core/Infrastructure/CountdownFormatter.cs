namespace AIUsageMonitor.Core.Infrastructure;

public static class CountdownFormatter
{
    /// <summary>Time until a reset: "adesso", "45m", "2h 10m", "3g 4h".</summary>
    public static string Until(DateTimeOffset target, DateTimeOffset now)
    {
        var remaining = target - now;
        if (remaining <= TimeSpan.Zero) return "adesso";
        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        if (minutes < 60) return $"{minutes}m";
        if (minutes < 24 * 60) return $"{minutes / 60}h {minutes % 60}m";
        var days = minutes / (24 * 60);
        var hours = (minutes % (24 * 60)) / 60;
        return $"{days}g {hours}h";
    }

    /// <summary>Time elapsed since an event: "42s", "1m", "1h 5m", "1g 1h".</summary>
    public static string Since(DateTimeOffset start, DateTimeOffset now)
    {
        var elapsed = now - start;
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var seconds = (int)elapsed.TotalSeconds;
        if (seconds < 60) return $"{seconds}s";
        var minutes = seconds / 60;
        if (minutes < 60) return $"{minutes}m";
        if (minutes < 24 * 60) return $"{minutes / 60}h {minutes % 60}m";
        return $"{minutes / (24 * 60)}g {(minutes % (24 * 60)) / 60}h";
    }
}
