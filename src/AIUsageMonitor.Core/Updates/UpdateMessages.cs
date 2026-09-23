namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Messaggi dell'updater, gia' in italiano come il resto dell'interfaccia: la UI mostra <see cref="UpdateStatus.Message"/>
/// e i messaggi delle eccezioni cosi' come sono. Classe partial: ogni area puo' aggiungere i propri in un file a parte.
/// </summary>
public static partial class UpdateMessages
{
    // Trasporto
    public static string AccessPrefix(int status, string detail) => $"GitHub ha rifiutato l'accesso alle release (HTTP {status}). {detail}";
    public static string RateLimit(int status) => $"GitHub ha limitato le richieste (HTTP {status}). Attendi prima di verificare nuovamente.";
    public const string AccessCredentials = "La sessione GitHub dell'app non è valida o è scaduta. Premi \"Collega GitHub e controlla\" per accedere nuovamente.";
    public const string AccessNotFound = "Il repository " + UpdateSource.Repository + " o la release richiesta non è visibile alla credenziale usata. Verifica che l'account collegato abbia accesso a questo repository.";
    public const string AccessSso = "GitHub richiede l'autorizzazione SSO della credenziale per l'organizzazione. Autorizzala nelle impostazioni GitHub e ripeti il controllo.";
    public const string AccessOAuthPolicy = "L'organizzazione limita l'accesso delle applicazioni OAuth. Richiedi l'approvazione di Git Credential Manager per l'account collegato dall'app.";
    public const string AccessPermissions = "La sessione collegata non dispone dei permessi richiesti. Verifica che l'account sia autorizzato a leggere il repository degli aggiornamenti.";
    public const string AccessForbidden = "La richiesta è stata negata. Verifica i permessi sul repository e le eventuali restrizioni dell'organizzazione per l'account usato.";
    public static string HttpFailed(int status) => $"GitHub non ha completato la richiesta di aggiornamento (HTTP {status}).";
    public const string UrlNotAllowed = "La release contiene un indirizzo di download non consentito.";
    public const string RedirectWithoutTarget = "Redirect della release privo di destinazione.";
    public const string RedirectInvalid = "Redirect della release non valido.";
    public const string RequestTimeout = "Richiesta di aggiornamento interrotta o tempo massimo superato.";
    public const string NetworkUnreachable = "Impossibile raggiungere GitHub. Verifica l'accesso HTTPS dalla rete in uso.";
    public const string TransferIncomplete = "Il trasferimento dell'aggiornamento non è stato completato.";
    public const string ResponseTooLarge = "Risposta GitHub troppo grande.";
    public const string MetadataInvalid = "GitHub ha restituito metadati della release non validi.";
    public const string PackageSizeMismatch = "La dimensione dell'eseguibile non corrisponde alla release.";
    public const string PackageTooLarge = "L'eseguibile supera la dimensione prevista.";
    public const string PackageWriteFailed = "Impossibile scrivere l'eseguibile scaricato sul disco locale.";
    public const string PackageIncomplete = "Download dell'eseguibile incompleto.";

