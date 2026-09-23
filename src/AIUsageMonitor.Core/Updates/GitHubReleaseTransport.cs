using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;

// I test regolano i timeout interni (30 s, 10 minuti, 2 s) senza doverli attendere davvero.
[assembly: InternalsVisibleTo("AIUsageMonitor.Tests")]

namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Porting di ChessAdvisor <c>transport.ts</c> su <see cref="HttpClient"/>: redirect seguiti a mano (max 5) e
/// verificati con <see cref="UpdateUrlPolicy"/>, token solo verso api.github.com, corpi limitati, errori classificati.
/// </summary>
/// <remarks>
/// Ogni errore che esce dai metodi pubblici e' una <see cref="UpdateException"/> con un messaggio fisso: ne' il corpo
/// delle risposte di GitHub ne' gli URL (i redirect verso le CDN portano una firma nella query) finiscono nei messaggi.
/// </remarks>
public sealed partial class GitHubReleaseTransport : IReleaseTransport, IDisposable
{
    public const string UserAgent = "AIUsageMonitor-Updater";
    public const string ApiVersion = "2026-03-10";

    private const int MaxRedirects = 5;
    private const long MaxJsonBytes = 8L * 1024 * 1024;
    private const int MaxErrorBodyBytes = 16 * 1024;
    private const int ChunkBytes = 81_920;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly HttpClient _http;
    private int _disposed;

    /// <param name="handler">Deve avere i redirect automatici disattivati (lo fa <see cref="CreateDefault"/>).</param>
    public GitHubReleaseTransport(HttpMessageHandler handler, bool disposeHandler = true)
    {
        ArgumentNullException.ThrowIfNull(handler);
        // Un redirect seguito dall'handler salterebbe la verifica di ogni passaggio fatta da UpdateUrlPolicy.
        if (handler is SocketsHttpHandler { AllowAutoRedirect: true } or HttpClientHandler { AllowAutoRedirect: true })
            throw new ArgumentException("L'handler HTTP dell'updater deve avere i redirect automatici disattivati.", nameof(handler));
        // I limiti di tempo sono gestiti per singola operazione: il timeout globale di HttpClient resta disattivato.
        _http = new HttpClient(handler, disposeHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Durata massima di <see cref="ReadJsonAsync"/> e <see cref="ReadAssetBytesAsync"/> (redirect compresi).</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Durata massima (wall-clock) di <see cref="DownloadAssetAsync"/>.</summary>
    internal TimeSpan DownloadTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Attesa massima degli header di ogni passaggio e tra due blocchi del corpo, come il timeout di inattivita' del
    /// socket in ChessAdvisor: una connessione ferma non tiene occupato il download per tutti i 10 minuti.
    /// </summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Tempo concesso alla lettura del corpo di un 403 per classificarlo.</summary>
    internal TimeSpan ErrorBodyTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Handler di produzione: SocketsHttpHandler senza redirect automatici ne' decompressione, proxy di sistema.</summary>
    public static GitHubReleaseTransport CreateDefault() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        // Proxy di sistema (HttpClient.DefaultProxy) con le credenziali di Windows, come il resto delle app della rete.
        UseProxy = true,
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    }, disposeHandler: true);

