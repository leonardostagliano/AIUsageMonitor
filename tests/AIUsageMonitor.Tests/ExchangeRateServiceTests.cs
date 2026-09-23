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

    private static ExchangeRateService Service(TempDir dir, FakeHttpMessageHandler handler, ManualTimeProvider time, decimal fallback = 1.14m) =>
        new(new ExchangeRateServiceOptions
        {
            CacheFile = Path.Combine(dir.Path, "exchange-rate.json"),
            Http = new HttpClient(handler),
            FallbackUsdPerEur = () => fallback,
            Time = time
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
            CacheFile = Path.Combine(dir.Path, "exchange-rate.json"),
            Http = new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException())),
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
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EcbXml) });
        var service = Service(dir, handler, time);
        service.LoadLocal();

        await service.RefreshIfStaleAsync();

        Assert.Equal(new ExchangeRate(1.1411m, ExchangeRateOrigin.Ecb, new DateOnly(2026, 9, 23)), service.Current);
        Assert.False(service.IsStale);
        Assert.Equal(ExchangeRateServiceOptions.DefaultSourceUrl, Assert.Single(handler.Requests).RequestUri);

        var restarted = Service(dir, new FakeHttpMessageHandler(_ => throw new InvalidOperationException()), time);
        restarted.LoadLocal();
        Assert.Equal(1.1411m, restarted.Current.UsdPerEur);
        Assert.False(restarted.IsStale);

        time.Advance(TimeSpan.FromHours(24));
        Assert.True(restarted.IsStale);
    }

    [Fact]
    public async Task A_failed_download_keeps_the_last_rate_and_records_the_error()
    {
        using var dir = new TempDir();
        var service = Service(dir, new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<x/>") }), new ManualTimeProvider());
        service.LoadLocal();
        var changed = 0;
        service.Changed += () => changed++;

        await service.RefreshIfStaleAsync();

        Assert.Equal(ExchangeRateOrigin.Fallback, service.Current.Origin);
        Assert.NotNull(service.LastError);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void A_non_positive_or_throwing_fallback_becomes_1_14()
    {
        using var dir = new TempDir();
        var zero = Service(dir, new FakeHttpMessageHandler(_ => throw new InvalidOperationException()), new ManualTimeProvider(), fallback: 0m);
        Assert.Equal(1.14m, zero.Current.UsdPerEur);

        var throwing = new ExchangeRateService(new ExchangeRateServiceOptions
        {
            CacheFile = Path.Combine(dir.Path, "x.json"),
            Http = new HttpClient(new FakeHttpMessageHandler(_ => throw new InvalidOperationException())),
            FallbackUsdPerEur = () => throw new InvalidOperationException("settings not loaded")
        });
        Assert.Equal(1.14m, throwing.Current.UsdPerEur);
    }
}
