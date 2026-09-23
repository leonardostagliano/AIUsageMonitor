using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Core.Pricing;

public sealed class PriceListServiceOptions
{
    public static readonly Uri DefaultSourceUrl = new("https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json");

    public required string CacheFile { get; init; }
    public required string OverrideFile { get; init; }
    public required HttpClient Http { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
    public Uri SourceUrl { get; init; } = DefaultSourceUrl;
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public long MaxBytes { get; init; } = 20L * 1024 * 1024;
    public string UserAgent { get; init; } = "AIUsageMonitor";
    public Func<Stream?> Snapshot { get; init; } = PriceListService.EmbeddedSnapshot;
    public Action<string>? LogInfo { get; init; }
    public Action<string, Exception?>? LogError { get; init; }
}

/// <summary>
/// The model price list: the embedded snapshot until a download succeeds, then the local cache, with
/// <c>prices-override.json</c> applied on top. Downloads the LiteLLM list only when the cache is older than
/// <see cref="PriceListServiceOptions.MaxAge"/>, revalidating it with its ETag. Thread-safe; never throws.
/// </summary>
public sealed class PriceListService
{
    public const string SnapshotResourceName = "prices-snapshot.json";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    private readonly PriceListServiceOptions _options;
    private readonly SemaphoreSlim _downloading = new(1, 1);
    private readonly object _gate = new();
    private readonly HashSet<long> _loggedThresholds = [];
    private ListFile? _cache;
    private string? _loggedOverride;
    private volatile PriceCatalog _current = PriceCatalog.Empty;
    private volatile string? _lastError;

    public PriceListService(PriceListServiceOptions options) => _options = options;

    public PriceCatalog Current => _current;

    /// <summary>Short description of the last failed download, null after a success.</summary>
    public string? LastError => _lastError;

    /// <summary>Raised on the thread that changed the list (or recorded an error).</summary>
    public event Action? Changed;

    public static Stream? EmbeddedSnapshot() =>
        typeof(PriceListService).Assembly.GetManifestResourceStream(SnapshotResourceName);

    public bool IsStale
    {
        get
        {
            lock (_gate) return _cache is null || _options.Time.GetUtcNow() - _cache.FetchedAt >= _options.MaxAge;
        }
    }

    /// <summary>Cache when valid, otherwise the embedded snapshot; the override on top. Never throws.</summary>
    public void LoadLocal()
    {
        try
        {
            var cache = ReadFile(_options.CacheFile);
            lock (_gate) _cache = cache;
            Publish(cache ?? ReadSnapshot());
        }
        catch (Exception ex)
        {
            Report("Listino prezzi: caricamento locale non riuscito", ex);
        }
    }

