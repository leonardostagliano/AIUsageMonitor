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

    private static PriceListService Service(TempDir dir, HttpMessageHandler handler, ManualTimeProvider time, List<string>? errors = null,
        long maxBytes = 20L * 1024 * 1024) =>
        new(new PriceListServiceOptions
        {
            CacheFile = Path.Combine(dir.Path, "prices-cache.json"),
            OverrideFile = Path.Combine(dir.Path, "prices-override.json"),
            Http = new HttpClient(handler),
            Time = time,
            MaxBytes = maxBytes,
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

        // The cache written to disk is what the next start reads back, ETag included.
        var revalidation = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotModified));
        var reloaded = Service(dir, revalidation, time);
        reloaded.LoadLocal();
        Assert.Equal(PriceListOrigin.Downloaded, reloaded.Current.Origin);
        Assert.Equal(6, reloaded.Current.Count);
        Assert.Equal(time.GetUtcNow(), reloaded.Current.FetchedAt);
        Assert.False(reloaded.IsStale);
        time.Advance(TimeSpan.FromHours(25));
        await reloaded.RefreshIfStaleAsync();
        Assert.Equal("\"v1\"", Assert.Single(revalidation.Requests).Headers.IfNoneMatch.Single().ToString());
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

        // The renewal is on disk too: the next start does not download again.
        var reloaded = Service(dir, new FakeHttpMessageHandler(_ => throw new InvalidOperationException("no network")), time);
        reloaded.LoadLocal();
        Assert.Equal(PriceListOrigin.Downloaded, reloaded.Current.Origin);
        Assert.Equal(time.GetUtcNow(), reloaded.Current.FetchedAt);
        Assert.False(reloaded.IsStale);
        Assert.NotNull(reloaded.Current.Find("gpt-6-luna"));
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
        var changed = 0;
        service.Changed += () => changed++;

        await service.RefreshIfStaleAsync();

        Assert.Same(before, service.Current);
        Assert.NotNull(service.LastError);
        Assert.Single(errors);
        Assert.Equal(1, changed);
        Assert.False(File.Exists(Path.Combine(dir.Path, "prices-cache.json")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_list_over_the_size_limit_is_refused_with_or_without_a_content_length(bool sized)
    {
        using var dir = new TempDir();
        var bytes = Encoding.UTF8.GetBytes(FullList);
        HttpContent Content() => sized ? new ByteArrayContent(bytes) : new UnsizedContent(bytes);
        Assert.Equal(sized, Content().Headers.ContentLength is not null);
        var errors = new List<string>();
        var service = Service(dir, new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Content() }),
            new ManualTimeProvider(), errors, maxBytes: bytes.Length - 1);
        service.LoadLocal();
        var before = service.Current;

        await service.RefreshIfStaleAsync();

        Assert.Same(before, service.Current);
        Assert.Equal("listino oltre il limite di dimensione", service.LastError);
        Assert.Single(errors);
        Assert.False(File.Exists(Path.Combine(dir.Path, "prices-cache.json")));

        // The same body within the limit is accepted: the limit, not the content, refused it.
        var accepting = Service(dir, new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = Content() }),
            new ManualTimeProvider(), maxBytes: bytes.Length);
        await accepting.RefreshIfStaleAsync();
        Assert.Null(accepting.LastError);
        Assert.Equal(PriceListOrigin.Downloaded, accepting.Current.Origin);
    }

    [Fact]
    public async Task A_download_that_outlasts_the_timeout_is_abandoned_as_a_timeout()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHttpMessageHandler(async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var errors = new List<string>();
        var service = Service(dir, handler, time, errors);
        service.LoadLocal();
        var before = service.Current;

        var refresh = service.RefreshIfStaleAsync();
        await entered.Task;
        time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(refresh.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(1));
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("timeout", service.LastError);
        Assert.Same(before, service.Current);
        Assert.Single(errors);
    }

    [Fact]
    public async Task A_success_after_a_failure_clears_the_error()
    {
        using var dir = new TempDir();
        var responses = new Queue<HttpResponseMessage>([new HttpResponseMessage(HttpStatusCode.InternalServerError), Ok(FullList)]);
        var service = Service(dir, new FakeHttpMessageHandler(_ => responses.Dequeue()), new ManualTimeProvider());
        service.LoadLocal();
        var changed = 0;
        service.Changed += () => changed++;

        await service.RefreshIfStaleAsync();
        Assert.Equal("HTTP 500", service.LastError);
        Assert.Equal(1, changed);
        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Origin);

        await service.RefreshIfStaleAsync();
        Assert.Null(service.LastError);
        Assert.Equal(2, changed);
        Assert.Equal(PriceListOrigin.Downloaded, service.Current.Origin);
    }

    [Fact]
    public async Task A_throwing_subscriber_is_logged_and_does_not_stop_the_others()
    {
        using var dir = new TempDir();
        var errors = new List<string>();
        var service = Service(dir, new FakeHttpMessageHandler(_ => Ok(FullList)), new ManualTimeProvider(), errors);
        var changed = 0;
        service.Changed += () => throw new InvalidOperationException("broken subscriber");
        service.Changed += () => changed++;

        service.LoadLocal();
        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Origin);
        Assert.Equal(1, changed);

        await service.RefreshIfStaleAsync();
        Assert.Equal(PriceListOrigin.Downloaded, service.Current.Origin);
        Assert.Null(service.LastError);
        Assert.False(service.IsStale);
        Assert.Equal(2, changed);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, message => Assert.Equal("Listino prezzi: un ascoltatore ha sollevato un'eccezione", message));
    }

    [Fact]
    public async Task Only_one_download_runs_at_a_time()
    {
        using var dir = new TempDir();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AsyncHttpMessageHandler((_, _) =>
        {
            entered.TrySetResult();
            return release.Task;
        });
        var service = Service(dir, handler, new ManualTimeProvider());
        service.LoadLocal();

        var first = service.RefreshIfStaleAsync();
        await entered.Task;
        var second = service.RefreshIfStaleAsync();
        Assert.True(second.IsCompleted);
        release.SetResult(Ok(FullList));
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, handler.Requests);
        Assert.Equal(PriceListOrigin.Downloaded, service.Current.Origin);
    }

    [Fact]
    public async Task A_cancelled_refresh_returns_without_a_request_or_an_error()
    {
        using var dir = new TempDir();
        var handler = new FakeHttpMessageHandler(_ => Ok(FullList));
        var service = Service(dir, handler, new ManualTimeProvider());
        service.LoadLocal();

        await service.RefreshIfStaleAsync(new CancellationToken(canceled: true));

        Assert.Empty(handler.Requests);
        Assert.Null(service.LastError);
        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Origin);
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

    [Theory]
    // The same model twice.
    [InlineData("""{"claude-opus-5-5":{"output_cost_per_token":0.00003},"claude-opus-5-5":{"input_cost_per_token":1e-6}}""")]
    // The same field twice, after an entry that would otherwise be applied.
    [InlineData("""{"codex-auto-review":{"input_cost_per_token":1e-7,"output_cost_per_token":5e-7},"claude-opus-5-5":{"output_cost_per_token":0.00003,"output_cost_per_token":0.00004}}""")]
    // The same key twice deeper down.
    [InlineData("""{"claude-opus-5-5":{"output_cost_per_token":0.00003,"provider_specific_entry":{"eu":1.1,"eu":1.2}}}""")]
    public async Task An_override_with_a_duplicate_key_is_ignored_whole_and_logged_once(string text)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "prices-override.json"), text);
        var errors = new List<string>();
        var service = Service(dir, new FakeHttpMessageHandler(_ => Ok(FullList)), new ManualTimeProvider(), errors);

        service.LoadLocal();
        service.LoadLocal();

        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Origin);
        Assert.False(service.Current.OverrideActive);
        var opus = service.Current.Find("claude-opus-5-5");
        Assert.NotNull(opus);
        Assert.Equal(0.000004m, opus.Standard.Base.Input);
        Assert.Equal(0.00002m, opus.Standard.Base.Output);
        Assert.Null(service.Current.Find("codex-auto-review"));
        Assert.Equal("prices-override.json non valido: ignorato", Assert.Single(errors));

        // A download still publishes the new list, without the override and without a new log line.
        await service.RefreshIfStaleAsync();
        Assert.Null(service.LastError);
        Assert.Equal(PriceListOrigin.Downloaded, service.Current.Origin);
        Assert.False(service.Current.OverrideActive);
        Assert.False(service.IsStale);
        Assert.Single(errors);
    }

    [Theory]
    // The same top-level key twice.
    [InlineData("""{"fetchedAt":"2026-09-23T09:00:00+00:00","fetchedAt":"2026-09-23T09:00:00+00:00","etag":null,"source":"test","models":{"gpt-6-luna":{"input_cost_per_token":1e-7,"output_cost_per_token":5e-7}}}""")]
    // The same model twice.
    [InlineData("""{"fetchedAt":"2026-09-23T09:00:00+00:00","etag":null,"source":"test","models":{"gpt-6-luna":{"input_cost_per_token":1e-7,"output_cost_per_token":5e-7},"gpt-6-luna":{"input_cost_per_token":1e-7,"output_cost_per_token":5e-7}}}""")]
    public void A_cache_with_a_duplicate_key_falls_back_to_the_snapshot(string cache)
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "prices-cache.json"), cache);
        File.WriteAllText(Path.Combine(dir.Path, "prices-override.json"), """{"gpt-6-luna":{"output_cost_per_token":6e-7}}""");
        var errors = new List<string>();
        var service = Service(dir, new FakeHttpMessageHandler(_ => throw new InvalidOperationException("no network")), new ManualTimeProvider(), errors);

        service.LoadLocal();

        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Origin);
        Assert.NotNull(service.Current.Find("claude-opus-5-5"));
        Assert.True(service.Current.OverrideActive);
        Assert.True(service.IsStale);
        Assert.Equal("Listino prezzi: cache non leggibile, verrà riscaricata", Assert.Single(errors));
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

    /// <summary>A body without a Content-Length, as a chunked response: only the streamed read can enforce the limit.</summary>
    private sealed class UnsizedContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>A handler whose response can wait, or honour the cancellation of the request.</summary>
    private sealed class AsyncHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return respond(request, cancellationToken);
        }
    }
}
