using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace AIUsageMonitor.Core.Pricing;

public enum ExchangeRateOrigin { Ecb, Fallback }

/// <summary>Dollars per euro, ECB convention: 1 € = <see cref="UsdPerEur"/> $.</summary>
public sealed record ExchangeRate(decimal UsdPerEur, ExchangeRateOrigin Origin, DateOnly? EcbDate);

public sealed class ExchangeRateServiceOptions
{
    public static readonly Uri DefaultSourceUrl = new("https://www.ecb.europa.eu/stats/eurofxref/eurofxref-daily.xml");

    public required string CacheFile { get; init; }
    public required HttpClient Http { get; init; }

    /// <summary>The settings' fallback rate, read at every access so a change applies at once.</summary>
    public required Func<decimal> FallbackUsdPerEur { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
    public Uri SourceUrl { get; init; } = DefaultSourceUrl;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);
    public string UserAgent { get; init; } = "AIUsageMonitor";
    public Action<string>? LogInfo { get; init; }
    public Action<string, Exception?>? LogError { get; init; }
}

/// <summary>
/// The ECB reference rate for USD, downloaded at most once a day and cached; without it, the fallback rate of the
/// settings. Thread-safe; never throws.
/// </summary>
public sealed class ExchangeRateService
{
    public const decimal DefaultUsdPerEur = 1.14m;

    private readonly ExchangeRateServiceOptions _options;
    private readonly SemaphoreSlim _downloading = new(1, 1);
    private volatile CachedRate? _cache;
    private volatile string? _lastError;

    public ExchangeRateService(ExchangeRateServiceOptions options) => _options = options;

    public ExchangeRate Current => _cache is { } cache
        ? new ExchangeRate(cache.UsdPerEur, ExchangeRateOrigin.Ecb, cache.EcbDate)
        : new ExchangeRate(Fallback(), ExchangeRateOrigin.Fallback, null);

    public string? LastError => _lastError;

    public event Action? Changed;

    public bool IsStale => _cache is not { } cache || _options.Time.GetUtcNow() - cache.FetchedAt >= _options.MaxAge;

    public void LoadLocal()
    {
        try
        {
            if (File.Exists(_options.CacheFile))
                _cache = JsonSerializer.Deserialize<CachedRate>(File.ReadAllText(_options.CacheFile)) is { UsdPerEur: > 0 } cached ? cached : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Report("Tasso BCE: cache non leggibile, verrà riscaricata", ex);
        }
        RaiseChanged();
    }

    public async Task RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStale) return;
        if (!await _downloading.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (!IsStale) return;
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.SourceUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            using var response = await _options.Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
            var xml = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!TryParse(xml, out var rate, out var date)) throw new InvalidDataException("file BCE senza tasso USD");

            var fresh = new CachedRate(rate, date, _options.Time.GetUtcNow());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.CacheFile))!);
            var tmp = _options.CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(fresh));
            File.Move(tmp, _options.CacheFile, overwrite: true);
            _cache = fresh;
            _lastError = null;
            _options.LogInfo?.Invoke(string.Create(CultureInfo.InvariantCulture, $"Tasso BCE aggiornato: 1 EUR = {rate} USD ({date:yyyy-MM-dd})"));
            RaiseChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is closing.
        }
        catch (Exception ex)
        {
            _lastError = ex is TaskCanceledException ? "timeout" : ex.Message;
            Report("Tasso BCE: download non riuscito", ex);
            RaiseChanged();
        }
        finally
        {
            _downloading.Release();
        }
    }

    /// <summary>Reads the USD rate and its date from <c>eurofxref-daily.xml</c>. DTDs are refused.</summary>
    public static bool TryParse(string xml, out decimal usdPerEur, out DateOnly date)
    {
        usdPerEur = 0m;
        date = default;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var cubes = XDocument.Load(reader).Descendants().Where(e => e.Name.LocalName == "Cube").ToList();
            var usd = cubes.FirstOrDefault(e => (string?)e.Attribute("currency") == "USD");
            var dated = cubes.FirstOrDefault(e => e.Attribute("time") is not null);
            if (usd is null || dated is null) return false;
            if (!decimal.TryParse((string?)usd.Attribute("rate"), NumberStyles.Number, CultureInfo.InvariantCulture, out usdPerEur) || usdPerEur <= 0) return false;
            return DateOnly.TryParseExact((string?)dated.Attribute("time"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private decimal Fallback()
    {
        try
        {
            var value = _options.FallbackUsdPerEur();
            return value > 0 ? value : DefaultUsdPerEur;
        }
        catch (Exception ex)
        {
            Report("Tasso di riserva non leggibile", ex);
            return DefaultUsdPerEur;
        }
    }

    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { Report("Tasso BCE: un ascoltatore ha sollevato un'eccezione", ex); }
        }
    }

    private void Report(string message, Exception? ex)
    {
        try { _options.LogError?.Invoke(message, ex); } catch { /* a broken logger must not break the rate */ }
    }

    private sealed record CachedRate(decimal UsdPerEur, DateOnly EcbDate, DateTimeOffset FetchedAt);
}
