using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Usage;

namespace AIUsageMonitor.Core.Sessions;

public enum CloudFetchStatus { Ok, NoCredentials, Unauthorized, Unavailable, Error }

/// <summary>
/// One read of the cloud sessions. <paramref name="RoutinesKnown"/> is false when the routines could not be read: the
/// runs already shown must then be kept, not taken for finished.
/// </summary>
public sealed record CloudFetchResult(CloudFetchStatus Status, IReadOnlyList<CloudSession> Sessions, bool RoutinesKnown, string? Detail = null)
{
    public static CloudFetchResult Failed(CloudFetchStatus status, string detail) => new(status, [], false, detail);
}

/// <summary>
/// Reads the Claude Code sessions of the account from the same endpoints the Claude Code CLI uses for
/// <c>--teleport</c> and for its routines tool: <c>GET /v1/code/sessions</c> and <c>GET /v1/code/triggers</c>, with the
/// OAuth token Claude Code keeps in <c>~/.claude/.credentials.json</c> (only read, like the quota provider does). The
/// routines call needs the organization of the account, read from <c>~/.claude.json</c>; without it only the sessions
/// are listed.
/// </summary>
public sealed class ClaudeCloudSessionsClient
{
    public static readonly Uri SessionsUri = new("https://api.anthropic.com/v1/code/sessions?limit=50");
    public static readonly Uri TriggersUri = new("https://api.anthropic.com/v1/code/triggers");
    public const string ApiVersion = "2023-06-01";
    public const string TriggersBeta = "ccr-triggers-2026-01-30";

    /// <summary>How long the organization read from <c>~/.claude.json</c> (a file that can be large) is reused.</summary>
    private static readonly TimeSpan OrganizationTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly AppPaths _paths;
    private readonly HttpClient _http;
    private readonly IClock _clock;
    private readonly string? _userAgent;
    private (string? Uuid, DateTimeOffset At)? _organization;

    public ClaudeCloudSessionsClient(AppPaths paths, HttpClient http, IClock clock, string? userAgent = null)
    {
        _paths = paths;
        _http = http;
        _clock = clock;
        _userAgent = userAgent;
    }

    public async Task<CloudFetchResult> FetchAsync(CancellationToken cancellationToken = default)
    {
        ClaudeCredentials? credentials;
        try
        {
            credentials = ClaudeUsageProvider.ReadCredentials(_paths.ClaudeCredentialsFile);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return CloudFetchResult.Failed(CloudFetchStatus.Error, "Credenziali Claude non leggibili");
        }
        if (credentials is null || credentials.ExpiresAt is { } expires && expires <= _clock.UtcNow)
            return CloudFetchResult.Failed(CloudFetchStatus.NoCredentials, ClaudeUsageProvider.TokenExpiredMessage);

        var (status, json, detail) = await GetAsync(SessionsUri, credentials.AccessToken, null, null, cancellationToken).ConfigureAwait(false);
        if (status != CloudFetchStatus.Ok) return CloudFetchResult.Failed(status, detail!);
        IReadOnlyList<CloudSession> sessions;
        try { sessions = CloudSessionParser.ParseSessions(json!); }
        catch (JsonException) { return CloudFetchResult.Failed(CloudFetchStatus.Error, "Risposta delle sessioni cloud non valida"); }

        if (Organization() is not { } organization)
            return new CloudFetchResult(CloudFetchStatus.Ok, sessions, false, "routine non lette: organizzazione sconosciuta");
        var (routineStatus, routineJson, routineDetail) =
            await GetAsync(TriggersUri, credentials.AccessToken, TriggersBeta, organization, cancellationToken).ConfigureAwait(false);
        if (routineStatus != CloudFetchStatus.Ok)
            return new CloudFetchResult(CloudFetchStatus.Ok, sessions, false, $"routine non lette: {routineDetail}");
        try
        {
            return new CloudFetchResult(CloudFetchStatus.Ok, CloudSessionParser.Merge(sessions, CloudSessionParser.ParseRoutineRuns(routineJson!)), true);
        }
        catch (JsonException)
        {
            return new CloudFetchResult(CloudFetchStatus.Ok, sessions, false, "routine non lette: risposta non valida");
        }
    }

    private async Task<(CloudFetchStatus Status, string? Json, string? Detail)> GetAsync(Uri uri, string token, string? beta,
        string? organization, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
        if (beta is not null) request.Headers.TryAddWithoutValidation("anthropic-beta", beta);
        if (organization is not null) request.Headers.TryAddWithoutValidation("x-organization-uuid", organization);
        if (_userAgent is not null) request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (CloudFetchStatus.Unauthorized, null, $"HTTP {(int)response.StatusCode}");
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
                return (CloudFetchStatus.Unavailable, null, $"HTTP {(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode) return (CloudFetchStatus.Error, null, $"HTTP {(int)response.StatusCode}");
            return (CloudFetchStatus.Ok, await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return (CloudFetchStatus.Error, null, "Rete non disponibile");
        }
    }

    /// <summary><c>oauthAccount.organizationUuid</c> of <c>~/.claude.json</c>, cached; null when it cannot be read.</summary>
    private string? Organization()
    {
        var now = _clock.UtcNow;
        if (_organization is { } cached && now - cached.At < OrganizationTtl) return cached.Uuid;
        string? uuid = null;
        try
        {
            if (File.Exists(_paths.ClaudeGlobalConfigFile))
            {
                using var stream = new FileStream(_paths.ClaudeGlobalConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("oauthAccount", out var account) && account.ValueKind == JsonValueKind.Object
                    && account.TryGetProperty("organizationUuid", out var org) && org.ValueKind == JsonValueKind.String)
                    uuid = org.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unknown organization: the routines are skipped until the next read.
        }
        _organization = (string.IsNullOrWhiteSpace(uuid) ? null : uuid, now);
        return _organization.Value.Uuid;
    }
}
