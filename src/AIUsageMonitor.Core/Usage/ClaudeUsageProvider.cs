using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Usage;

public sealed record ClaudeCredentials(string AccessToken, DateTimeOffset? ExpiresAt, string? PlanLabel);

public sealed class ClaudeUsageProvider : IUsageProvider
{
    public static readonly Uri UsageUri = new("https://api.anthropic.com/api/oauth/usage");
    public const string TokenExpiredMessage = "Apri Claude Code per rinnovare la sessione";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly IClock _clock;

    public ClaudeUsageProvider(AppPaths paths, HttpClient http, IClock clock)
    {
        _paths = paths;
        _http = http;
        _clock = clock;
    }

    public AgentKind Agent => AgentKind.Claude;

    public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;

        ClaudeCredentials? credentials;
        try
        {
            credentials = ReadCredentials(_paths.ClaudeCredentialsFile);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return UsageSnapshot.Empty(Agent, UsageStatus.Error, "Credenziali Claude non leggibili", now);
        }

        if (credentials is null)
            return UsageSnapshot.Empty(Agent, UsageStatus.TokenExpired, TokenExpiredMessage, now);
        if (credentials.ExpiresAt is { } expiresAt && expiresAt <= now)
            return UsageSnapshot.Empty(Agent, UsageStatus.TokenExpired, TokenExpiredMessage, now);

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return UsageSnapshot.Empty(Agent, UsageStatus.TokenExpired, TokenExpiredMessage, now);
            if (!response.IsSuccessStatusCode)
                return UsageSnapshot.Empty(Agent, UsageStatus.Error, $"HTTP {(int)response.StatusCode}", now);

            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseResponse(json, credentials.PlanLabel, now);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            return UsageSnapshot.Empty(Agent, UsageStatus.Error, "Rete non disponibile", now);
        }
    }

    /// <summary>Reads the OAuth block of ~/.claude/.credentials.json. Returns null when the file or token is missing.</summary>
    public static ClaudeCredentials? ReadCredentials(string path)
    {
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return null;

        var token = oauth.TryGetProperty("accessToken", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(token)) return null;

        DateTimeOffset? expires = oauth.TryGetProperty("expiresAt", out var e) && e.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64())
            : null;
        var subscription = oauth.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var tier = oauth.TryGetProperty("rateLimitTier", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        return new ClaudeCredentials(token, expires, PlanLabelFrom(subscription, tier));
    }

    /// <summary>"max" + "default_claude_max_20x" => "Max 20x"; "pro" + "default_claude_pro" => "Pro".</summary>
    public static string? PlanLabelFrom(string? subscriptionType, string? rateLimitTier)
    {
        if (string.IsNullOrWhiteSpace(subscriptionType)) return null;
        var label = char.ToUpperInvariant(subscriptionType[0]) + subscriptionType[1..];
        var match = Regex.Match(rateLimitTier ?? string.Empty, @"_(\d+)x$");
        return match.Success ? $"{label} {match.Groups[1].Value}x" : label;
    }

    public static UsageSnapshot ParseResponse(string json, string? planLabel, DateTimeOffset now)
    {
        var dto = JsonSerializer.Deserialize<ClaudeUsageResponse>(json, JsonOptions) ?? throw new JsonException("Empty usage response");

        var windows = new List<UsageWindow>();
        if (dto.FiveHour is { } fiveHour) windows.Add(ToWindow("5h", fiveHour.Utilization, fiveHour.ResetsAt, null));
        if (dto.SevenDay is { } sevenDay) windows.Add(ToWindow("7g", sevenDay.Utilization, sevenDay.ResetsAt, null));
        foreach (var limit in dto.Limits ?? [])
        {
            if (limit.Kind != "weekly_scoped") continue;
            var model = limit.Scope?.Model?.DisplayName;
            if (string.IsNullOrWhiteSpace(model)) continue;
            windows.Add(ToWindow($"7g {model}", limit.Percent, limit.ResetsAt, limit.Severity));
        }

        string? extra = null;
        if (dto.ExtraUsage is { IsEnabled: true, UsedCredits: > 0 } eu)
        {
            var scale = Math.Pow(10, eu.DecimalPlaces ?? 2);
            var used = (eu.UsedCredits ?? 0) / scale;
            var limit = (eu.MonthlyLimit ?? 0) / scale;
            extra = string.Create(CultureInfo.InvariantCulture, $"Extra {used:0.##}/{limit:0.##} {eu.Currency}").TrimEnd();
        }

        return new UsageSnapshot(AgentKind.Claude, windows, planLabel, extra, UsageStatus.Ok, null, now);
    }

    private static UsageWindow ToWindow(string label, double? percent, string? resetsAt, string? severity)
    {
        var pct = Math.Clamp(percent ?? 0, 0, 100);
        DateTimeOffset? reset = DateTimeOffset.TryParse(resetsAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return new UsageWindow(label, pct, reset, SeverityRules.FromApi(severity, pct));
    }
}

internal sealed class ClaudeUsageResponse
{
    [JsonPropertyName("five_hour")] public ClaudeWindowDto? FiveHour { get; set; }
    [JsonPropertyName("seven_day")] public ClaudeWindowDto? SevenDay { get; set; }
    [JsonPropertyName("limits")] public List<ClaudeLimitDto>? Limits { get; set; }
    [JsonPropertyName("extra_usage")] public ClaudeExtraUsageDto? ExtraUsage { get; set; }
}

internal sealed class ClaudeWindowDto
{
    [JsonPropertyName("utilization")] public double? Utilization { get; set; }
    [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
}

internal sealed class ClaudeLimitDto
{
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("percent")] public double? Percent { get; set; }
    [JsonPropertyName("severity")] public string? Severity { get; set; }
    [JsonPropertyName("resets_at")] public string? ResetsAt { get; set; }
    [JsonPropertyName("scope")] public ClaudeScopeDto? Scope { get; set; }
}

internal sealed class ClaudeScopeDto
{
    [JsonPropertyName("model")] public ClaudeScopeModelDto? Model { get; set; }
}

internal sealed class ClaudeScopeModelDto
{
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
}

internal sealed class ClaudeExtraUsageDto
{
    [JsonPropertyName("is_enabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("monthly_limit")] public double? MonthlyLimit { get; set; }
    [JsonPropertyName("used_credits")] public double? UsedCredits { get; set; }
    [JsonPropertyName("currency")] public string? Currency { get; set; }
    [JsonPropertyName("decimal_places")] public int? DecimalPlaces { get; set; }
}
