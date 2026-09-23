using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;
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

    // Indented and without escaping '+' in fetchedAt: a regenerated snapshot must give a readable diff.
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // A hand-written override may repeat a key by mistake: refused at any depth, as a JsonException.
    private static readonly JsonDocumentOptions OverrideJson = new() { AllowDuplicateProperties = false };

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
            lock (_gate) return _cache is null || CacheAge.IsStale(_cache.FetchedAt, _options.Time.GetUtcNow(), _options.MaxAge);
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
        // A zero timeout never blocks, so the token adds nothing to the wait; checking it first keeps a cancelled call quiet.
        if (cancellationToken.IsCancellationRequested || !_downloading.Wait(0)) return;
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
        try
        {
            File.WriteAllText(tmp, document.ToJsonString(WriteOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        ListFile? cache;
        lock (_gate) cache = _cache;

        using var request = new HttpRequestMessage(HttpMethod.Get, _options.SourceUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        if (cache?.ETag is { Length: > 0 } etag) request.Headers.TryAddWithoutValidation("If-None-Match", etag);

        // On the injected clock, like the cache age.
        using var deadline = new CancellationTokenSource(_options.Timeout, _options.Time);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        using var response = await _options.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var now = _options.Time.GetUtcNow();

        if (response.StatusCode == HttpStatusCode.NotModified && cache is not null)
        {
            var renewed = cache with { FetchedAt = now };
            lock (_gate) _cache = renewed;
            _lastError = null;
            _options.LogInfo?.Invoke("Listino prezzi invariato (304)");
            Publish(renewed);
            SaveCache(renewed);
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
        lock (_gate) _cache = fresh;
        _lastError = null;
        _options.LogInfo?.Invoke($"Listino prezzi aggiornato: {anthropic} modelli Anthropic, {openai} OpenAI");
        Publish(fresh);
        SaveCache(fresh);
    }

    /// <summary>
    /// Writes the cache file after the list is already in use, and only logs a failure (a read-only file, a stuck
    /// .tmp, a full disk): the list stays fresh in memory. Throwing here would throw the download away and repeat it —
    /// 2.8 MB — at every refresh click, settings save and 6 h check for as long as the file cannot be written.
    /// </summary>
    private void SaveCache(ListFile list)
    {
        try
        {
            WriteListFile(_options.CacheFile, list.FetchedAt, list.ETag, list.Source, list.Models);
        }
        catch (Exception ex)
        {
            Report("Listino prezzi: cache non scrivibile, il listino scaricato resta in memoria", ex);
        }
    }

    private void Publish(ListFile? list)
    {
        var models = list?.Models ?? new JsonObject();
        var patched = ApplyOverride(models);
        using var document = JsonDocument.Parse((patched ?? models).ToJsonString());
        var parsed = LiteLlmPriceParser.Parse(document.RootElement);
        foreach (var threshold in parsed.UnknownThresholds)
        {
            bool first;
            lock (_gate) first = _loggedThresholds.Add(threshold);
            if (first) _options.LogInfo?.Invoke($"Listino prezzi: fascia oltre {threshold} token ignorata (soglia sconosciuta)");
        }

        var origin = list is null ? PriceListOrigin.None : list.IsSnapshot ? PriceListOrigin.Snapshot : PriceListOrigin.Downloaded;
        _current = new PriceCatalog(parsed.Models, origin, list?.FetchedAt, patched is not null);
        RaiseChanged();
    }

    /// <summary>
    /// A copy of <paramref name="models"/> with prices-override.json applied field by field (unknown ids added); null
    /// when there is none or it is unreadable. <paramref name="models"/> is never changed, so a file that fails halfway
    /// leaves no entry of it applied.
    /// </summary>
    private JsonObject? ApplyOverride(JsonObject models)
    {
        string text;
        try
        {
            if (!File.Exists(_options.OverrideFile)) return null;
            text = File.ReadAllText(_options.OverrideFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report("prices-override.json non leggibile", ex);
            return null;
        }

        try
        {
            if (JsonNode.Parse(text, documentOptions: OverrideJson) is not JsonObject overrides)
                throw new JsonException("prices-override.json deve essere un oggetto: id modello → campi di prezzo");
            var patched = models.DeepClone().AsObject();
            foreach (var (id, value) in overrides)
            {
                if (value is not JsonObject fields) continue;
                if (patched[id] is not JsonObject target)
                {
                    target = new JsonObject();
                    patched[id] = target;
                }
                foreach (var (name, field) in fields) target[name] = field?.DeepClone();
            }
            lock (_gate) _loggedOverride = null;
            return patched;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            // Logged once per content: Publish applies the override at every load and every download, the same broken
            // file must not flood the log. ArgumentException: a key written twice, should a JsonObject meet one.
            bool first;
            lock (_gate)
            {
                first = _loggedOverride != text;
                _loggedOverride = text;
            }
            if (first) Report("prices-override.json non valido: ignorato", ex);
            return null;
        }
    }

    private ListFile? ReadSnapshot()
    {
        try
        {
            using var stream = _options.Snapshot();
            return stream is null ? null : Parse(JsonNode.Parse(stream), isSnapshot: true);
        }
        catch (Exception ex) when (ex is JsonException or IOException or FormatException or InvalidOperationException or ArgumentException)
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
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidOperationException or ArgumentException)
        {
            Report("Listino prezzi: cache non leggibile, verrà riscaricata", ex);
            return null;
        }
    }

    /// <summary>
    /// A list file, read through its first two levels: a key written twice there (a damaged file: <see cref="WriteListFile"/>
    /// never writes one) throws ArgumentException now, in the reader, rather than later while the override is merged in.
    /// </summary>
    private static ListFile? Parse(JsonNode? node, bool isSnapshot)
    {
        if (node is not JsonObject root || root["models"] is not JsonObject models) return null;
        foreach (var (_, entry) in models)
            if (entry is JsonObject fields) _ = fields.Count;
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
        // The caller did not cancel (RefreshIfStaleAsync filters that out), so a cancellation is the timeout.
        OperationCanceledException => "timeout",
        JsonException => "risposta non valida",
        _ => ex.Message
    };

    private sealed record ListFile(DateTimeOffset FetchedAt, string? ETag, string Source, JsonObject Models, bool IsSnapshot);
}
