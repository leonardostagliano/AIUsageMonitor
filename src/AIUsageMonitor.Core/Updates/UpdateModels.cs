namespace AIUsageMonitor.Core.Updates;

/// <summary>Fasi dell'updater. La UI mostra lo stato, il servizio possiede ogni chiamata di rete.</summary>
public enum UpdatePhase
{
    Idle,
    Authenticating,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Downloaded,
    Installing,
    Error
}

/// <summary>
/// Come e' stato avviato l'eseguibile corrente. Solo <see cref="Supported"/> puo' scaricare e sostituire se stesso:
/// un avvio da <c>dotnet run</c>/cartella bin e' <see cref="Development"/>, un exe in una cartella non scrivibile
/// (es. Program Files) e' <see cref="ReadOnlyLocation"/>, un sistema diverso da Windows x64 e'
/// <see cref="UnsupportedPlatform"/>.
/// </summary>
public enum InstallationKind
{
    Development,
    Supported,
    ReadOnlyLocation,
    UnsupportedPlatform
}

/// <summary>Variante pubblicata dalla release: l'updater scarica sempre la stessa variante dell'exe in esecuzione.</summary>
public enum PackageVariant
{
    FrameworkDependent,
    SelfContained
}

/// <summary>Origine della credenziale usata per le release: solo la sessione creata dall'app stessa.</summary>
public enum UpdateAuthSource
{
    NotChecked,
    GitHubApp,
    Anonymous
}

public enum CredentialFailure
{
    NotConnected,
    StoredCredentialUnavailable
}

/// <summary>Fonte del checksum di confronto per l'eseguibile di una release.</summary>
public enum ChecksumSource
{
    Sha256Sums,
    GitHubDigest,
    Unavailable
}

/// <summary>Vista di una release candidata, senza segreti ne' URL firmati: puo' finire nella UI.</summary>
public sealed record UpdateRelease(
    string Version,
    string Tag,
    string PublishedAt,
    string Url,
    string Notes,
    string AssetName,
    long AssetSize,
    ChecksumSource Checksum);

public sealed record UpdateDownload(
    long ReceivedBytes,
    long TotalBytes,
    int Percent,
    string? Sha256,
    bool Verified);

/// <summary>
/// Istantanea immutabile dello stato dell'updater. <see cref="Revision"/> cresce a ogni cambiamento, cosi' chi riceve
/// le istantanee fuori ordine (eventi da thread diversi) puo' scartare quelle vecchie.
/// </summary>
public sealed record UpdateStatus(
    long Revision,
    UpdatePhase Phase,
    string CurrentVersion,
    InstallationKind Installation,
    PackageVariant Variant,
    string Repository,
    string RepositoryUrl,
    bool AutoCheck,
    DateTimeOffset? CheckedAt,
    UpdateAuthSource AuthSource,
    string? GitHubAccount,
    UpdateRelease? Release,
    UpdateDownload? Download,
    bool CanDownload,
    bool CanInstall,
    string Message,
    string? ErrorCode);

/// <summary>Sessione GitHub letta dallo store dell'app. Il token non va mai loggato ne' mostrato.</summary>
public sealed record UpdateCredential(UpdateAuthSource Source, string? Token, string? Account, CredentialFailure? Failure)
{
    public static UpdateCredential Connected(string token, string account) => new(UpdateAuthSource.GitHubApp, token, account, null);
    public static UpdateCredential Missing(CredentialFailure failure) => new(UpdateAuthSource.Anonymous, null, null, failure);
}

/// <summary>Un asset caricato di una release GitHub. <see cref="Digest"/> e' lo SHA-256 esadecimale minuscolo, se GitHub lo pubblica.</summary>
public sealed record ReleaseAsset(long Id, string Name, long Size, string? Digest);

/// <summary>Release stabile con esattamente un eseguibile della variante richiesta e, se presente, il manifest SHA256SUMS.</summary>
public sealed record ReleaseCandidate(long ReleaseId, UpdateRelease View, ReleaseAsset Package, ReleaseAsset? Checksums);

/// <summary>Esito di un download in streaming: hash calcolato durante la scrittura e byte scritti.</summary>
public sealed record DownloadResult(string Sha256, long Size);

/// <summary>Eseguibile scaricato, verificato e pronto per la sostituzione.</summary>
public sealed record StagedUpdate(string Path, string Sha256, string? ExpectedHash, long Size, string Version);
