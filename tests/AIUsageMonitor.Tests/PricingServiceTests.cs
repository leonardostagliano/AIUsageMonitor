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

    private static FakeHttpMessageHandler Network() => new(request =>
        request.RequestUri == ExchangeRateServiceOptions.DefaultSourceUrl
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(EcbXml) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(FullList, Encoding.UTF8, "application/json") });

    private static PricingService Service(TempDir dir, FakeHttpMessageHandler handler, ManualTimeProvider time, Func<bool>? enabled = null)
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
        return new PricingService(prices, rates, enabled ?? (() => true), time);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task Start_loads_the_local_lists_and_checks_20_seconds_later()
    {
        using var dir = new TempDir();
        var time = new ManualTimeProvider();
        var handler = Network();
        using var service = Service(dir, handler, time);

        service.Start();
        Assert.Equal(PriceListOrigin.Snapshot, service.Current.Catalog.Origin);
        Assert.Equal(ExchangeRateOrigin.Fallback, service.Current.Rate.Origin);

        time.Advance(TimeSpan.FromSeconds(19));
        Assert.Empty(handler.Requests);

        time.Advance(TimeSpan.FromSeconds(1));
        await Eventually(() => service.Current.Catalog.Origin == PriceListOrigin.Downloaded && service.Current.Rate.Origin == ExchangeRateOrigin.Ecb);
        Assert.Equal(2, handler.Requests.Count);
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
