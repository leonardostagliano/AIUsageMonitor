using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Usage;

public sealed class CodexUsageProvider : IUsageProvider
{
    public const string NoDataMessage = "Nessuna sessione Codex recente";
    public const string ReadErrorMessage = "Errore lettura sessioni Codex";

    // Accepted range of DateTimeOffset.FromUnixTimeSeconds; out-of-range values (e.g. milliseconds) are ignored.
    private const long MinUnixSeconds = -62135596800L;
    private const long MaxUnixSeconds = 253402300799L;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AppPaths _paths;
    private readonly IClock _clock;

    public CodexUsageProvider(AppPaths paths, IClock clock)
    {
        _paths = paths;
        _clock = clock;
    }

    public AgentKind Agent => AgentKind.Codex;

    public int MaxFilesToScan { get; init; } = 200;
    public int MaxLinesPerFile { get; init; } = 5000;
    public TimeSpan LookBack { get; init; } = TimeSpan.FromDays(7);

    public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Fetch(cancellationToken), cancellationToken);

    private UsageSnapshot Fetch(CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        try
        {
            return FetchCore(now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return UsageSnapshot.Empty(Agent, UsageStatus.Error, ReadErrorMessage, now);
        }
    }

    private UsageSnapshot FetchCore(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dir = _paths.CodexSessionsDir;
        if (!Directory.Exists(dir))
            return UsageSnapshot.Empty(Agent, UsageStatus.NoData, "Codex non trovato", now);

        var cutoff = (now - LookBack).UtcDateTime;
        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(dir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= cutoff)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFilesToScan)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return UsageSnapshot.Empty(Agent, UsageStatus.Error, "Cartella sessioni Codex non leggibile", now);
        }

        // Newest file first, so the first rate_limits seen for a limit_id is the most recent one.
        var latestByLimit = new Dictionary<string, CodexRateLimits>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CodexRateLimits? rateLimits;
            try
            {
                rateLimits = FindLatestRateLimits(ReverseLineReader.ReadLinesFromEnd(file.FullName).Take(MaxLinesPerFile));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (rateLimits is null) continue;
            latestByLimit.TryAdd(rateLimits.LimitId ?? "codex", rateLimits);
        }

        if (latestByLimit.Count == 0)
            return UsageSnapshot.Empty(Agent, UsageStatus.NoData, NoDataMessage, now);

        var windows = new List<UsageWindow>();
        string? plan = null;
        foreach (var (limitId, rl) in latestByLimit.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var prefix = latestByLimit.Count > 1 ? $"{limitId} " : string.Empty;
            if (rl.Primary is { } primary) windows.Add(ResolveWindow(primary, prefix, now));
            if (rl.Secondary is { } secondary) windows.Add(ResolveWindow(secondary, prefix, now));
            plan ??= PlanLabelFrom(rl.PlanType);
        }

        return new UsageSnapshot(Agent, windows, plan, null, UsageStatus.Ok, null, now);
    }

    /// <summary>Lines must be supplied newest-first; returns the first token_count record carrying rate_limits.</summary>
    public static CodexRateLimits? FindLatestRateLimits(IEnumerable<string> linesFromEnd)
    {
        foreach (var line in linesFromEnd)
        {
            if (!line.Contains("\"rate_limits\"", StringComparison.Ordinal)) continue;
            CodexRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<CodexRecord>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (record?.Type == "event_msg" && record.Payload?.Type == "token_count" && record.Payload.RateLimits is { } rl)
                return rl;
        }
        return null;
    }

    /// <summary>Turns a Codex rate window into a UsageWindow; a reset in the past means the window rolled over (percent 0, reset advanced).</summary>
    public static UsageWindow ResolveWindow(CodexRateWindow window, string labelPrefix, DateTimeOffset now)
    {
        var minutes = window.WindowMinutes ?? 0;
        var label = labelPrefix + LabelFor(minutes);
        var percent = Math.Clamp(window.UsedPercent ?? 0, 0, 100);
        DateTimeOffset? reset = window.ResetsAt is { } seconds && seconds is >= MinUnixSeconds and <= MaxUnixSeconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;

        if (reset is { } resetAt && resetAt <= now)
        {
            percent = 0;
            if (minutes > 0)
            {
                var step = TimeSpan.FromMinutes(minutes);
                while (resetAt <= now) resetAt += step;
                reset = resetAt;
            }
            else
            {
                reset = null;
            }
        }

        return new UsageWindow(label, percent, reset, SeverityRules.FromPercent(percent));
    }

    public static string LabelFor(int windowMinutes) => windowMinutes switch
    {
        <= 0 => "?",
        < 1440 => $"{Math.Max(1, (int)Math.Round(windowMinutes / 60.0))}h",
        _ => $"{Math.Max(1, (int)Math.Round(windowMinutes / 1440.0))}g"
    };

    public static string? PlanLabelFrom(string? planType) =>
        string.IsNullOrWhiteSpace(planType) ? null : char.ToUpperInvariant(planType[0]) + planType[1..];
}

public sealed class CodexRecord
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("payload")] public CodexPayload? Payload { get; set; }
}

public sealed class CodexPayload
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("rate_limits")] public CodexRateLimits? RateLimits { get; set; }
}

public sealed class CodexRateLimits
{
    [JsonPropertyName("limit_id")] public string? LimitId { get; set; }
    [JsonPropertyName("primary")] public CodexRateWindow? Primary { get; set; }
    [JsonPropertyName("secondary")] public CodexRateWindow? Secondary { get; set; }
    [JsonPropertyName("plan_type")] public string? PlanType { get; set; }
}

public sealed class CodexRateWindow
{
    [JsonPropertyName("used_percent")] public double? UsedPercent { get; set; }
    [JsonPropertyName("window_minutes")] public int? WindowMinutes { get; set; }
    [JsonPropertyName("resets_at")] public long? ResetsAt { get; set; }
}
