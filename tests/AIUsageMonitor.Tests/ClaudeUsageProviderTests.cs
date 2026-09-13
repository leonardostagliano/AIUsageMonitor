using System.Net;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Usage;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ClaudeUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 1, 0, 0, TimeSpan.Zero);
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static string Credentials(long expiresAtMs, string sub = "max", string tier = "default_claude_max_20x") =>
        $$$"""{"claudeAiOauth":{"accessToken":"sk-ant-test-token","refreshToken":"r","expiresAt":{{{expiresAtMs}}},"subscriptionType":"{{{sub}}}","rateLimitTier":"{{{tier}}}"},"mcpOAuth":{}}""";

    private static (ClaudeUsageProvider Provider, FakeHttpMessageHandler Http) Build(TempDir dir, FakeHttpMessageHandler http, string? credentialsJson)
    {
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        if (credentialsJson is not null) dir.File(@".claude\.credentials.json", credentialsJson);
        return (new ClaudeUsageProvider(paths, new HttpClient(http), new FakeClock(Now)), http);
    }

    [Fact]
    public void ParseResponse_maps_windows_plan_and_extra_usage()
    {
        var snap = ClaudeUsageProvider.ParseResponse(Fixture("claude-usage.json"), "Max 20x", Now);

        Assert.Equal(AgentKind.Claude, snap.Agent);
        Assert.Equal(UsageStatus.Ok, snap.Status);
        Assert.Equal("Max 20x", snap.PlanLabel);
        Assert.Equal("Extra 12.5/170 EUR", snap.ExtraUsage);
        Assert.Collection(snap.Windows,
            w => { Assert.Equal("5h", w.Label); Assert.Equal(48, w.Percent); Assert.Equal(new DateTimeOffset(2026, 9, 13, 2, 20, 0, 844, TimeSpan.Zero).AddTicks(2170), w.ResetsAt); Assert.Equal(Severity.Normal, w.Severity); },
            w => { Assert.Equal("7g", w.Label); Assert.Equal(31, w.Percent); Assert.Equal(Severity.Normal, w.Severity); },
            w => { Assert.Equal("7g Fable", w.Label); Assert.Equal(84, w.Percent); Assert.Equal(Severity.Critical, w.Severity); });
    }

    [Fact]
    public void ParseResponse_hides_extra_usage_when_unused()
    {
        var json = Fixture("claude-usage.json").Replace("\"used_credits\": 1250", "\"used_credits\": 0");
        Assert.Null(ClaudeUsageProvider.ParseResponse(json, null, Now).ExtraUsage);
    }

    [Theory]
    [InlineData("max", "default_claude_max_20x", "Max 20x")]
    [InlineData("pro", "default_claude_pro", "Pro")]
    [InlineData(null, "x", null)]
    public void PlanLabel_from_credentials(string? sub, string tier, string? expected) =>
        Assert.Equal(expected, ClaudeUsageProvider.PlanLabelFrom(sub, tier));

    [Fact]
    public async Task Fetch_sends_bearer_token_and_beta_header()
    {
        using var dir = new TempDir();
        var (provider, http) = Build(dir, FakeHttpMessageHandler.Json(HttpStatusCode.OK, Fixture("claude-usage.json")), Credentials(Now.AddHours(1).ToUnixTimeMilliseconds()));

        var snap = await provider.FetchAsync();

        var req = Assert.Single(http.Requests);
        Assert.Equal(ClaudeUsageProvider.UsageUri, req.RequestUri);
        Assert.Equal("Bearer sk-ant-test-token", req.Headers.Authorization!.ToString());
        Assert.Equal("oauth-2025-04-20", req.Headers.GetValues("anthropic-beta").Single());
        Assert.Equal(UsageStatus.Ok, snap.Status);
        Assert.Equal("Max 20x", snap.PlanLabel);
        Assert.Equal(3, snap.Windows.Count);
    }

    [Fact]
    public async Task Fetch_reports_token_expired_without_calling_the_api()
    {
        using var dir = new TempDir();
        var (provider, http) = Build(dir, FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"), Credentials(Now.AddMinutes(-1).ToUnixTimeMilliseconds()));

        var snap = await provider.FetchAsync();

        Assert.Empty(http.Requests);
        Assert.Equal(UsageStatus.TokenExpired, snap.Status);
        Assert.Equal("Apri Claude Code per rinnovare la sessione", snap.StatusMessage);
    }

    [Fact]
    public async Task Fetch_reports_token_expired_on_401()
    {
        using var dir = new TempDir();
        var (provider, _) = Build(dir, FakeHttpMessageHandler.Json(HttpStatusCode.Unauthorized, "{}"), Credentials(Now.AddHours(1).ToUnixTimeMilliseconds()));
        var snap = await provider.FetchAsync();
        Assert.Equal(UsageStatus.TokenExpired, snap.Status);
    }

    [Fact]
    public async Task Fetch_reports_missing_credentials_and_network_errors()
    {
        using var dir = new TempDir();
        var (noCreds, _) = Build(dir, FakeHttpMessageHandler.Json(HttpStatusCode.OK, "{}"), null);
        Assert.Equal(UsageStatus.TokenExpired, (await noCreds.FetchAsync()).Status);

        using var dir2 = new TempDir();
        var (offline, _) = Build(dir2, FakeHttpMessageHandler.Throws(new HttpRequestException("no network")), Credentials(Now.AddHours(1).ToUnixTimeMilliseconds()));
        var snap = await offline.FetchAsync();
        Assert.Equal(UsageStatus.Error, snap.Status);
        Assert.Equal("Rete non disponibile", snap.StatusMessage);
    }

    [Fact]
    public async Task Fetch_reports_http_errors_with_status_code()
    {
        using var dir = new TempDir();
        var (provider, _) = Build(dir, FakeHttpMessageHandler.Json(HttpStatusCode.InternalServerError, "oops"), Credentials(Now.AddHours(1).ToUnixTimeMilliseconds()));
        var snap = await provider.FetchAsync();
        Assert.Equal(UsageStatus.Error, snap.Status);
        Assert.Equal("HTTP 500", snap.StatusMessage);
    }
}