    // Servizio
    public const string Initial = "Verifica le release pubblicate del repository di questa applicazione.";
    public const string ChecksumManifestInvalid = "Manifest dei checksum della release non valido.";
    public const string LocalPackageChanged = "L'eseguibile scaricato è cambiato sul disco: scaricalo nuovamente.";
    public const string LocalVerifyTimeout = "Verifica locale dell'eseguibile scaduta.";
    public const string LocalSizeChanged = "La dimensione dell'eseguibile scaricato è cambiata.";
    public const string LocalIncomplete = "L'eseguibile scaricato è incompleto.";
    public const string BusyAuth = "Completa prima il collegamento GitHub in corso.";
    public const string BusyUpdate = "Un aggiornamento è già in corso.";
    public const string BusyOperation = "Un'operazione di aggiornamento è già in corso.";
    public const string Closing = "L'applicazione si sta chiudendo.";
    public const string GenericFailure = "Operazione di aggiornamento non completata. Riprova dopo aver verificato rete e spazio disponibile.";
    public const string AuthRequired = "Premi \"Collega GitHub e controlla\" e accedi con l'account che vede il repository degli aggiornamenti.";
    public const string AuthRequiredUnreadable = "La sessione GitHub salvata dall'app non è leggibile. Premi \"Collega GitHub e controlla\" per accedere nuovamente.";
    public static string LinkedAccount(string account) => $" Account collegato dall'app: {account}.";
    public const string AuthInProgress = "Collegamento GitHub in corso. Nel browser scegli l'account che vede il repository e, se richiesto, completa login e autorizzazione.";
    public const string AuthSaving = "Account GitHub individuato. Salvataggio del collegamento…";
    public const string AuthCancelledNotice = "Collegamento GitHub annullato. Puoi riprovare quando vuoi.";
    public const string Disconnected = "Account GitHub scollegato: la sessione salvata dall'app è stata eliminata.";
    public const string DisconnectFailed = "Impossibile eliminare la sessione GitHub salvata dall'app.";
    public const string VersionUnstable = "La versione corrente non ha una base SemVer stabile: usa una release ufficiale o una build con versione di release.";
    public const string Checking = "Verifica delle release GitHub in corso…";
    public const string ReleaseListInvalid = "Elenco delle release GitHub non valido.";
    public const string NoRelease = "Nessuna release stabile con un eseguibile Windows x64 compatibile è disponibile.";
    public static string UpToDateDev(string base_, string latest) => $"Versione di sviluppo con base {base_}. Ultima release stabile disponibile: {latest}.";
    public const string UpToDate = "Questa applicazione è già aggiornata rispetto alle release stabili disponibili.";
    public static string Available(string version, string suffix) => $"Disponibile la versione {version}.{suffix}";
    public const string SuffixDevelopment = " Download e installazione sono disponibili solo nell'eseguibile pubblicato.";
    public const string SuffixReadOnlyLocation = " La cartella dell'eseguibile non è scrivibile: scarica la nuova versione dalla pagina della release e sostituisci l'eseguibile a mano.";
    public const string SuffixUnsupportedPlatform = " L'aggiornamento integrato è disponibile solo su Windows x64.";
    public const string SuffixNoChecksum = " La release non pubblica un checksum di confronto.";
    public const string DownloadState = "Verifica prima una nuova release dall'eseguibile Windows x64 pubblicato.";
    public const string Downloading = "Download della nuova versione in corso…";
    public const string ChecksumNotUnique = "Il manifest SHA256SUMS non contiene un checksum univoco per questo eseguibile.";
    public const string ChecksumConflict = "I checksum pubblicati da GitHub e da SHA256SUMS non coincidono.";
    public const string IntegrityMismatch = "Checksum SHA-256 non corrispondente: file scaricato eliminato, aggiornamento annullato.";
    public const string NotWindowsExecutable = "Il download non è un eseguibile Windows valido.";
    public const string DownloadedVerified = "Nuova versione scaricata e SHA-256 verificato. Installa quando sei pronto a riavviare l'app.";
    public const string DownloadedUnverified = "Nuova versione scaricata via HTTPS. La release non pubblica checksum: l'integrità è confrontabile solo con il file locale scaricato.";
    public const string InstallState = "Scarica prima la nuova versione dall'eseguibile Windows x64 pubblicato.";
    public const string NotNewer = "La release scaricata non è più recente della versione corrente.";
    public const string IntegrityChangedLocally = "L'eseguibile scaricato è cambiato: installazione annullata. Scaricalo nuovamente.";
    public const string FinalVerification = "Verifica finale della nuova versione…";
    public const string InstallStarted = "Aggiornamento applicato. L'app si chiude e riparte con la nuova versione; impostazioni e hook vengono conservati.";
    public const string ReleaseChanged = "La release è cambiata dopo la verifica. Controlla nuovamente gli aggiornamenti.";
    public const string BrowserFailed = "Impossibile aprire la pagina GitHub nel browser di sistema.";

    // Credenziali
    public const string AuthCancelled = "Collegamento GitHub interrotto.";
    public const string AuthPlatform = "Il collegamento GitHub integrato richiede Git Credential Manager per Windows.";
    public const string AuthStorageUnavailable = "La protezione della sessione GitHub non è disponibile su questo PC. Il collegamento non può essere salvato.";
    public const string GcmMissing = "Git Credential Manager non disponibile. Installa Git for Windows con Git Credential Manager e riprova.";
    public const string AuthTimeout = "Il collegamento GitHub non è terminato entro tre minuti. Ripeti il collegamento e completa login e autorizzazione nel browser.";
    public const string AuthSpawnFailed = "Windows non ha avviato Git Credential Manager. Verifica l'installazione di Git for Windows.";
    public static string AuthProcessFailed(int? code) =>
        $"Git Credential Manager ha interrotto il collegamento{(code is null ? "." : $" (codice {code}).")} Ripeti il collegamento e completa l'autorizzazione GitHub nel browser.";
    public static string AuthCredentialInvalid(string detail) => $"Git Credential Manager ha concluso il comando senza restituire una sessione valida. {detail}";
    public const string DetailMissingAccount = "Manca un nome account valido nella risposta OAuth.";
    public const string DetailMissingToken = "Manca un token valido nella risposta OAuth.";
    public const string DetailInvalidScope = "La risposta non corrisponde all'accesso HTTPS a GitHub.";
    public const string DetailOversize = "La risposta OAuth supera la dimensione consentita.";
    public const string AuthSaveFailed = "Accesso GitHub completato, ma non è stato possibile salvare la sessione cifrata dell'app. Verifica che la cartella dati dell'app sia scrivibile.";

    // Installazione (sostituzione dell'eseguibile)
    public const string InstallNotStarted = "Windows non ha avviato la nuova versione: è stata ripristinata quella corrente. Verifica autorizzazioni e protezione del sistema.";
    public const string InstallReplaceFailed = "Impossibile sostituire l'eseguibile corrente: la versione in uso è stata ripristinata.";
    public const string InstallVersionMismatch = "La versione scritta nell'eseguibile scaricato non corrisponde alla release.";
}
