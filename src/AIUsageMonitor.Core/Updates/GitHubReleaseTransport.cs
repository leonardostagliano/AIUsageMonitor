namespace AIUsageMonitor.Core.Updates;

// CONTRATTO — implementazione assegnata all'agente "Trasporto". Firme pubbliche da non cambiare.
/// <summary>
/// Porting di ChessAdvisor <c>transport.ts</c> su <see cref="HttpClient"/>: redirect seguiti a mano (max 5) e
/// verificati con <see cref="UpdateUrlPolicy"/>, token solo verso api.github.com, corpi limitati, errori classificati.
/// </summary>
public sealed class GitHubReleaseTransport : IReleaseTransport, IDisposable
{
    public const string UserAgent = "AIUsageMonitor-Updater";
    public const string ApiVersion = "2026-03-10";

    /// <param name="handler">Deve avere i redirect automatici disattivati (lo fa <see cref="CreateDefault"/>).</param>
    public GitHubReleaseTransport(HttpMessageHandler handler, bool disposeHandler = true) => throw new NotImplementedException();

    /// <summary>Handler di produzione: SocketsHttpHandler senza redirect automatici ne' decompressione, proxy di sistema.</summary>
    public static GitHubReleaseTransport CreateDefault() => throw new NotImplementedException();

    public Task<System.Text.Json.JsonDocument> ReadJsonAsync(string relativePath, string token, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<byte[]> ReadAssetBytesAsync(long assetId, string token, long maxBytes, CancellationToken cancellationToken) => throw new NotImplementedException();

    public Task<DownloadResult> DownloadAssetAsync(long assetId, string destinationPath, long expectedSize, string token,
        IProgress<long>? progress, CancellationToken cancellationToken) => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}

// CONTRATTO — implementazione assegnata all'agente "Trasporto".
/// <summary>Solo endpoint ufficiali: API del repository, download delle sue release, CDN degli asset di GitHub.</summary>
public static class UpdateUrlPolicy
{
    public static bool IsPermitted(Uri url, bool asset) => throw new NotImplementedException();
}
