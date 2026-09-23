using System.Globalization;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Pricing;

/// <summary>Price list and exchange rate as seen at one moment: what a cost is computed with.</summary>
public sealed record PricingSnapshot(PriceCatalog Catalog, ExchangeRate Rate)
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    public CostResult Cost(UsageLedger? ledger) => CostCalculator.Compute(ledger, Catalog, Rate.UsdPerEur);

    /// <summary>"Listino LiteLLM del 23/09 14:02 · 216 modelli" (plus " · override attivo"), in <paramref name="zone"/> (local by default).</summary>
    public string CatalogLine(TimeZoneInfo? zone = null)
    {
        DateTimeOffset? at = Catalog.FetchedAt is { } fetched ? TimeZoneInfo.ConvertTime(fetched, zone ?? TimeZoneInfo.Local) : null;
        var line = Catalog.Origin switch
        {
            PriceListOrigin.Downloaded => string.Create(Italian, $"Listino LiteLLM del {at:dd'/'MM HH':'mm} · {Catalog.Count} modelli"),
            PriceListOrigin.Snapshot => string.Create(Italian, $"Listino LiteLLM (copia imbarcata del {at:dd'/'MM'/'yyyy}) · {Catalog.Count} modelli"),
            _ => "Listino non disponibile"
        };
        return Catalog.OverrideActive ? line + " · override attivo" : line;
    }

    /// <summary>"BCE: 1 € = 1,1411 $ del 23/09" or "Tasso di riserva: 1 € = 1,14 $".</summary>
    public string RateLine() => Rate.Origin == ExchangeRateOrigin.Ecb
        ? string.Create(Italian, $"BCE: 1 € = {Rate.UsdPerEur:0.0000} $ del {Rate.EcbDate:dd'/'MM}")
        : string.Create(Italian, $"Tasso di riserva: 1 € = {Rate.UsdPerEur:0.00} $");
}

/// <summary>
/// Keeps the price list and the ECB rate up to date: loads the local copies at <see cref="Start"/>, checks
/// <see cref="FirstCheckDelay"/> later and then every <see cref="CheckEvery"/>, and downloads only what is older than
/// a day — and nothing at all while the costs are hidden. Never throws from its timer.
/// </summary>
public sealed class PricingService : IDisposable
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(6);

    private readonly PriceListService _prices;
    private readonly ExchangeRateService _rates;
    private readonly Func<bool> _enabled;
    private readonly TimeProvider _time;
    private readonly Action<string, Exception?>? _logError;
    private readonly CancellationTokenSource _cts = new();

    // Start may run on a pool thread while Dispose runs on the UI thread: the three fields below are guarded by _gate.
    private readonly object _gate = new();
    private ITimer? _timer;
    private bool _started;
    private bool _disposed;

    public PricingService(PriceListService prices, ExchangeRateService rates, Func<bool> enabled, TimeProvider time,
        Action<string, Exception?>? logError = null)
    {
        _prices = prices;
        _rates = rates;
        _enabled = enabled;
        _time = time;
        _logError = logError;
        _prices.Changed += RaiseChanged;
        _rates.Changed += RaiseChanged;
    }

    public PricingSnapshot Current => new(_prices.Current, _rates.Current);

    /// <summary>The last download errors of the two sources, joined; null when both succeeded (or never ran).</summary>
    public string? LastError
    {
        get
        {
            var errors = new[] { _prices.LastError is { } p ? $"listino: {p}" : null, _rates.LastError is { } r ? $"tasso BCE: {r}" : null }
                .Where(e => e is not null).ToList();
            return errors.Count == 0 ? null : string.Join(" · ", errors);
        }
    }

    /// <summary>Raised when the list or the rate changed (or recorded an error), on the thread that changed them.</summary>
    public event Action? Changed;

    /// <summary>
    /// Loads the local copies and schedules the checks. Only the first call does anything, and none after
    /// <see cref="Dispose"/>; safe to call from any thread, also while another one disposes the service.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
        }

        // File IO and listeners stay outside the lock.
        _prices.LoadLocal();
        _rates.LoadLocal();

        lock (_gate)
        {
            // Disposed while the local copies were loading: no timer that nothing would dispose.
            if (_disposed) return;
            _timer = _time.CreateTimer(_ => _ = RefreshIfStaleAsync(), null, FirstCheckDelay, CheckEvery);
        }
    }

    /// <summary>Downloads whatever is stale, both sources in parallel; nothing while costs are hidden. Never throws.</summary>
    public async Task RefreshIfStaleAsync()
    {
        try
        {
            if (_cts.IsCancellationRequested || !_enabled()) return;
            var token = _cts.Token;
            await Task.WhenAll(_prices.RefreshIfStaleAsync(token), _rates.RefreshIfStaleAsync(token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Disposed while downloading.
        }
        catch (Exception ex)
        {
            try { _logError?.Invoke("Prezzi: aggiornamento non riuscito", ex); } catch { /* never escape the timer */ }
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            timer = _timer;
            _timer = null;
        }
        timer?.Dispose();
        _prices.Changed -= RaiseChanged;
        _rates.Changed -= RaiseChanged;
        _cts.Cancel();
    }

    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex)
            {
                try { _logError?.Invoke("Prezzi: un ascoltatore ha sollevato un'eccezione", ex); } catch { /* ignore */ }
            }
        }
    }
}
