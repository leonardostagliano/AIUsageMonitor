using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Pricing;
using AIUsageMonitor.Core.Settings;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class PricingServiceTests
{
    private static readonly string FullList = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "litellm-prices.json"));

    private const string EcbXml = "<Envelope><Cube><Cube time='2026-09-23'><Cube currency='USD' rate='1.1411'/></Cube></Cube></Envelope>";

    private static HttpResponseMessage Respond(HttpRequestMessage request) =>
        request.RequestUri == ExchangeRateServiceOptions.DefaultSourceUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EcbXml) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullList, Encoding.UTF8, "application/json") };

    private static FakeHttpMessageHandler Network() => new(Respond);

    private static PricingService Service(TempDir dir, FakeHttpMessageHandler handler, ManualTimeProvider time, Func<bool>? enabled = null,
        Action<string, Exception?>? logError = null)
    {
        var (prices, rates) = Sources(dir, handler, time);
        return new PricingService(prices, rates, enabled ?? (() => true), time, logError);
    }

    private static (PriceListService Prices, ExchangeRateService Rates) Sources(TempDir dir, FakeHttpMessageHandler handler, ManualTimeProvider time)
    {
        var http = new HttpClient(handler);
        var prices = new PriceListService(new PriceListServiceOptions
        {
            CacheFile = Path.Combine(dir.Path, "prices-cache.json"),
            OverrideFile = Path.Combine(dir.Path, "prices-override.json"),
            Http = http,
            Time = time
        });
        var rates = new ExchangeRateService(new ExchangeRateServiceOptions
        {
            CacheFile = Path.Combine(dir.Path, "exchange-rate.json"),
            Http = http,
            FallbackUsdPerEur = () => 1.14m,
            Time = time
        });
        return (prices, rates);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task RefreshIfStaleAsync_downloads_off_the_calling_thread()
    {
        using var dir = new TempDir();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // A handler that blocks: run on the caller's thread — the UI thread for the refresh button — it would block
        // the call itself, the way a proxy lookup does before the first real await.
        var handler = new FakeHttpMessageHandler(request =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return Respond(request);
        });
        using var service = Service(dir, handler, new ManualTimeProvider());

        var refresh = service.RefreshIfStaleAsync();

        Assert.False(refresh.IsCompleted);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        release.Set();
        await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(PriceListOrigin.Downloaded, service.Current.Catalog.Origin);
        Assert.Equal(ExchangeRateOrigin.Ecb, service.Current.Rate.Origin);
    }

    [Fact]
    public async Task Start_loads_the_local_lists_checks_20_seconds_later_then_every_6_hours()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), PricingService.FirstCheckDelay);
        Assert.Equal(TimeSpan.FromHours(6), PricingService.CheckEvery);

        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var start = time.GetUtcNow();
        var handler = Network();
        // Every check asks whether the costs are shown first: counting the questions counts the checks, downloads or not.
        var checks = 0;
        using var service = Service(dir, handler, time, enabled: () => { checks++; return true; });

        service.Start();
        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Catalog.Origin);
        Assert.Equal(ExchangeRateOrigin.Fallback, service.Current.Rate.Origin);

        time.Advance(PricingService.FirstCheckDelay - TimeSpan.FromSeconds(1));
        Assert.Equal(0, checks);
        Assert.Empty(handler.Requests);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, checks);
        await Eventually(() => service.Current.Catalog.Origin == PriceListOrigin.Downloaded && service.Current.Rate.Origin == ExchangeRateOrigin.Ecb);
        Assert.Equal(2, handler.Requests.Count);

        // A check every 6 hours, but both copies stay fresh for a day: nothing is downloaded at 6, 12 and 18 hours.
        time.Advance(PricingService.CheckEvery - TimeSpan.FromSeconds(1));
        Assert.Equal(1, checks);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, checks);
        time.Advance(PricingService.CheckEvery * 3 - TimeSpan.FromSeconds(1));
        Assert.Equal(4, checks);
        Assert.Equal(2, handler.Requests.Count);

        // The check 24 h after the first download finds both copies a day old and downloads them again.
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(5, checks);
        await Eventually(() => handler.Requests.Count == 4 && service.Current.Catalog.FetchedAt == start + PricingService.FirstCheckDelay + TimeSpan.FromDays(1));
        Assert.Contains(handler.Requests.Skip(2), r => r.RequestUri == PriceListServiceOptions.DefaultSourceUrl);
        Assert.Contains(handler.Requests.Skip(2), r => r.RequestUri == ExchangeRateServiceOptions.DefaultSourceUrl);
    }

    [Fact]
    public async Task Nothing_is_downloaded_while_costs_are_hidden()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var handler = Network();
        using var service = Service(dir, handler, time, enabled: () => false);
        service.Start();

        time.Advance(TimeSpan.FromHours(7));
        await service.RefreshIfStaleAsync();

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Changed_forwards_both_sources_and_Dispose_stops_the_timer()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var handler = Network();
        var service = Service(dir, handler, time);
        service.Start();
        var changed = 0;
        service.Changed += () => changed++;

        await service.RefreshIfStaleAsync();
        Assert.Equal(2, changed);

        service.Dispose();
        Assert.Equal(0, time.ActiveTimers);
    }

    [Fact]
    public async Task A_failing_check_is_logged_and_never_escapes()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var handler = Network();
        var failure = new InvalidOperationException("settings unreadable");
        var errors = new ConcurrentQueue<(string Message, Exception? Error)>();
        using var service = Service(dir, handler, time, enabled: () => throw failure, logError: (m, e) => errors.Enqueue((m, e)));
        service.Start();

        time.Advance(PricingService.FirstCheckDelay);
        await service.RefreshIfStaleAsync();

        Assert.Empty(handler.Requests);
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error =>
        {
            Assert.Equal("Prezzi: aggiornamento non riuscito", error.Message);
            Assert.Same(failure, error.Error);
        });

        // Not even a logger that throws gets out.
        using var broken = Service(dir, handler, time, enabled: () => throw failure, logError: (_, _) => throw new InvalidOperationException("logger"));
        await broken.RefreshIfStaleAsync();
    }

    [Fact]
    public async Task A_throwing_listener_is_logged_and_the_next_ones_still_run()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var failure = new InvalidOperationException("listener");
        var errors = new ConcurrentQueue<(string Message, Exception? Error)>();
        using var service = Service(dir, Network(), time, logError: (m, e) => errors.Enqueue((m, e)));
        var reached = 0;
        service.Changed += () => throw failure;
        service.Changed += () => Interlocked.Increment(ref reached);

        await service.RefreshIfStaleAsync();

        Assert.Equal(2, Volatile.Read(ref reached));
        Assert.Equal(2, errors.Count);
        Assert.All(errors, error =>
        {
            Assert.Equal("Prezzi: un ascoltatore ha sollevato un'eccezione", error.Message);
            Assert.Same(failure, error.Error);
        });
    }

    [Fact]
    public async Task LastError_names_the_failed_sources_until_they_download()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var failing = new HashSet<Uri> { PriceListServiceOptions.DefaultSourceUrl, ExchangeRateServiceOptions.DefaultSourceUrl };
        var handler = new FakeHttpMessageHandler(request =>
            failing.Contains(request.RequestUri!) ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Respond(request));
        using var service = Service(dir, handler, time);
        service.Start();
        Assert.Null(service.LastError);

        await service.RefreshIfStaleAsync();
        Assert.Equal("listino: HTTP 503 · tasso BCE: HTTP 503", service.LastError);

        // A failed source has nothing fresh, so the next check tries it again at once.
        failing.Remove(PriceListServiceOptions.DefaultSourceUrl);
        await service.RefreshIfStaleAsync();
        Assert.Equal("tasso BCE: HTTP 503", service.LastError);

        failing.Clear();
        await service.RefreshIfStaleAsync();
        Assert.Null(service.LastError);
        Assert.Equal(PriceListOrigin.Downloaded, service.Current.Catalog.Origin);
        Assert.Equal(ExchangeRateOrigin.Ecb, service.Current.Rate.Origin);
    }

    [Fact]
    public async Task After_Dispose_the_sources_no_longer_reach_Changed_and_nothing_is_downloaded()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var handler = Network();
        var (prices, rates) = Sources(dir, handler, time);
        var service = new PricingService(prices, rates, () => true, time);
        var changed = 0;
        service.Changed += () => changed++;

        prices.LoadLocal();
        rates.LoadLocal();
        Assert.Equal(2, changed);

        service.Dispose();
        prices.LoadLocal();
        rates.LoadLocal();
        await service.RefreshIfStaleAsync();

        Assert.Equal(2, changed);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Start_runs_once_and_never_after_Dispose()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var service = Service(dir, Network(), time);
        var loaded = 0;
        service.Changed += () => loaded++;

        service.Start();
        service.Start();
        Assert.Equal(1, time.ActiveTimers);
        Assert.Equal(2, loaded); // one local load per source, once

        service.Dispose();
        service.Dispose();
        Assert.Equal(0, time.ActiveTimers);

        var late = Service(dir, Network(), time);
        late.Dispose();
        late.Start();
        Assert.Equal(0, time.ActiveTimers);
        Assert.Equal(PriceListOrigin.None, late.Current.Catalog.Origin);
    }

    [Fact]
    public void Snapshot_prices_a_ledger_and_describes_its_sources()
    {
        var models = new Dictionary<string, ModelPrice>
        {
            ["m"] = new("m", new VariantPrices(new PriceRates(0.000001m, 0m, 0m, 0m, 0m), new Dictionary<long, PriceRates>()), null, null,
                new Dictionary<string, decimal>(), 0m, null)
        };
        var fetched = new DateTimeOffset(2026, 9, 23, 12, 2, 0, TimeSpan.Zero);
        var snapshot = new PricingSnapshot(new PriceCatalog(models, PriceListOrigin.Downloaded, fetched, overrideActive: true),
            new ExchangeRate(1.25m, ExchangeRateOrigin.Ecb, new DateOnly(2026, 9, 23)));
        var ledger = UsageLedger.From([KeyValuePair.Create(new UsageKey("m", PriceTier.Standard, null, 0), new LedgerTokens(1_000_000, 0, 0, 0, 0))]);

        Assert.Equal(0.8m, snapshot.Cost(ledger).Eur);
        Assert.Equal("Listino LiteLLM del 23/09 12:02 · 1 modelli · override attivo", snapshot.CatalogLine(TimeZoneInfo.Utc));
        Assert.Equal("BCE: 1 € = 1,2500 $ del 23/09", snapshot.RateLine());
        Assert.Equal("Tasso di riserva: 1 € = 1,14 $", (snapshot with { Rate = new ExchangeRate(1.14m, ExchangeRateOrigin.Fallback, null) }).RateLine());
        Assert.Equal("Listino non disponibile", new PricingSnapshot(PriceCatalog.Empty, snapshot.Rate).CatalogLine());
    }

    [Fact]
    public void Settings_default_to_showing_costs_and_clamp_the_fallback_rate()
    {
        Assert.True(new AppSettings().ShowCosts);
        Assert.Equal(1.14, new AppSettings().UsdPerEur);
        Assert.Equal(2.0, new AppSettings { UsdPerEur = 5 }.Normalized().UsdPerEur);
        Assert.Equal(0.5, new AppSettings { UsdPerEur = 0.1 }.Normalized().UsdPerEur);
        Assert.Equal(1.14, new AppSettings { UsdPerEur = double.NaN }.Normalized().UsdPerEur);
    }

    [Fact]
    public void Pricing_files_live_in_the_app_data_folder()
    {
        var paths = new AppPaths("C:\\home", "C:\\lad");
        Assert.Equal(Path.Combine("C:\\lad", "prices-cache.json"), paths.PricesCacheFile);
        Assert.Equal(Path.Combine("C:\\lad", "prices-override.json"), paths.PricesOverrideFile);
        Assert.Equal(Path.Combine("C:\\lad", "exchange-rate.json"), paths.ExchangeRateFile);
    }
}
