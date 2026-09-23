using System.Text.Json;

namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Accesso in sola lettura alle release del repository fissato in <see cref="UpdateSource"/>. Ogni metodo accetta solo
/// indirizzi ufficiali (API del repository, download della release, CDN di GitHub), segue i redirect uno per uno
/// verificandoli e manda il token solo ad <c>api.github.com</c>. Gli errori sono sempre <see cref="UpdateException"/>.
/// </summary>
public interface IReleaseTransport
{
    /// <summary>GET JSON su <c>{ApiRoot}{relativePath}</c> (es. <c>/releases?per_page=100&amp;page=1</c>), al massimo 8 MB, 30 s.</summary>
    Task<JsonDocument> ReadJsonAsync(string relativePath, string token, CancellationToken cancellationToken);

    /// <summary>Contenuto di un asset piccolo (es. SHA256SUMS.txt) letto in memoria, al massimo <paramref name="maxBytes"/>.</summary>
    Task<byte[]> ReadAssetBytesAsync(long assetId, string token, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Scarica un asset in streaming in un file NUOVO (<see cref="FileMode.CreateNew"/>) calcolando lo SHA-256 durante la
    /// scrittura. La dimensione deve coincidere esattamente con <paramref name="expectedSize"/> e non superare
    /// <see cref="UpdateSource.MaxPackageBytes"/>; durata massima 10 minuti. <paramref name="progress"/> riceve i byte
    /// ricevuti, al piu' ogni 200 ms e sempre alla fine. In caso di errore il file parziale resta al chiamante.
    /// </summary>
    Task<DownloadResult> DownloadAssetAsync(long assetId, string destinationPath, long expectedSize, string token,
        IProgress<long>? progress, CancellationToken cancellationToken);
}

/// <summary>Protezione a riposo della sessione GitHub (DPAPI utente corrente su Windows; un fake nei test).</summary>
public interface ISecretProtector
{
    /// <summary>Nome scritto nel file della sessione, es. <c>dpapi-current-user</c>.</summary>
    string CipherName { get; }
    bool IsAvailable { get; }
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

/// <summary>Unica fonte di credenziali per controlli e download: la sessione che l'app ha creato da se'.</summary>
public interface IUpdateCredentialStore
{
    /// <summary>Non lancia: un file assente da' NotConnected, uno illeggibile/non valido StoredCredentialUnavailable.</summary>
    UpdateCredential Read();

    /// <summary>Salva cifrato e in modo atomico; lancia <see cref="UpdateException"/> (UPDATES_AUTH_SAVE) se non riesce.</summary>
    void Save(string token, string account);

    /// <summary>Elimina la sessione salvata; un file gia' assente non e' un errore.</summary>
    void Delete();
}

/// <summary>Collegamento esplicito dell'account: OAuth nel browser via Git Credential Manager, poi salvataggio cifrato.</summary>
public interface IGitHubLogin
{
    /// <summary>
    /// Avvia il login interattivo e salva la sessione nello store. <paramref name="credentialReady"/> viene chiamato quando
    /// GCM ha restituito una credenziale valida, prima del salvataggio. Lancia <see cref="UpdateException"/>: tra gli altri
    /// UPDATES_AUTH_CANCELLED se <paramref name="cancellationToken"/> viene cancellato.
    /// </summary>
    Task<UpdateCredential> AuthenticateAsync(Action? credentialReady, CancellationToken cancellationToken);
}

/// <summary>Esecuzione di un processo figlio con stdin/stdout limitati, stderr scartato. Astrae <c>Process</c> per i test.</summary>
public interface ICredentialProcessRunner
{
    Task<CredentialProcessResult> RunAsync(CredentialProcessRequest request, CancellationToken cancellationToken);
}

public sealed record CredentialProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment,
    string WorkingDirectory,
    string StandardInput,
    int MaxStandardOutputBytes,
    TimeSpan Timeout);

public enum CredentialProcessOutcome
{
    Exited,
    SpawnFailed,
    TimedOut,
    Cancelled,
    Oversize
}

/// <summary>Stdout grezzo: chi lo riceve lo azzera con <c>CryptographicOperations.ZeroMemory</c> dopo il parsing.</summary>
public sealed record CredentialProcessResult(CredentialProcessOutcome Outcome, int? ExitCode, byte[] StandardOutput);

/// <summary>
/// Applica un eseguibile verificato al posto di quello in esecuzione e avvia la nuova versione. Implementato dall'App
/// (Windows): il Core decide solo quando chiamarlo.
/// </summary>
public interface IUpdateInstaller
{
    /// <summary>Tipo di installazione dell'eseguibile corrente; puo' fare IO leggero (prova di scrittura nella cartella).</summary>
    InstallationKind DetectInstallation();

    /// <summary>
    /// Sostituisce l'eseguibile corrente con <paramref name="staged"/> e avvia la nuova versione. Al ritorno la nuova
    /// istanza e' partita e aspetta la chiusura di questo processo; se un passo fallisce ripristina l'eseguibile
    /// originale e lancia <see cref="UpdateException"/>.
    /// </summary>
    Task InstallAsync(StagedUpdate staged, CancellationToken cancellationToken);
}

/// <summary>I comandi dell'updater di cui ha bisogno la conferma "scarica e riavvia" (implementata da UpdateService).</summary>
public interface IUpdateCommands
{
    UpdateStatus Status { get; }

    /// <summary>Alzato da thread qualsiasi con l'istantanea appena prodotta.</summary>
    event Action<UpdateStatus>? Changed;

    Task<UpdateStatus> DownloadAsync();
    Task<UpdateStatus> InstallAsync();
}