    public async Task<JsonDocument> ReadJsonAsync(string relativePath, string token, CancellationToken cancellationToken)
    {
        var url = ApiUrl(relativePath);
        var bytes = await RunAsync(url, token, asset: false, RequestTimeout, cancellationToken,
            (response, ct) => ReadBoundedAsync(response.Content, MaxJsonBytes, ct)).ConfigureAwait(false);
        // JsonDocument non valida l'UTF-8 dei valori: GetString() lancerebbe piu' tardi, fuori da ogni classificazione.
        if (!Utf8.IsValid(bytes)) throw new UpdateException("UPDATES_RESPONSE", UpdateMessages.MetadataInvalid);
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new UpdateException("UPDATES_RESPONSE", UpdateMessages.MetadataInvalid);
        }
    }

    public async Task<byte[]> ReadAssetBytesAsync(long assetId, string token, long maxBytes, CancellationToken cancellationToken)
    {
        var url = AssetUrl(assetId);
        return await RunAsync(url, token, asset: true, RequestTimeout, cancellationToken,
            (response, ct) => ReadBoundedAsync(response.Content, maxBytes, ct)).ConfigureAwait(false);
    }

    public async Task<DownloadResult> DownloadAssetAsync(long assetId, string destinationPath, long expectedSize, string token,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var url = AssetUrl(assetId);
        // Una dimensione attesa impossibile non vale una richiesta ne' un file vuoto sul disco.
        if (expectedSize <= 0 || expectedSize > UpdateSource.MaxPackageBytes)
            throw new UpdateException("UPDATES_SIZE", UpdateMessages.PackageSizeMismatch);
        return await RunAsync(url, token, asset: true, DownloadTimeout, cancellationToken,
            (response, ct) => WriteFileAsync(response.Content, destinationPath, expectedSize, progress, ct)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _http.Dispose();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Richiesta con durata massima <paramref name="timeout"/> collegata al token del chiamante. Come in ChessAdvisor
    /// scadenza e annullamento del chiamante sono entrambi UPDATES_TIMEOUT; un errore dopo gli header e' UPDATES_DOWNLOAD.
    /// </summary>
    private async Task<T> RunAsync<T>(Uri url, string? token, bool asset, TimeSpan timeout, CancellationToken cancellationToken,
        Func<HttpResponseMessage, CancellationToken, Task<T>> read)
    {
        if (IsDisposed) throw Closed();
        var authorization = Authorization(token);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(url, authorization, asset, cts.Token).ConfigureAwait(false);
            return await read(response, cts.Token).ConfigureAwait(false);
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception) when (cts.IsCancellationRequested)
        {
            throw TimedOut();
        }
        catch (Exception) when (IsDisposed)
        {
            throw Closed();
        }
        catch (Exception)
        {
            throw new UpdateException("UPDATES_DOWNLOAD", UpdateMessages.TransferIncomplete);
        }
        finally
        {
            response?.Dispose();
        }
    }

    /// <summary>
    /// GET con redirect seguiti uno per uno: ogni destinazione e' rivalidata, e ogni passaggio e' una richiesta nuova,
    /// quindi token e versione API vanno solo agli URL di api.github.com e mai alle CDN. Restituisce solo un 200.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Uri url, string? authorization, bool asset, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            if (redirects > MaxRedirects || !UpdateUrlPolicy.IsPermitted(url, asset))
                throw new UpdateException("UPDATES_URL", UpdateMessages.UrlNotAllowed);
            var api = UpdateUrlPolicy.IsApiHost(url);

            HttpResponseMessage response;
            using (var request = CreateRequest(url, api ? authorization : null, asset, api))
            using (var hop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                hop.CancelAfter(IdleTimeout);
                try
                {
                    response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, hop.Token).ConfigureAwait(false);
                }
                catch (Exception) when (token.IsCancellationRequested)
                {
                    throw TimedOut();
                }
                catch (Exception) when (IsDisposed)
                {
                    throw Closed();
                }
                catch (Exception)
                {
                    throw new UpdateException("UPDATES_NETWORK", UpdateMessages.NetworkUnreachable);
                }
            }

            var status = (int)response.StatusCode;
            if (status is 301 or 302 or 303 or 307 or 308)
            {
                try
                {
                    url = RedirectTarget(response, url);
                }
                finally
                {
                    response.Dispose();
                }
                continue;
            }
            if (status == 200) return response;
            try
            {
                throw await ResponseErrorAsync(response, api, token).ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
        }
    }

    private static HttpRequestMessage CreateRequest(Uri url, string? authorization, bool asset, bool api)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var headers = request.Headers;
        headers.TryAddWithoutValidation("User-Agent", UserAgent);
        headers.TryAddWithoutValidation("Accept", asset ? "application/octet-stream" : "application/vnd.github+json");
        headers.TryAddWithoutValidation("Accept-Encoding", "identity");
        if (api)
        {
            headers.TryAddWithoutValidation("X-GitHub-Api-Version", ApiVersion);
            if (authorization is not null) headers.TryAddWithoutValidation("Authorization", authorization);
        }
        return request;
    }

    /// <summary>
    /// Valore dell'header Authorization, o null senza token. Un token con spazi, a capo o caratteri non ASCII non puo'
    /// essere un token GitHub: la sessione salvata e' da ricollegare, e non deve arrivare negli header.
    /// </summary>
    private static string? Authorization(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        foreach (var c in token)
        {
            if (c is < '!' or > '~')
                throw new UpdateException("UPDATES_AUTH_REQUIRED", UpdateMessages.AuthRequiredUnreadable);
        }
        return "Bearer " + token;
    }

    /// <summary>Destinazione di un redirect, risolta sull'URL corrente se relativa; la validazione avviene al giro dopo.</summary>
    private static Uri RedirectTarget(HttpResponseMessage response, Uri current)
    {
        if (!response.Headers.NonValidated.TryGetValues("Location", out var values) || values.Count == 0)
            throw new UpdateException("UPDATES_URL", UpdateMessages.RedirectWithoutTarget);
        if (values.Count != 1) throw new UpdateException("UPDATES_URL", UpdateMessages.RedirectInvalid);
        var location = values.ToString().Trim();
        if (location.Length == 0) throw new UpdateException("UPDATES_URL", UpdateMessages.RedirectWithoutTarget);
        if (!Uri.TryCreate(current, location, out var next) || !next.IsAbsoluteUri)
            throw new UpdateException("UPDATES_URL", UpdateMessages.RedirectInvalid);
        return next;
    }

    /// <summary>Porting di <c>responseError()</c>: limiti di frequenza, accesso negato all'API, altri codici HTTP.</summary>
    private async Task<UpdateException> ResponseErrorAsync(HttpResponseMessage response, bool githubApi, CancellationToken token)
    {
        var status = (int)response.StatusCode;
        if (status == 429 || (status == 403 && Header(response, "x-ratelimit-remaining") == "0"))
            return RateLimited(status);
        if (githubApi && status == 401)
            return new UpdateAccessException(status, UpdateAccessReason.Credentials, UpdateMessages.AccessCredentials);
        if (githubApi && status == 404)
            return new UpdateAccessException(status, UpdateAccessReason.NotFound, UpdateMessages.AccessNotFound);
        if (githubApi && status == 403)
        {
            var sso = Header(response, "x-github-sso");
            if (sso is not null && SsoRequired().IsMatch(sso))
                return new UpdateAccessException(status, UpdateAccessReason.Sso, UpdateMessages.AccessSso);
            var message = await ErrorMessageAsync(response.Content, token).ConfigureAwait(false);
            if (RateLimitMessage().IsMatch(message)) return RateLimited(status);
            if (OAuthPolicyMessage().IsMatch(message))
                return new UpdateAccessException(status, UpdateAccessReason.OAuthPolicy, UpdateMessages.AccessOAuthPolicy);
            if (PermissionsMessage().IsMatch(message))
                return new UpdateAccessException(status, UpdateAccessReason.Permissions, UpdateMessages.AccessPermissions);
            return new UpdateAccessException(status, UpdateAccessReason.Forbidden, UpdateMessages.AccessForbidden);
        }
        return new UpdateException("UPDATES_HTTP", UpdateMessages.HttpFailed(status));
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values) ? values.ToString().Trim() : null;

    /// <summary>
    /// Campo <c>message</c> (in minuscolo) di un corpo di errore JSON letto con limiti di dimensione (16 KB) e tempo
    /// (2 s); stringa vuota se manca o non e' leggibile. Serve solo a classificare: non esce mai dal trasporto.
    /// </summary>
    private async Task<string> ErrorMessageAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxErrorBodyBytes) return "";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(ErrorBodyTimeout);
        try
        {
            var stream = await content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var buffer = new byte[MaxErrorBodyBytes + 1];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                }
                if (total > MaxErrorBodyBytes) return "";
                using var document = JsonDocument.Parse(buffer.AsMemory(0, total));
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String
                    ? message.GetString()!.ToLowerInvariant()
                    : "";
            }
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Corpo intero in memoria, al massimo <paramref name="maxBytes"/> (Content-Length controllato prima).</summary>
    private async Task<byte[]> ReadBoundedAsync(HttpContent content, long maxBytes, CancellationToken token)
    {
        if (content.Headers.ContentLength > maxBytes) throw new UpdateException("UPDATES_SIZE", UpdateMessages.ResponseTooLarge);
        var limit = Math.Min(maxBytes, Array.MaxLength);
        var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            using var memory = new MemoryStream();
            var buffer = new byte[ChunkBytes];
            long total = 0;
            while (true)
            {
                var read = await ReadChunkAsync(stream, buffer, idle).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
                if (total > limit) throw new UpdateException("UPDATES_SIZE", UpdateMessages.ResponseTooLarge);
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
    }

    /// <summary>
    /// Download in streaming in un file nuovo ed esclusivo: SHA-256 calcolato durante la scrittura, dimensione
    /// controllata a ogni blocco, flush su disco prima di restituire l'hash. Il file parziale resta al chiamante.
    /// </summary>
    private async Task<DownloadResult> WriteFileAsync(HttpContent content, string destinationPath, long expectedSize,
        IProgress<long>? progress, CancellationToken token)
    {
        if (content.Headers.ContentLength is { } declared && (declared > UpdateSource.MaxPackageBytes || declared != expectedSize))
            throw new UpdateException("UPDATES_SIZE", UpdateMessages.PackageSizeMismatch);

        var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var file = OpenNewFile(destinationPath);
            try
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                var buffer = new byte[ChunkBytes];
                long size = 0;
                long? lastProgress = null;
                while (true)
                {
                    var read = await ReadChunkAsync(stream, buffer, idle).ConfigureAwait(false);
                    if (read == 0) break;
                    size += read;
                    if (size > expectedSize || size > UpdateSource.MaxPackageBytes)
                        throw new UpdateException("UPDATES_SIZE", UpdateMessages.PackageTooLarge);
                    hash.AppendData(buffer, 0, read);
                    try
                    {
                        await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    catch (Exception e) when (IsFileError(e))
                    {
                        throw WriteFailed();
                    }
                    if (progress is not null && (lastProgress is null || Stopwatch.GetElapsedTime(lastProgress.Value) >= ProgressInterval))
                    {
                        lastProgress = Stopwatch.GetTimestamp();
                        progress.Report(size);
                    }
                }
                if (size != expectedSize || size == 0) throw new UpdateException("UPDATES_SIZE", UpdateMessages.PackageIncomplete);
                try
                {
                    file.Flush(flushToDisk: true);
                }
                catch (Exception e) when (IsFileError(e))
                {
                    throw WriteFailed();
                }
                progress?.Report(size);
                return new DownloadResult(Convert.ToHexStringLower(hash.GetHashAndReset()), size);
            }
            finally
            {
                // Dopo il flush i dati sono gia' sul disco: un errore di chiusura non deve coprire l'esito vero.
                try { await file.DisposeAsync().ConfigureAwait(false); }
                catch (Exception e) when (IsFileError(e)) { }
            }
        }
    }

    /// <summary>File nuovo (mai uno esistente), non condiviso e, fuori da Windows, leggibile solo dall'utente.</summary>
    private static FileStream OpenNewFile(string destinationPath)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = 0
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            return new FileStream(destinationPath, options);
        }
        catch (Exception e) when (IsFileError(e))
        {
            throw WriteFailed();
        }
    }

    /// <summary>
    /// Un blocco del corpo con un'attesa massima di <see cref="IdleTimeout"/>. <paramref name="idle"/> e' figlio del
    /// token dell'operazione: se scade solo lui la connessione era ferma e <see cref="RunAsync{T}"/> lo classifica come
    /// trasferimento incompleto (UPDATES_DOWNLOAD); scadenza complessiva e annullamento restano UPDATES_TIMEOUT.
    /// Il timer si ferma dopo ogni lettura: la scrittura su un disco lento non conta come connessione ferma.
    /// </summary>
    private async ValueTask<int> ReadChunkAsync(Stream stream, byte[] buffer, CancellationTokenSource idle)
    {
        idle.CancelAfter(IdleTimeout);
        var read = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
        idle.CancelAfter(Timeout.InfiniteTimeSpan);
        return read;
    }

    private static Uri ApiUrl(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath[0] != '/'
            || !Uri.TryCreate(UpdateSource.ApiRoot + relativePath, UriKind.Absolute, out var url))
            throw new UpdateException("UPDATES_URL", UpdateMessages.UrlNotAllowed);
        return url;
    }

    private static Uri AssetUrl(long assetId)
    {
        if (assetId <= 0) throw new UpdateException("UPDATES_URL", UpdateMessages.UrlNotAllowed);
        return new Uri($"{UpdateSource.ApiRoot}/releases/assets/{assetId.ToString(CultureInfo.InvariantCulture)}");
    }

    private static bool IsFileError(Exception e) =>
        e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or System.Security.SecurityException;

    private static UpdateException RateLimited(int status) => new("UPDATES_RATE_LIMIT", UpdateMessages.RateLimit(status));
    private static UpdateException TimedOut() => new("UPDATES_TIMEOUT", UpdateMessages.RequestTimeout);
    private static UpdateException Closed() => new("UPDATES_CLOSED", UpdateMessages.Closing);
    private static UpdateException WriteFailed() => new("UPDATES_WRITE", UpdateMessages.PackageWriteFailed);

    [GeneratedRegex(@"\Arequired(?:;|\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SsoRequired();

    [GeneratedRegex("secondary rate limit|api rate limit exceeded|abuse detection", RegexOptions.CultureInvariant)]
    private static partial Regex RateLimitMessage();

    [GeneratedRegex("oauth app access restrictions|oauth application access restrictions|third.party application restrictions", RegexOptions.CultureInvariant)]
    private static partial Regex OAuthPolicyMessage();

    [GeneratedRegex("resource not accessible by (?:personal access token|integration)", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionsMessage();
}

/// <summary>Solo endpoint ufficiali: API del repository, download delle sue release, CDN degli asset di GitHub.</summary>
/// <remarks>
/// Il controllo lavora sulla forma canonica di <see cref="Uri"/>, cioe' quella che viene davvero inviata: host in
/// minuscolo (IDN compreso), segmenti <c>.</c>/<c>..</c> (anche come <c>%2e</c>) gia' risolti. Sui percorsi di
/// api.github.com e github.com non e' ammesso alcun escape <c>%</c> residuo (ad esempio <c>%2f</c>), che il server
/// potrebbe decodificare diversamente; il confronto del percorso e' esatto, maiuscole comprese.
/// </remarks>
public static class UpdateUrlPolicy
{
    private const string ApiHost = "api.github.com";
    private const string WebHost = "github.com";
    private const string ApiReleasesPath = "/repos/" + UpdateSource.Repository + "/releases";
    private const string DownloadPath = "/" + UpdateSource.Repository + "/releases/download/";

    /// <summary>Host da cui GitHub serve i file delle release (URL firmati, senza token).</summary>
    private static readonly string[] CdnHosts =
    [
        "release-assets.githubusercontent.com",
        "objects.githubusercontent.com",
        "github-releases.githubusercontent.com"
    ];

    public static bool IsPermitted(Uri url, bool asset)
    {
        if (url is null || !url.IsAbsoluteUri) return false;
        if (url.Scheme != Uri.UriSchemeHttps
            || url.UserInfo.Length > 0
            || url.Port != 443
            || url.Fragment.Length > 0
            || url.HostNameType != UriHostNameType.Dns)
            return false;
        var host = url.IdnHost;
        var path = url.AbsolutePath;
        if (host == ApiHost)
            return !path.Contains('%')
                && (path == ApiReleasesPath || path.StartsWith(ApiReleasesPath + "/", StringComparison.Ordinal));
        if (!asset) return false;
        if (host == WebHost) return !path.Contains('%') && path.StartsWith(DownloadPath, StringComparison.Ordinal);
        return CdnHosts.Contains(host, StringComparer.Ordinal);
    }

    /// <summary>Unico host a cui vanno token e versione dell'API.</summary>
    internal static bool IsApiHost(Uri url) => url.IsAbsoluteUri && url.IdnHost == ApiHost;
}