    /// <summary>Downloads the list when the cache is stale; one download at a time. Never throws.</summary>
    public async Task RefreshIfStaleAsync(CancellationToken cancellationToken = default)
    {
        if (!IsStale) return;
        if (!await _downloading.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return;
        try
        {
            if (IsStale) await DownloadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The app is closing.
        }
        catch (Exception ex)
        {
            _lastError = Describe(ex);
            Report("Listino prezzi: download non riuscito", ex);
            RaiseChanged();
        }
        finally
        {
            _downloading.Release();
        }
    }

    /// <summary>Writes a list file (cache or snapshot) atomically: <c>{fetchedAt, etag, source, models}</c>.</summary>
    public static void WriteListFile(string path, DateTimeOffset fetchedAt, string? etag, string source, JsonObject models)
    {
        var document = new JsonObject
        {
            ["fetchedAt"] = fetchedAt.ToString("O", CultureInfo.InvariantCulture),
            ["etag"] = etag,
            ["source"] = source,
            ["models"] = models.DeepClone()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, document.ToJsonString(WriteOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        ListFile? cache;
        lock (_gate) cache = _cache;

        using var request = new HttpRequestMessage(HttpMethod.Get, _options.SourceUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        if (cache?.ETag is { Length: > 0 } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        using var response = await _options.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var now = _options.Time.GetUtcNow();

        if (response.StatusCode == HttpStatusCode.NotModified && cache is not null)
        {
            var renewed = cache with { FetchedAt = now };
            WriteListFile(_options.CacheFile, renewed.FetchedAt, renewed.ETag, renewed.Source, renewed.Models);
            lock (_gate) _cache = renewed;
            _lastError = null;
            _options.LogInfo?.Invoke("Listino prezzi invariato (304)");
            Publish(renewed);
            return;
        }

        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}");
        if (response.Content.Headers.ContentLength > _options.MaxBytes) throw new InvalidDataException("listino oltre il limite di dimensione");

        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > _options.MaxBytes) throw new InvalidDataException("listino oltre il limite di dimensione");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;

        using var document = await JsonDocument.ParseAsync(buffer, cancellationToken: timeout.Token).ConfigureAwait(false);
        var models = LiteLlmPriceParser.Trim(document.RootElement, out var anthropic, out var openai);
        if (anthropic == 0 || openai == 0)
            throw new InvalidDataException($"listino senza modelli Anthropic ({anthropic}) o OpenAI ({openai})");

        var fresh = new ListFile(now, response.Headers.ETag?.ToString(), _options.SourceUrl.ToString(), models, IsSnapshot: false);
        WriteListFile(_options.CacheFile, fresh.FetchedAt, fresh.ETag, fresh.Source, fresh.Models);
        lock (_gate) _cache = fresh;
        _lastError = null;
        _options.LogInfo?.Invoke($"Listino prezzi aggiornato: {anthropic} modelli Anthropic, {openai} OpenAI");
        Publish(fresh);
    }

    private void Publish(ListFile? list)
    {
        var merged = list?.Models.DeepClone().AsObject() ?? new JsonObject();
        var overrideActive = ApplyOverride(merged);
        using var document = JsonDocument.Parse(merged.ToJsonString());
        var parsed = LiteLlmPriceParser.Parse(document.RootElement);
        foreach (var threshold in parsed.UnknownThresholds)
        {
            bool first;
            lock (_gate) first = _loggedThresholds.Add(threshold);
            if (first) _options.LogInfo?.Invoke($"Listino prezzi: fascia oltre {threshold} token ignorata (soglia sconosciuta)");
        }

        var origin = list is null ? PriceListOrigin.None : list.IsSnapshot ? PriceListOrigin.Snapshot : PriceListOrigin.Downloaded;
        _current = new PriceCatalog(parsed.Models, origin, list?.FetchedAt, overrideActive);
        RaiseChanged();
    }

    /// <summary>Applies prices-override.json field by field; false when there is none or it is unreadable.</summary>
    private bool ApplyOverride(JsonObject merged)
    {
        string text;
        try
        {
            if (!File.Exists(_options.OverrideFile)) return false;
            text = File.ReadAllText(_options.OverrideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("prices-override.json non leggibile", ex);
            return false;
        }

        try
        {
            if (JsonNode.Parse(text) is not JsonObject overrides)
                throw new JsonException("prices-override.json deve essere un oggetto: id modello → campi di prezzo");
            foreach (var (id, value) in overrides)
            {
                if (value is not JsonObject fields) continue;
                if (merged[id] is not JsonObject target)
                {
                    target = new JsonObject();
                    merged[id] = target;
                }
                foreach (var (name, field) in fields) target[name] = field?.DeepClone();
            }
            _loggedOverride = null;
            return true;
        }
        catch (JsonException ex)
        {
            // Logged once per content: LoadLocal runs at every download, the same broken file must not flood the log.
            if (_loggedOverride != text)
            {
                _loggedOverride = text;
                Report("prices-override.json non valido: ignorato", ex);
            }
            return false;
        }
    }

    private ListFile? ReadSnapshot()
    {
        try
        {
            using var stream = _options.Snapshot();
            return stream is null ? null : Parse(JsonNode.Parse(stream), isSnapshot: true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException)
        {
            Report("Listino prezzi: copia imbarcata non leggibile", ex);
            return null;
        }
    }

    private ListFile? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(JsonNode.Parse(File.ReadAllText(path)), isSnapshot: false) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            Report("Listino prezzi: cache non leggibile, verrà riscaricata", ex);
            return null;
        }
    }

    private static ListFile? Parse(JsonNode? node, bool isSnapshot)
    {
        if (node is not JsonObject root || root["models"] is not JsonObject models) return null;
        var fetchedAt = DateTimeOffset.Parse((string?)root["fetchedAt"] ?? throw new FormatException("fetchedAt mancante"), CultureInfo.InvariantCulture);
        return new ListFile(fetchedAt, (string?)root["etag"], (string?)root["source"] ?? "", models.DeepClone().AsObject(), isSnapshot);
    }

    private void RaiseChanged()
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { Report("Listino prezzi: un ascoltatore ha sollevato un'eccezione", ex); }
        }
    }

    private void Report(string message, Exception? ex)
    {
        try { _options.LogError?.Invoke(message, ex); } catch { /* a broken logger must not break the price list */ }
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException => "timeout",
        JsonException => "risposta non valida",
        _ => ex.Message
    };

    private sealed record ListFile(DateTimeOffset FetchedAt, string? ETag, string Source, JsonObject Models, bool IsSnapshot);
}
