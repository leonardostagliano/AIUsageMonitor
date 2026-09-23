using System.Net;
using AIUsageMonitor.Core.Pricing;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ExchangeRateServiceTests
{
    private const string EcbXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
        	<gesmes:subject>Reference rates</gesmes:subject>
        	<gesmes:Sender><gesmes:name>European Central Bank</gesmes:name></gesmes:Sender>
        	<Cube>
        		<Cube time='2026-09-23'>
        			<Cube currency='USD' rate='1.1411'/>
        			<Cube currency='JPY' rate='180.20'/>
        			<Cube currency='GBP' rate='0.85950'/>
        		</Cube>
        	</Cube>
        </gesmes:Envelope>
        """;

    private const string XmlWithoutUsd = "<Envelope><Cube><Cube time='2026-09-24'><Cube currency='JPY' rate='180'/></Cube></Cube></Envelope>";

    private static readonly ExchangeRate EcbRate = new(1.1411m, ExchangeRateOrigin.Ecb, new DateOnly(2026, 9, 23));

    private static string CacheFile(TempDir dir) => Path.Combine(dir.Path, "exchange-rate.json");

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private static FakeHttpMessageHandler Offline() => new(_ => throw new InvalidOperationException("no request expected"));

    private static ExchangeRateService Service(TempDir dir, HttpMessageHandler handler, ManualTimeProvider time, decimal fallback = 1.14m, List<string>? errors = null) =>
        new(new ExchangeRateServiceOptions
        {
            CacheFile = CacheFile(dir),
            Http = new HttpClient(handler),
            FallbackUsdPerEur = () => fallback,
            Time = time,
            LogError = (message, _) => errors?.Add(message)
        });

    [Fact]
    public void Parses_the_USD_rate_and_the_date_of_the_real_ECB_file()
    {
        Assert.True(ExchangeRateService.TryParse(EcbXml, out var rate, out var date));
        Assert.Equal(1.1411m, rate);
        Assert.Equal(new DateOnly(2026, 9, 23), date);
    }

    [Theory]
    [InlineData("<Envelope><Cube><Cube time='2026-09-23'><Cube currency='JPY' rate='180'/></Cube></Cube></Envelope>")]
    [InlineData("<Envelope><Cube><Cube><Cube currency='USD' rate='1.1'/></Cube></Cube></Envelope>")]
    [InlineData("<Envelope><Cube><Cube time='2026-09-23'><Cube currency='USD' rate='abc'/></Cube></Cube></Envelope>")]
    [InlineData("<Envelope><Cube><Cube time='2026-09-23'><Cube currency='USD' rate='1,1411'/></Cube></Cube></Envelope>")]
    [InlineData("<Envelope><Cube><Cube time='2026-09-23'><Cube currency='USD' rate='11411'/></Cube></Cube></Envelope>")]
    [InlineData("<Envelope><Cube><Cube time='2026-09-23'><Cube currency='USD' rate='0.05'/></Cube></Cube></Envelope>")]
    [InlineData("<Envelope><Cube><Cube time='2026-09-23'><Cube currency='USD' rate='-1.1'/></Cube></Cube></Envelope>")]
    [InlineData("not xml")]
    [InlineData("<!DOCTYPE x [<!ENTITY e 'y'>]><Envelope/>")]
    public void Rejects_files_without_a_usable_USD_rate_or_date(string xml) =>
        Assert.False(ExchangeRateService.TryParse(xml, out _, out _));

    [Fact]
    public void Without_a_cache_the_fallback_rate_from_the_settings_is_used()
    {
        using var dir = new TempDir();
        var fallback = 1.2m;
        var service = new ExchangeRateService(new ExchangeRateServiceOptions
        {
            CacheFile = CacheFile(dir),
            Http = new HttpClient(Offline()),
            FallbackUsdPerEur = () => fallback,
            Time = new ManualTimeProvider()
        });

        service.LoadLocal();
        Assert.Equal(new ExchangeRate(1.2m, ExchangeRateOrigin.Fallback, null), service.Current);

        fallback = 1.3m;
        Assert.Equal(1.3m, service.Current.UsdPerEur);
        Assert.True(service.IsStale);
    }

    [Fact]
    public async Task A_download_is_cached_and_reused_after_a_restart()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var handler = new FakeHttpMessageHandler(_ => Ok(EcbXml));
        var service = Service(dir, handler, time);
        service.LoadLocal();

        await service.RefreshIfStaleAsync();

        Assert.Equal(EcbRate, service.Current);
        Assert.False(service.IsStale);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(ExchangeRateServiceOptions.DefaultSourceUrl, request.RequestUri);
        Assert.Equal("AIUsageMonitor", request.Headers.UserAgent.ToString());

        var offline = Offline();
        var restarted = Service(dir, offline, time);
        restarted.LoadLocal();
        Assert.Equal(EcbRate, restarted.Current);
        Assert.False(restarted.IsStale);

        await restarted.RefreshIfStaleAsync();
        Assert.Empty(offline.Requests);
        Assert.Null(restarted.LastError);

        time.Advance(TimeSpan.FromHours(24));
        Assert.True(restarted.IsStale);
        Assert.Equal(EcbRate, restarted.Current);
    }

    [Fact]
    public async Task A_failed_first_download_keeps_the_fallback_rate_and_records_the_error()
    {
        using var dir = new TempDir();
        var errors = new List<string>();
        var service = Service(dir, new FakeHttpMessageHandler(_ => Ok("<x/>")), new ManualTimeProvider(), errors: errors);
        service.LoadLocal();
        var changed = 0;
        service.Changed += () => changed++;

        await service.RefreshIfStaleAsync();

        Assert.Equal(new ExchangeRate(1.14m, ExchangeRateOrigin.Fallback, null), service.Current);
        Assert.NotNull(service.LastError);
        Assert.Single(errors);
        Assert.Equal(1, changed);
        Assert.False(File.Exists(CacheFile(dir)));
    }

    [Theory]
    [InlineData("HTTP 503")]
    [InlineData("XML without USD")]
    [InlineData("network error")]
    public async Task A_failed_refresh_keeps_the_stale_ECB_rate_and_records_the_error(string failure)
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var yesterday = Service(dir, new FakeHttpMessageHandler(_ => Ok(EcbXml)), time);
        yesterday.LoadLocal();
        await yesterday.RefreshIfStaleAsync();
        var cached = File.ReadAllText(CacheFile(dir));
        time.Advance(TimeSpan.FromHours(25));

        var errors = new List<string>();
        var handler = new FakeHttpMessageHandler(_ => failure switch
        {
            "HTTP 503" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "XML without USD" => Ok(XmlWithoutUsd),
            _ => throw new HttpRequestException("network down")
        });
        var service = Service(dir, handler, time, errors: errors);
        service.LoadLocal();
        Assert.True(service.IsStale);

        await service.RefreshIfStaleAsync();

        Assert.Single(handler.Requests);
        Assert.Equal(EcbRate, service.Current);
        Assert.True(service.IsStale);
        Assert.NotNull(service.LastError);
        if (failure == "HTTP 503") Assert.Equal("HTTP 503", service.LastError);
        Assert.Single(errors);
        Assert.Equal(cached, File.ReadAllText(CacheFile(dir)));
    }

    [Fact]
    public async Task A_successful_download_clears_the_previous_error()
    {
        using var dir = new TempDir();
        var down = true;
        var service = Service(dir, new FakeHttpMessageHandler(_ => down ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(EcbXml)), new ManualTimeProvider());
        service.LoadLocal();

        await service.RefreshIfStaleAsync();
        Assert.Equal("HTTP 503", service.LastError);

        down = false;
        await service.RefreshIfStaleAsync();

        Assert.Null(service.LastError);
        Assert.Equal(EcbRate, service.Current);
    }

    [Fact]
    public async Task A_download_that_outlasts_the_timeout_is_recorded_as_a_timeout()
    {
        using var dir = new TempDir();
        var errors = new List<string>();
        var service = new ExchangeRateService(new ExchangeRateServiceOptions
        {
            CacheFile = CacheFile(dir),
            Http = new HttpClient(new HangingHandler()),
            FallbackUsdPerEur = () => 1.14m,
            Time = new ManualTimeProvider(),
            Timeout = TimeSpan.FromMilliseconds(50),
            LogError = (message, _) => errors.Add(message)
        });

        await service.RefreshIfStaleAsync();

        Assert.Equal("timeout", service.LastError);
        Assert.Equal(ExchangeRateOrigin.Fallback, service.Current.Origin);
        Assert.Single(errors);
    }

    [Fact]
    public async Task A_response_over_the_size_limit_is_refused()
    {
        using var dir = new TempDir();
        var errors = new List<string>();
        var service = new ExchangeRateService(new ExchangeRateServiceOptions
        {
            CacheFile = CacheFile(dir),
            Http = new HttpClient(new FakeHttpMessageHandler(_ => Ok(EcbXml))),
            FallbackUsdPerEur = () => 1.14m,
            Time = new ManualTimeProvider(),
            MaxBytes = 100,
            LogError = (message, _) => errors.Add(message)
        });

        await service.RefreshIfStaleAsync();

        Assert.Equal(ExchangeRateOrigin.Fallback, service.Current.Origin);
        Assert.Equal("file BCE oltre il limite di dimensione", service.LastError);
        Assert.Single(errors);
        Assert.False(File.Exists(CacheFile(dir)));
    }

    [Fact]
    public async Task A_cancelled_refresh_ends_quietly_and_leaves_no_error()
    {
        using var dir = new TempDir();
        var errors = new List<string>();
        var handler = new FakeHttpMessageHandler(_ => Ok(EcbXml));
        var service = Service(dir, handler, new ManualTimeProvider(), errors: errors);
        var changed = 0;
        service.Changed += () => changed++;

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await service.RefreshIfStaleAsync(cancelled.Token);
        Assert.Empty(handler.Requests);

        var hanging = new HangingHandler();
        var closing = Service(dir, hanging, new ManualTimeProvider(), errors: errors);
        using var closingToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await closing.RefreshIfStaleAsync(closingToken.Token);
        Assert.Equal(1, hanging.Requests);
        Assert.Null(closing.LastError);

        Assert.Null(service.LastError);
        Assert.Empty(errors);
        Assert.Equal(0, changed);

        // The cancelled call took no slot, so the next refresh still downloads.
        await service.RefreshIfStaleAsync();
        Assert.Equal(EcbRate, service.Current);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"UsdPerEur":"abc","EcbDate":"2026-09-23","FetchedAt":"2026-09-23T10:00:00+00:00"}""")]
    [InlineData("""{"UsdPerEur":0,"EcbDate":"2026-09-23","FetchedAt":"2026-09-23T10:00:00+00:00"}""")]
    [InlineData("""{"UsdPerEur":11411,"EcbDate":"2026-09-23","FetchedAt":"2026-09-23T10:00:00+00:00"}""")]
    [InlineData("""{"UsdPerEur":1.1411,"FetchedAt":"2026-09-23T10:00:00+00:00"}""")]
    [InlineData("null")]
    public void An_invalid_cache_is_reported_and_the_settings_rate_is_used(string content)
    {
        using var dir = new TempDir();
        dir.File("exchange-rate.json", content);
        var errors = new List<string>();
        var service = Service(dir, Offline(), new ManualTimeProvider(), errors: errors);

        service.LoadLocal();

        Assert.Equal(new ExchangeRate(1.14m, ExchangeRateOrigin.Fallback, null), service.Current);
        Assert.True(service.IsStale);
        Assert.Single(errors);
    }

    [Fact]
    public void A_locked_cache_file_is_reported_and_the_settings_rate_is_used()
    {
        using var dir = new TempDir();
        var path = dir.File("exchange-rate.json", """{"UsdPerEur":1.1411,"EcbDate":"2026-09-23","FetchedAt":"2026-09-23T10:00:00+00:00"}""");
        var errors = new List<string>();
        var service = Service(dir, Offline(), new ManualTimeProvider(), errors: errors);

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            service.LoadLocal();

        Assert.Equal(ExchangeRateOrigin.Fallback, service.Current.Origin);
        Assert.Single(errors);
    }

    [Fact]
    public async Task A_cache_dated_in_the_future_is_stale_and_downloaded_again()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));
        // Written while the clock was a month ahead (or edited by hand): it must not block downloads until then.
        dir.File("exchange-rate.json", """{"UsdPerEur":1.2,"EcbDate":"2026-10-22","FetchedAt":"2026-10-23T10:00:00+00:00"}""");
        var handler = new FakeHttpMessageHandler(_ => Ok(EcbXml));
        var service = Service(dir, handler, time);
        service.LoadLocal();

        Assert.True(service.IsStale);
        await service.RefreshIfStaleAsync();

        Assert.Single(handler.Requests);
        Assert.Equal(EcbRate, service.Current);
        Assert.False(service.IsStale);
    }

    [Fact]
    public void A_cache_a_few_minutes_ahead_is_still_fresh()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));
        // A small clock correction after the download.
        dir.File("exchange-rate.json", """{"UsdPerEur":1.1411,"EcbDate":"2026-09-23","FetchedAt":"2026-09-23T10:04:00+00:00"}""");
        var service = Service(dir, Offline(), time);
        service.LoadLocal();

        Assert.False(service.IsStale);
    }

    [Fact]
    public async Task A_downloaded_rate_is_used_even_when_the_cache_cannot_be_written()
    {
        using var dir = new TempDir();
        dir.Sub("exchange-rate.json"); // a folder where the cache file should go
        var errors = new List<string>();
        var service = Service(dir, new FakeHttpMessageHandler(_ => Ok(EcbXml)), new ManualTimeProvider(), errors: errors);
        service.LoadLocal();

        await service.RefreshIfStaleAsync();

        Assert.Equal(EcbRate, service.Current);
        Assert.False(service.IsStale);
        Assert.Null(service.LastError);
        Assert.Single(errors);
        Assert.False(File.Exists(CacheFile(dir) + ".tmp"));
    }

    [Fact]
    public async Task Broken_listeners_and_loggers_do_not_break_the_rate()
    {
        using var dir = new TempDir();
        dir.File("exchange-rate.json", "{not json");
        var down = true;
        var service = new ExchangeRateService(new ExchangeRateServiceOptions
        {
            CacheFile = CacheFile(dir),
            Http = new HttpClient(new FakeHttpMessageHandler(_ => down ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Ok(EcbXml))),
            FallbackUsdPerEur = () => 1.14m,
            Time = new ManualTimeProvider(),
            LogInfo = _ => throw new InvalidOperationException("info log down"),
            LogError = (_, _) => throw new InvalidOperationException("error log down")
        });
        var notified = 0;
        service.Changed += () => throw new InvalidOperationException("listener down");
        service.Changed += () => notified++;

        service.LoadLocal();
        await service.RefreshIfStaleAsync();
        Assert.Equal(ExchangeRateOrigin.Fallback, service.Current.Origin);
        Assert.Equal("HTTP 503", service.LastError);

        down = false;
        await service.RefreshIfStaleAsync();

        Assert.Equal(EcbRate, service.Current);
        Assert.Null(service.LastError);
        Assert.Equal(3, notified);
    }

    [Fact]
    public void A_non_positive_or_throwing_fallback_becomes_1_14()
    {
        using var dir = new TempDir();
        var zero = Service(dir, Offline(), new ManualTimeProvider(), fallback: 0m);
        Assert.Equal(1.14m, zero.Current.UsdPerEur);

        var throwing = new ExchangeRateService(new ExchangeRateServiceOptions
        {
            CacheFile = Path.Combine(dir.Path, "x.json"),
            Http = new HttpClient(Offline()),
            FallbackUsdPerEur = () => throw new InvalidOperationException("settings not loaded")
        });
        Assert.Equal(1.14m, throwing.Current.UsdPerEur);
    }

    /// <summary>Never answers: the request ends only when its token is cancelled.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
