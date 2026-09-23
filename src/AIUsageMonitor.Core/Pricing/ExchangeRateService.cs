using System.Globalization;
using System.Text;
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

    /// <summary>Largest response body accepted; the real file is a few kilobytes.</summary>
    public long MaxBytes { get; init; } = 1024 * 1024;

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

    // EUR/USD has always stayed within 0.82–1.60: a value outside this much wider range is a malformed file, not a rate.
    private const decimal MinPlausibleUsdPerEur = 0.1m;
    private const decimal MaxPlausibleUsdPerEur = 10m;

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

    public bool IsStale => _cache is not { } cache || CacheAge.IsStale(cache.FetchedAt, _options.Time.GetUtcNow(), _options.MaxAge);

    public void LoadLocal()
    {
        try
        {
            if (File.Exists(_options.CacheFile))
            {
                var cached = JsonSerializer.Deserialize<CachedRate>(File.ReadAllText(_options.CacheFile));
                if (cached is not null && IsPlausible(cached.UsdPerEur) && cached.EcbDate != default)
                {
                    _cache = cached;
                }
                else
                {
                    _cache = null;
                    Report("Tasso BCE: cache non valida, verrà riscaricata", null);
                }
            }
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
        // A zero timeout never blocks, so the token adds nothing to the wait; checking it first keeps a cancelled call quiet.
        if (cancellationToken.IsCancellationRequested || !_downloading.Wait(0)) return;
        try
        {
            if (!IsStale) return;
            var (rate, date) = await DownloadAsync(cancellationToken).ConfigureAwait(false);

            // The rate is good even when the cache cannot be written: use it now, the file only saves a download at the next start.
            var fresh = new CachedRate(rate, date, _options.Time.GetUtcNow());
            _cache = fresh;
            _lastError = null;
            Info(string.Create(CultureInfo.InvariantCulture, $"Tasso BCE aggiornato: 1 EUR = {rate} USD ({date:yyyy-MM-dd})"));
            Save(fresh);
            RaiseChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is closing.
        }
        catch (Exception ex)
        {
            // The caller did not cancel (filter above), so a cancellation here is the timeout.
            _lastError = ex is OperationCanceledException ? "timeout" : ex.Message;
            Report("Tasso BCE: download non riuscito", ex);
            RaiseChanged();
        }
        finally
        {
            _downloading.Release();
        }
    }

    /// <summary>
    /// Reads the USD rate and its date from <c>eurofxref-daily.xml</c>. DTDs are refused, and so is a rate written with a
    /// thousands separator or outside 0.1–10 (a malformed file, not a rate).
    /// </summary>
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
            if (!decimal.TryParse((string?)usd.Attribute("rate"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out usdPerEur) || !IsPlausible(usdPerEur)) return false;
            return DateOnly.TryParseExact((string?)dated.Attribute("time"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private async Task<(decimal Rate, DateOnly Date)> DownloadAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _options.SourceUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var response = await _options.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _options.MaxBytes) throw new InvalidDataException("file BCE oltre il limite di dimensione");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        using var text = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        if (!TryParse(text.ReadToEnd(), out var rate, out var date)) throw new InvalidDataException("file BCE senza tasso USD");
        return (rate, date);
    }

    private void Save(CachedRate fresh)
    {
        var tmp = _options.CacheFile + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.CacheFile))!);
            File.WriteAllText(tmp, JsonSerializer.Serialize(fresh));
            File.Move(tmp, _options.CacheFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Report("Tasso BCE: cache non scrivibile, il tasso scaricato resta in memoria", ex);
            try { File.Delete(tmp); } catch { /* best effort */ }
        }
    }

    private static bool IsPlausible(decimal usdPerEur) => usdPerEur is >= MinPlausibleUsdPerEur and <= MaxPlausibleUsdPerEur;

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

    private void Info(string message)
    {
        try { _options.LogInfo?.Invoke(message); } catch { /* a broken logger must not turn a good rate into an error */ }
    }

    private sealed record CachedRate(decimal UsdPerEur, DateOnly EcbDate, DateTimeOffset FetchedAt);
}
