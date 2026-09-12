namespace AIUsageMonitor.Core.Models;

public enum Severity { Normal, Warning, Critical }

public enum UsageStatus { Ok, Stale, TokenExpired, NoData, Error }

/// <summary>One rate-limit window ("5h", "7g", "7g Fable"). Percent is 0..100.</summary>
public sealed record UsageWindow(string Label, double Percent, DateTimeOffset? ResetsAt, Severity Severity);

public sealed record UsageSnapshot(
    AgentKind Agent,
    IReadOnlyList<UsageWindow> Windows,
    string? PlanLabel,
    string? ExtraUsage,
    UsageStatus Status,
    string? StatusMessage,
    DateTimeOffset FetchedAt)
{
    public static UsageSnapshot Empty(AgentKind agent, UsageStatus status, string? message, DateTimeOffset now) =>
        new(agent, Array.Empty<UsageWindow>(), null, null, status, message, now);
}

public static class SeverityRules
{
    public static Severity FromPercent(double percent) =>
        percent >= 80 ? Severity.Critical : percent >= 50 ? Severity.Warning : Severity.Normal;

    /// <summary>Combines the API severity string (if any) with the percent thresholds; the worse one wins.</summary>
    public static Severity FromApi(string? apiSeverity, double percent)
    {
        var fromPercent = FromPercent(percent);
        var fromApi = apiSeverity?.Trim().ToLowerInvariant() switch
        {
            "critical" or "exceeded" or "blocked" => Severity.Critical,
            "warning" or "warn" or "high" => Severity.Warning,
            _ => Severity.Normal
        };
        return (Severity)Math.Max((int)fromPercent, (int)fromApi);
    }
}
