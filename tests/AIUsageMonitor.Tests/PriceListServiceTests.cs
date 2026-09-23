using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AIUsageMonitor.Core.Pricing;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class PriceListServiceTests
{
    private static readonly string FullList = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "litellm-prices.json"));

    // Built as a JsonObject: an interpolation hole right before the closing braces of a raw string does not compile.
    private static string ListFile(DateTimeOffset fetchedAt, string models, string? etag = null) =>
        new JsonObject
        {
            ["fetchedAt"] = fetchedAt.ToString("O"),
            ["etag"] = etag,
            ["source"] = "test",
            ["models"] = JsonNode.Parse(models)
        }.ToJsonString();

    private static Stream SnapshotStream() => new MemoryStream(Encoding.UTF8.GetBytes(ListFile(
        new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
        """{"claude-opus-5-5":{"input_cost_per_token":0.000004,"output_cost_per_token":0.00002,"litellm_provider":"anthropic"}}""")));

    private static PriceListService Service(TempDir dir, FakeHttpMessageHandler handler, ManualTimeProvider time, List<string>? errors = null) =>
        new(new PriceListServiceOptions
        {
            CacheFile = Path.Combine(dir.Path, "prices-cache.json"),
            OverrideFile = Path.Combine(dir.Path, "prices-override.json"),
            Http = new HttpClient(handler),
            Time = time,
            Snapshot = SnapshotStream,
            LogError = (message, _) => errors?.Add(message)
        });

    private static HttpResponseMessage Ok(string body, string? etag = "\"v1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        return response;
    }

    [Fact]
    public void Without_a_cache_the_embedded_snapshot_is_used_and_the_list_is_stale()
    {
        using var dir = new TempDir();
        var service = Service(dir, new FakeHttpMessageHandler(_ => throw new InvalidOperationException("no network")), new ManualTimeProvider());

        service.LoadLocal();

        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Origin);
        Assert.NotNull(service.Current.Find("claude-opus-5-5"));
        Assert.True(service.IsStale);
    }

    [Fact]
    public async Task A_download_is_trimmed_cached_and_published()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var handler = new FakeHttpMessageHandler(_ => Ok(FullList));
        var service = Service(dir, handler, time);
        service.LoadLocal();
        var changed = 0;
        service.Changed += () => changed++;

        await service.RefreshIfStaleAsync();

        Assert.Equal(PriceListOrigin.Downloaded, service.Current.Origin);
        Assert.Equal(6, service.Current.Count);
        Assert.Equal(time.GetUtcNow(), service.Current.FetchedAt);
        Assert.False(service.IsStale);
        Assert.Null(service.LastError);
        Assert.Equal(1, changed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(PriceListServiceOptions.DefaultSourceUrl, request.RequestUri);
        Assert.Contains("AIUsageMonitor", request.Headers.UserAgent.ToString());
        var cached = JsonNode.Parse(File.ReadAllText(Path.Combine(dir.Path, "prices-cache.json")))!;
        Assert.Equal("\"v1\"", (string?)cached["etag"]);
        Assert.Null(cached["models"]!["gemini-9-pro"]);

        await service.RefreshIfStaleAsync(); // fresh: no second request
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_stale_cache_is_revalidated_with_its_etag_and_a_304_renews_it()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        File.WriteAllText(Path.Combine(dir.Path, "prices-cache.json"), ListFile(time.GetUtcNow().AddHours(-25),
            """{"gpt-6-luna":{"input_cost_per_token":1e-7,"output_cost_per_token":5e-7,"litellm_provider":"openai"}}""", "\"v7\""));
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var service = Service(dir, handler, time);
        service.LoadLocal();
        Assert.Equal(PriceListOrigin.Downloaded, service.Current.Origin);
        Assert.True(service.IsStale);

        await service.RefreshIfStaleAsync();

        Assert.Equal("\"v7\"", Assert.Single(handler.Requests).Headers.IfNoneMatch.Single().ToString());
        Assert.False(service.IsStale);
        Assert.Equal(time.GetUtcNow(), service.Current.FetchedAt);
        Assert.NotNull(service.Current.Find("gpt-6-luna"));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"claude-opus-5-5":{"input_cost_per_token":1,"output_cost_per_token":1,"litellm_provider":"anthropic","mode":"chat"}}""")]
    public async Task A_failed_or_incomplete_download_keeps_the_current_list_and_records_the_error(HttpStatusCode status, string body)
    {
        using var dir = new TempDir();
        var errors = new List<string>();
        var service = Service(dir, new FakeHttpMessageHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) }), new ManualTimeProvider(), errors);
        service.LoadLocal();
        var before = service.Current;

        await service.RefreshIfStaleAsync();

        Assert.Same(before, service.Current);
        Assert.NotNull(service.LastError);
        Assert.Single(errors);
        Assert.False(File.Exists(Path.Combine(dir.Path, "prices-cache.json")));
    }

    [Fact]
    public void The_override_adds_models_and_replaces_single_fields()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "prices-override.json"), """
            {
              "claude-opus-5-5": { "output_cost_per_token": 0.00003 },
              "codex-auto-review": { "input_cost_per_token": 1e-7, "output_cost_per_token": 5e-7 }
            }
            """);
        var service = Service(dir, new FakeHttpMessageHandler(_ => throw new InvalidOperationException()), new ManualTimeProvider());

        service.LoadLocal();

        Assert.True(service.Current.OverrideActive);
        Assert.Equal(0.000004m, service.Current.Find("claude-opus-5-5")!.Standard.Base.Input);
        Assert.Equal(0.00003m, service.Current.Find("claude-opus-5-5")!.Standard.Base.Output);
        Assert.Equal(0.0000001m, service.Current.Find("codex-auto-review")!.Standard.Base.Input);
    }

    [Fact]
    public void A_malformed_override_is_ignored_and_logged_once()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "prices-override.json"), "{ not json");
        var errors = new List<string>();
        var service = Service(dir, new FakeHttpMessageHandler(_ => throw new InvalidOperationException()), new ManualTimeProvider(), errors);

        service.LoadLocal();
        service.LoadLocal();

        Assert.False(service.Current.OverrideActive);
        Assert.NotNull(service.Current.Find("claude-opus-5-5"));
        Assert.Single(errors);
    }

    [Fact]
    public void The_embedded_snapshot_is_present_and_lists_both_vendors()
    {
        using var stream = PriceListService.EmbeddedSnapshot();
        Assert.NotNull(stream);
        using var dir = new TempDir();
        var service = new PriceListService(new PriceListServiceOptions
        {
            CacheFile = Path.Combine(dir.Path, "c.json"),
            OverrideFile = Path.Combine(dir.Path, "o.json"),
            Http = new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException()))
        });

        service.LoadLocal();

        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Origin);
        Assert.NotNull(service.Current.Find("claude-opus-5-5"));
        Assert.NotNull(service.Current.Find("gpt-6-luna"));
    }
}
