using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Macchina a stati dell'updater (porting di ChessAdvisor <c>AppUpdateService</c>). Controllo automatico opzionale
/// (15 s dopo l'avvio, poi ogni 6 ore) solo con una sessione GitHub dell'app; download e installazione sono comandi
/// espliciti separati. Thread-safe: i metodi possono essere chiamati da qualsiasi thread, <see cref="Changed"/> viene
/// alzato da thread qualsiasi.
/// </summary>
/// <remarks>
/// Lo stato vive sotto un unico lock e ogni cambiamento produce un'istantanea immutabile con <see cref="UpdateStatus.Revision"/>
/// crescente; <see cref="Changed"/> viene alzato fuori dal lock, quindi due istantanee possono arrivare fuori ordine:
/// chi le riceve scarta quelle con revisione minore. Le API pubbliche falliscono solo con <see cref="UpdateException"/>.
/// </remarks>
public sealed partial class UpdateService : IUpdateCommands, IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ObsoleteCleanupDelay = TimeSpan.FromSeconds(30);

    /// <summary>Quanto Dispose aspetta una sostituzione dell'exe gia' partita (copia verificata + due rename).</summary>
    public static readonly TimeSpan InstallShutdownTimeout = TimeSpan.FromSeconds(30);

    private const string ReleasesPath = "/releases?per_page=100&page=1";
    private const string PartialSuffix = ".part";
    private const int MaxCleanupEntries = 200;
    private const char ByteOrderMark = (char)0xFEFF;
    private static readonly TimeSpan PreferenceCheckDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LocalVerifyTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ObsoleteDownloadAge = TimeSpan.FromDays(7);

    private const string CodeClosed = "UPDATES_CLOSED";
    private const string CodeBusy = "UPDATES_BUSY";
    private const string CodeGeneric = "UPDATES_ERROR";
    private const string CodeAuthRequired = "UPDATES_AUTH_REQUIRED";
    private const string CodeAuthCancelled = "UPDATES_AUTH_CANCELLED";
    private const string CodeVersion = "UPDATES_VERSION";
    private const string CodeNoRelease = "UPDATES_NO_RELEASE";
    private const string CodeDownloadState = "UPDATES_DOWNLOAD_STATE";
    private const string CodeInstallState = "UPDATES_INSTALL_STATE";
    private const string CodeChecksum = "UPDATES_CHECKSUM";
    private const string CodeIntegrity = "UPDATES_INTEGRITY";
    private const string CodeFormat = "UPDATES_FORMAT";
    private const string CodeDisconnect = "UPDATES_DISCONNECT";

    private readonly UpdateServiceOptions _options;
    private readonly object _gate = new();

    // Tutto lo stato qui sotto e' protetto da _gate (tranne _loaded, letto anche senza lock come scorciatoia).
    private UpdateStatus _status;
    private ReleaseCandidate? _candidate;
    private StagedUpdate? _staged;
    private CancellationTokenSource? _active;
    private Task? _installing;
    private Task<UpdateStatus>? _checking;
    private Task<UpdateStatus>? _authenticating;
    private ITimer? _checkTimer;
    private long _checkTimerGeneration;
    private ITimer? _cleanupTimer;
    private bool _disposed;
    private volatile bool _loaded;

    public UpdateService(UpdateServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        // Installazione, preferenza e sessione arrivano al caricamento (EnsureLoaded), prima di qualunque lettura dello
        // stato: questa istantanea provvisoria non e' mai visibile.
        _status = new UpdateStatus(
            Revision: 0,
            Phase: UpdatePhase.Idle,
            CurrentVersion: options.CurrentVersion,
            Installation: InstallationKind.Development,
            Variant: options.Variant,
            Repository: UpdateSource.Repository,
            RepositoryUrl: UpdateSource.RepositoryUrl,
            AutoCheck: false,
            CheckedAt: null,
            AuthSource: UpdateAuthSource.NotChecked,
            GitHubAccount: null,
            Release: null,
            Download: null,
            CanDownload: false,
            CanInstall: false,
            Message: UpdateMessages.Initial,
            ErrorCode: null);
    }

    public event Action<UpdateStatus>? Changed;

    public UpdateStatus Status
    {
        get
        {
            EnsureLoaded();
            lock (_gate) return _status;
        }
    }

    /// <summary>Legge la sessione salvata, pianifica la pulizia dei vecchi download e il primo controllo automatico.</summary>
    public Task StartAsync()
    {
        try
        {
            EnsureLoaded();
            bool firstCheck;
            UpdateStatus status;
            lock (_gate)
            {
                if (_disposed) return Task.CompletedTask;
                // I vecchi eseguibili possono essere grandi: la scansione aspetta per non competere con l'avvio.
                _cleanupTimer ??= _options.Time.CreateTimer(OnCleanupTimer, null, ObsoleteCleanupDelay, Timeout.InfiniteTimeSpan);
                // Senza una sessione salvata un controllo automatico potrebbe solo fallire: si aspetta il collegamento esplicito.
                firstCheck = _status.AutoCheck && _status.AuthSource == UpdateAuthSource.GitHubApp;
                status = _status;
            }
            LogInfo($"Updates: current version {status.CurrentVersion}, installation {status.Installation}, variant {status.Variant}, " +
                    $"automatic check {(status.AutoCheck ? "on" : "off")}, GitHub session {(status.AuthSource == UpdateAuthSource.GitHubApp ? "present" : "absent")}");
            if (firstCheck) Schedule(FirstCheckDelay);
        }
        catch (Exception ex)
        {
            // Gli aggiornamenti sono opzionali: un avvio fallito non deve bloccare l'app.
            LogError("Updates: start failed", ex);
        }
        return Task.CompletedTask;
    }

    /// <summary>La preferenza e' cambiata nelle impostazioni: aggiorna lo stato e ripianifica.</summary>
    public void PreferencesChanged(bool autoCheck)
    {
        try
        {
            UpdateStatus snapshot;
            lock (_gate)
            {
                if (!_loaded || _disposed || _status.AutoCheck == autoCheck) return;
                CancelCheckTimerLocked();
                snapshot = CommitLocked(_status with { AutoCheck = autoCheck });
            }
            Raise(snapshot);
            LogInfo($"Updates: automatic check {(autoCheck ? "enabled" : "disabled")}");
            if (autoCheck) Schedule(PreferenceCheckDelay);
        }
        catch (Exception ex)
        {
            LogError("Updates: preference change failed", ex);
        }
    }

    public Task<UpdateStatus> CheckAsync()
    {
        try
        {
            EnsureLoaded();
            TaskCompletionSource<UpdateStatus> completion;
            lock (_gate)
            {
                if (_disposed) return Task.FromException<UpdateStatus>(Closed());
                if (_authenticating is not null) return Task.FromException<UpdateStatus>(new UpdateException(CodeBusy, UpdateMessages.BusyAuth));
                // Controlli concorrenti (timer + comando della UI) condividono la stessa operazione.
                if (_checking is not null) return _checking;
                if (_status.Phase is UpdatePhase.Downloading or UpdatePhase.Installing)
                    return Task.FromException<UpdateStatus>(new UpdateException(CodeBusy, UpdateMessages.BusyUpdate));
                completion = new TaskCompletionSource<UpdateStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                _checking = completion.Task;
            }
            _ = RunCheckAsync(completion);
            return completion.Task;
        }
        catch (Exception ex)
        {
            return Task.FromException<UpdateStatus>(Unexpected(ex, "check"));
        }
    }

    /// <summary>Solo il comando esplicito della UI entra nel login interattivo, poi verifica le release.</summary>
    public Task<UpdateStatus> AuthenticateAsync()
    {
        try
        {
            EnsureLoaded();
            TaskCompletionSource<UpdateStatus> completion;
            lock (_gate)
            {
                if (_disposed) return Task.FromException<UpdateStatus>(Closed());
                if (_authenticating is not null || _checking is not null || _status.Phase is UpdatePhase.Downloading or UpdatePhase.Installing)
                    return Task.FromException<UpdateStatus>(new UpdateException(CodeBusy, UpdateMessages.BusyOperation));
                completion = new TaskCompletionSource<UpdateStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                _authenticating = completion.Task;
            }
            _ = RunAuthenticateAsync(completion);
            return completion.Task;
        }
        catch (Exception ex)
        {
            return Task.FromException<UpdateStatus>(Unexpected(ex, "GitHub connection"));
        }
    }

    public async Task<UpdateStatus> CancelAuthenticationAsync()
    {
        try
        {
            EnsureLoaded();
            Task<UpdateStatus> pending;
            CancellationTokenSource? active;
            lock (_gate)
            {
                // Dopo che GCM ha restituito la credenziale (fase Checking) il salvataggio va completato: annullare non e' piu' possibile.
                if (_authenticating is null || _status.Phase != UpdatePhase.Authenticating) return _status;
                pending = _authenticating;
                active = _active;
            }
            CancelQuietly(active);
            try { await pending.ConfigureAwait(false); }
            catch (Exception) { /* l'esito e' gia' nello stato */ }
            lock (_gate) return _status;
        }
        catch (Exception ex)
        {
            throw Unexpected(ex, "GitHub connection cancel");
        }
    }

    // Task.Run: prima del primo await download e installazione rileggono la sessione e fanno la prova di scrittura nella
    // cartella dell'exe, IO che non deve girare sul thread UI da cui arrivano i comandi.
    public Task<UpdateStatus> DownloadAsync() => GuardAsync(() => Task.Run(DownloadCoreAsync), "download");

    public Task<UpdateStatus> InstallAsync() => GuardAsync(() => Task.Run(InstallCoreAsync), "install");

    /// <summary>Elimina la sessione GitHub salvata dall'app (rifiutato durante un'operazione in corso).</summary>
    public Task<UpdateStatus> DisconnectAsync()
    {
        try
        {
            EnsureLoaded();
            UpdateStatus snapshot;
            Exception? failure = null;
            lock (_gate)
            {
                if (_disposed) return Task.FromException<UpdateStatus>(Closed());
                if (_authenticating is not null || _checking is not null || IsBusyPhase(_status.Phase))
                    return Task.FromException<UpdateStatus>(new UpdateException(CodeBusy, UpdateMessages.BusyOperation));
                // L'eliminazione avviene sotto il lock: nessun controllo puo' leggere la sessione a meta' dello scollegamento.
                try { _options.Credentials.Delete(); }
                catch (Exception ex) { failure = ex; }
                if (failure is null)
                {
                    CancelCheckTimerLocked();
                    snapshot = CommitLocked(_status with
                    {
                        Phase = UpdatePhase.Idle,
                        AuthSource = UpdateAuthSource.Anonymous,
                        GitHubAccount = null,
                        ErrorCode = null,
                        Message = UpdateMessages.Disconnected
                    });
                }
                else
                {
                    snapshot = _status;
                }
            }
            if (failure is not null)
                return Task.FromException<UpdateStatus>(Fail(new UpdateException(CodeDisconnect, UpdateMessages.DisconnectFailed), "disconnect", failure));
            Raise(snapshot);
            LogInfo("Updates: GitHub session removed");
            return Task.FromResult(snapshot);
        }
        catch (Exception ex)
        {
            return Task.FromException<UpdateStatus>(Unexpected(ex, "disconnect"));
        }
    }

    /// <summary>Pagina della release proposta, o l'elenco delle release del repository.</summary>
    public string ReleaseUrl()
    {
        lock (_gate) return _status.Release?.Url ?? $"{UpdateSource.RepositoryUrl}/releases";
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        Task? installing = null;
        string? staged = null;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CancelCheckTimerLocked();
            _cleanupTimer?.Dispose();
            _cleanupTimer = null;
            active = _active;
            // L'installazione puo' avere ancora bisogno del file: durante Installing non si elimina.
            if (_status.Phase != UpdatePhase.Installing) staged = TakeStagedLocked();
            else installing = _installing;
            CommitLocked(_status); // CanDownload/CanInstall tornano false; nessun evento dopo Dispose
        }
        CancelQuietly(active);
        // Chiusura dell'app (Esci, fine sessione) durante la sostituzione dell'exe: il processo non deve terminare tra i
        // due rename, o al percorso dell'exe non resterebbe nulla. La cancellazione ferma l'installer prima dello scambio;
        // se lo scambio e' gia' partito lo si lascia finire (o ripristinare), con un tetto.
        if (installing is not null)
        {
            try
            {
                if (!installing.Wait(InstallShutdownTimeout)) LogInfo("Updates: closing while the executable swap is still running");
            }
            catch (Exception) { /* l'esito e' gia' nel log dell'installer */ }
        }
        DeleteQuietly(staged);
    }

    // ---- Operazioni ----

    private async Task RunCheckAsync(TaskCompletionSource<UpdateStatus> completion)
    {
        Exception? failure = null;
        try { await CheckNowAsync(null).ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        Finish(completion, failure, () => { if (_checking == completion.Task) _checking = null; }, "check");
    }

    private async Task RunAuthenticateAsync(TaskCompletionSource<UpdateStatus> completion)
    {
        Exception? failure = null;
        try { await AuthenticateNowAsync().ConfigureAwait(false); }
        catch (Exception ex) { failure = ex; }
        Finish(completion, failure, () => { if (_authenticating == completion.Task) _authenticating = null; }, "GitHub connection");
    }

    /// <summary>Chiude un'operazione condivisa: libera il flag di occupato, pubblica, ripianifica e completa il Task.</summary>
    private void Finish(TaskCompletionSource<UpdateStatus> completion, Exception? failure, Action release, string operation)
    {
        try
        {
            var status = Publish(current =>
            {
                release();
                return current;
            });
            Schedule(CheckInterval);
            if (failure is null) completion.TrySetResult(status);
            else completion.TrySetException(failure as UpdateException ?? Unexpected(failure, operation));
        }
        catch (Exception ex)
        {
            lock (_gate) release();
            completion.TrySetException(failure as UpdateException ?? Unexpected(failure ?? ex, operation));
        }
    }

    private async Task<UpdateStatus> AuthenticateNowAsync()
    {
        var operation = new CancellationTokenSource();
        lock (_gate)
        {
            if (_disposed) throw Closed();
            _active = operation;
        }
        Publish(s => s with { Phase = UpdatePhase.Authenticating, ErrorCode = null, Message = UpdateMessages.AuthInProgress });
        LogInfo("Updates: GitHub connection started");
        UpdateCredential credential;
        try
        {
            // Quando GCM ha restituito la credenziale il salvataggio atomico va completato prima che un'altra azione
            // possa annunciare l'annullamento: la sessione salvata deve corrispondere a quello che l'utente vede.
            credential = await _options.Login.AuthenticateAsync(OnCredentialReady, operation.Token).ConfigureAwait(false);
            bool cancelled;
            lock (_gate) cancelled = operation.IsCancellationRequested || _disposed;
            if (cancelled) throw new UpdateException(CodeAuthCancelled, UpdateMessages.AuthCancelled);
            if (credential is null) throw new UpdateException(CodeGeneric, UpdateMessages.GenericFailure);
            Publish(s => s with { AuthSource = credential.Source, GitHubAccount = credential.Account });
            LogInfo("Updates: GitHub connection completed");
            try { _options.ReturnToApp(); }
            catch (Exception ex) { LogError("Updates: could not bring the app back to the front", ex); }
        }
        catch (Exception ex) when (IsAuthCancellation(ex, operation) && !IsDisposed())
        {
            LogInfo("Updates: GitHub connection cancelled");
            return Publish(s => s with { Phase = UpdatePhase.Idle, ErrorCode = null, Message = UpdateMessages.AuthCancelledNotice });
        }
        catch (Exception ex)
        {
            throw Fail(ex, "GitHub connection");
        }
        finally
        {
            ReleaseActive(operation);
        }
        // Il controllo prosegue sotto lo stesso flag di autenticazione, con la credenziale appena restituita.
        return await CheckNowAsync(credential).ConfigureAwait(false);
    }

    private void OnCredentialReady()
    {
        try { Publish(s => s with { Phase = UpdatePhase.Checking, Message = UpdateMessages.AuthSaving }); }
        catch (Exception ex) { LogError("Updates: status update failed", ex); }
    }

    private async Task<UpdateStatus> CheckNowAsync(UpdateCredential? linkedCredential)
    {
        var operation = new CancellationTokenSource();
        InstallationKind installation;
        lock (_gate)
        {
            if (_disposed) throw Closed();
            _active = operation;
            installation = _status.Installation;
        }
        Publish(s => s with { Phase = UpdatePhase.Checking, ErrorCode = null, Message = UpdateMessages.Checking });
        LogInfo("Updates: checking releases");
        try
        {
            var development = installation == InstallationKind.Development;
            var currentBase = UpdateSource.StableVersion(_options.CurrentVersion)
                ?? throw new UpdateException(CodeVersion, UpdateMessages.VersionUnstable);
            // La pipeline pubblica versioni stabili crescenti: si confrontano semanticamente le 100 release piu' recenti
            // invece di fidarsi dell'etichetta "Latest", che si puo' spostare a mano.
            var (document, _) = await ReleaseMetadataAsync(ReleasesPath, operation.Token, linkedCredential).ConfigureAwait(false);
            ReleaseCandidate? latest;
            using (document) latest = ReleaseCatalog.Latest(document.RootElement, _options.Variant);
            var checkedAt = _options.Time.GetUtcNow();
            if (latest is null) throw new UpdateException(CodeNoRelease, UpdateMessages.NoRelease);

            string? obsolete = null;
            UpdateStatus status;
            if (UpdateSource.CompareVersions(latest.View.Version, currentBase) <= 0)
            {
                status = Publish(s =>
                {
                    obsolete = TakeStagedLocked();
                    _candidate = null;
                    return s with
                    {
                        Phase = UpdatePhase.UpToDate,
                        CheckedAt = checkedAt,
                        Release = latest.View,
                        Download = null,
                        Message = development ? UpdateMessages.UpToDateDev(currentBase, latest.View.Version) : UpdateMessages.UpToDate
                    };
                });
                LogInfo($"Updates: {currentBase} is up to date (latest release {latest.View.Version})");
            }
            else
            {
                // C'e' una versione da proporre: solo ora si verifica che la cartella dell'exe sia scrivibile, cosi' la
                // proposta (tray, notifica) non offre un'installazione che non potrebbe riuscire.
                installation = DetectInstallation(probeWritable: true);
                var suffix = installation switch
                {
                    InstallationKind.Development => UpdateMessages.SuffixDevelopment,
                    InstallationKind.ReadOnlyLocation => UpdateMessages.SuffixReadOnlyLocation,
                    InstallationKind.UnsupportedPlatform => UpdateMessages.SuffixUnsupportedPlatform,
                    _ => ""
                } + (latest.View.Checksum == ChecksumSource.Unavailable ? UpdateMessages.SuffixNoChecksum : "");
                status = Publish(s =>
                {
                    // Un eseguibile gia' scaricato resta valido solo se la release e il suo asset non sono cambiati.
                    if (_staged?.Version != latest.View.Version
                        || _candidate?.Package.Id != latest.Package.Id
                        || _candidate?.Package.Digest != latest.Package.Digest)
                        obsolete = TakeStagedLocked();
                    _candidate = latest;
                    var staged = _staged is not null;
                    return s with
                    {
                        Installation = installation,
                        Phase = staged ? UpdatePhase.Downloaded : UpdatePhase.Available,
                        CheckedAt = checkedAt,
                        Release = latest.View,
                        Download = staged ? s.Download : null,
                        Message = UpdateMessages.Available(latest.View.Version, suffix)
                    };
                });
                LogInfo($"Updates: version {latest.View.Version} available (current {currentBase})");
            }
            DeleteQuietly(obsolete);
            return status;
        }
        catch (Exception ex)
        {
            throw Fail(ex, "check");
        }
        finally
        {
            ReleaseActive(operation);
        }
    }

    private async Task<UpdateStatus> DownloadCoreAsync()
    {
        EnsureLoaded();
        var installation = DetectInstallation(probeWritable: true);
        var operation = new CancellationTokenSource();
        ReleaseCandidate? selected = null;
        UpdateStatus? snapshot = null;
        bool notify;
        lock (_gate)
        {
            if (installation != _status.Installation) snapshot = CommitLocked(_status with { Installation = installation });
            if (CapabilitiesLocked(_status).CanDownload && _candidate is not null)
            {
                selected = _candidate;
                _active = operation;
                snapshot = CommitLocked(_status with
                {
                    Phase = UpdatePhase.Downloading,
                    ErrorCode = null,
                    Download = new UpdateDownload(0, selected.Package.Size, 0, null, false),
                    Message = UpdateMessages.Downloading
                });
            }
            notify = !_disposed;
        }
        if (snapshot is not null && notify) Raise(snapshot);
        if (selected is null) throw new UpdateException(CodeDownloadState, UpdateMessages.DownloadState);

        LogInfo($"Updates: downloading version {selected.View.Version}");
        string? partial = null;
        try
        {
            // Si rilegge proprio questa release prima di scrivere byte: un asset puo' essere stato sostituito dopo il controllo.
            var (document, credential) = await ReleaseMetadataAsync($"/releases/{selected.ReleaseId}", operation.Token, null).ConfigureAwait(false);
            ReleaseCandidate fresh;
            using (document) fresh = ReleaseCatalog.Exact(document.RootElement, selected, _options.Variant);
            var token = credential.Token!;
            var expectedHash = fresh.Package.Digest;
            if (fresh.Checksums is not null)
            {
                var bytes = await _options.Transport.ReadAssetBytesAsync(fresh.Checksums.Id, token, UpdateSource.MaxChecksumManifestBytes, operation.Token).ConfigureAwait(false);
                // Un BOM iniziale (manifest scritto su Windows) impedirebbe di riconoscere la prima riga.
                var manifest = Encoding.UTF8.GetString(bytes).TrimStart(ByteOrderMark);
                var manifestHash = ReleaseCatalog.ChecksumFor(manifest, fresh.Package.Name);
                if (expectedHash is not null && expectedHash != manifestHash)
                    throw new UpdateException(CodeChecksum, UpdateMessages.ChecksumConflict);
                expectedHash = manifestHash;
            }

            Directory.CreateDirectory(_options.DownloadsDirectory);
            partial = Path.Combine(_options.DownloadsDirectory, $"{UpdateSource.PackageName}-{fresh.View.Version}-{Guid.NewGuid():D}.exe{PartialSuffix}");
            var total = fresh.Package.Size;
            var progress = new InlineProgress(received => ReportProgress(operation, received, total));
            var downloaded = await _options.Transport.DownloadAssetAsync(fresh.Package.Id, partial, total, token, progress, operation.Token).ConfigureAwait(false);
            var sha256 = downloaded.Sha256.ToLowerInvariant();
            if (expectedHash is not null && sha256 != expectedHash)
                throw new UpdateException(CodeIntegrity, UpdateMessages.IntegrityMismatch);
            EnsureWindowsExecutable(partial);

            var finalPath = partial[..^PartialSuffix.Length];
            File.Move(partial, finalPath);
            partial = null;
            var staged = new StagedUpdate(finalPath, sha256, expectedHash, downloaded.Size, fresh.View.Version);
            UpdateStatus status;
            string? replaced;
            bool closed;
            lock (_gate)
            {
                closed = _disposed;
                if (closed)
                {
                    // Dispose e' gia' passato: nessuno eliminerebbe piu' questo file.
                    replaced = finalPath;
                    status = _status;
                }
                else
                {
                    replaced = TakeStagedLocked();
                    _staged = staged;
                    _candidate = fresh;
                    status = CommitLocked(_status with
                    {
                        Phase = UpdatePhase.Downloaded,
                        Release = fresh.View,
                        Download = new UpdateDownload(downloaded.Size, downloaded.Size, 100, sha256, expectedHash is not null),
                        Message = expectedHash is not null ? UpdateMessages.DownloadedVerified : UpdateMessages.DownloadedUnverified
                    });
                }
            }
            DeleteQuietly(replaced);
            if (closed) throw Closed();
            Raise(status);
            LogInfo($"Updates: version {staged.Version} downloaded ({(expectedHash is not null ? "SHA-256 verified" : "no published checksum")})");
            return status;
        }
        catch (Exception ex)
        {
            DeleteQuietly(partial);
            throw Fail(ex, "download");
        }
        finally
        {
            ReleaseActive(operation);
            Schedule(CheckInterval);
        }
    }

    private void ReportProgress(CancellationTokenSource operation, long received, long total)
    {
        UpdateStatus snapshot;
        lock (_gate)
        {
            // Un avanzamento arrivato in ritardo non deve sovrascrivere lo stato finale del download.
            if (_disposed || _active != operation || _status.Phase != UpdatePhase.Downloading) return;
            var percent = total > 0 ? (int)Math.Clamp(received * 100 / total, 0, 100) : 0;
            snapshot = CommitLocked(_status with { Download = new UpdateDownload(received, total, percent, null, false) });
        }
        Raise(snapshot);
    }

    private async Task<UpdateStatus> InstallCoreAsync()
    {
        EnsureLoaded();
        var installation = DetectInstallation(probeWritable: true);
        var operation = new CancellationTokenSource();
        // Completato alla fine dell'operazione (finally qui sotto): Dispose lo aspetta durante Installing. Assegnato nello
        // stesso lock che porta la fase a Installing, cosi' Dispose non puo' vedere la fase senza il task da aspettare.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        StagedUpdate? staged = null;
        UpdateStatus? snapshot = null;
        bool notify;
        lock (_gate)
        {
            if (installation != _status.Installation) snapshot = CommitLocked(_status with { Installation = installation });
            if (CapabilitiesLocked(_status).CanInstall && _staged is not null)
            {
                staged = _staged;
                _active = operation;
                _installing = finished.Task;
                snapshot = CommitLocked(_status with { Phase = UpdatePhase.Installing, ErrorCode = null, Message = UpdateMessages.FinalVerification });
            }
            notify = !_disposed;
        }
        if (snapshot is not null && notify) Raise(snapshot);
        if (staged is null) throw new UpdateException(CodeInstallState, UpdateMessages.InstallState);

        LogInfo($"Updates: installing version {staged.Version}");
        try
        {
            var current = UpdateSource.StableVersion(_options.CurrentVersion)
                ?? throw new UpdateException(CodeVersion, UpdateMessages.VersionUnstable);
            if (UpdateSource.CompareVersions(staged.Version, current) <= 0)
                throw new UpdateException(CodeVersion, UpdateMessages.NotNewer);
            string hash;
            try
            {
                hash = await FileHashAsync(staged.Path, staged.Size, operation.Token).ConfigureAwait(false);
            }
            catch (StagedFileChangedException)
            {
                RemoveStaged(staged);
                throw;
            }
            if (IsDisposed()) throw Closed();
            if (hash != staged.Sha256 || (staged.ExpectedHash is not null && hash != staged.ExpectedHash))
            {
                RemoveStaged(staged);
                throw new UpdateException(CodeIntegrity, UpdateMessages.IntegrityChangedLocally);
            }
            await _options.Installer.InstallAsync(staged, operation.Token).ConfigureAwait(false);
            // La fase resta Installing: l'app si chiude e la nuova istanza prende il suo posto.
            var status = Publish(s => s with { Message = UpdateMessages.InstallStarted });
            LogInfo($"Updates: version {staged.Version} started, closing for the update");
            try { _options.QuitForInstall(); }
            catch (Exception ex) { LogError("Updates: quit for the update failed", ex); }
            return status;
        }
        catch (Exception ex)
        {
            throw Fail(ex, "install");
        }
        finally
        {
            ReleaseActive(operation);
            finished.TrySetResult();
        }
    }

    /// <summary>Solo la sessione creata dal collegamento dell'app: senza token il controllo chiede di collegarsi.</summary>
    private async Task<(JsonDocument Document, UpdateCredential Credential)> ReleaseMetadataAsync(
        string relativePath, CancellationToken cancellationToken, UpdateCredential? linkedCredential)
    {
        var credential = linkedCredential ?? ReadCredential();
        Publish(s => s with { AuthSource = credential.Source, GitHubAccount = credential.Account });
        if (string.IsNullOrEmpty(credential.Token))
        {
            throw new UpdateException(CodeAuthRequired, credential.Failure == CredentialFailure.StoredCredentialUnavailable
                ? UpdateMessages.AuthRequiredUnreadable
                : UpdateMessages.AuthRequired);
        }
        try
        {
            var document = await _options.Transport.ReadJsonAsync(relativePath, credential.Token, cancellationToken).ConfigureAwait(false);
            return (document, credential);
        }
        catch (UpdateAccessException ex)
        {
            throw new LinkedAccountException(ex, credential.Account);
        }
    }

    private async Task<string> FileHashAsync(string path, long expectedSize, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
            || info.Length != expectedSize || info.Length > UpdateSource.MaxPackageBytes)
            throw new StagedFileChangedException(UpdateMessages.LocalPackageChanged);

        using var timeout = new CancellationTokenSource(LocalVerifyTimeout, _options.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.Read,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long size = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
            {
                size += read;
                if (size > expectedSize) throw new StagedFileChangedException(UpdateMessages.LocalSizeChanged);
                hash.AppendData(buffer, 0, read);
            }
            if (size != expectedSize) throw new StagedFileChangedException(UpdateMessages.LocalIncomplete);
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException(CodeIntegrity, UpdateMessages.LocalVerifyTimeout);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new StagedFileChangedException(UpdateMessages.LocalPackageChanged);
        }
    }

    private static void EnsureWindowsExecutable(string path)
    {
        var header = new byte[2];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        if (read < 2 || header[0] != (byte)'M' || header[1] != (byte)'Z')
            throw new UpdateException(CodeFormat, UpdateMessages.NotWindowsExecutable);
    }

    // ---- Pianificazione ----

    private void Schedule(TimeSpan delay)
    {
        try
        {
            lock (_gate)
            {
                CancelCheckTimerLocked();
                if (_disposed || !_status.AutoCheck || _status.AuthSource != UpdateAuthSource.GitHubApp) return;
                _checkTimer = _options.Time.CreateTimer(OnCheckTimer, _checkTimerGeneration, delay, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            LogError("Updates: automatic check could not be scheduled", ex);
        }
    }

    private void OnCheckTimer(object? state)
    {
        try
        {
            bool postpone;
            lock (_gate)
            {
                // Un timer sostituito o annullato mentre il suo callback era gia' in coda non deve fare nulla.
                if (_disposed || state is not long generation || generation != _checkTimerGeneration) return;
                CancelCheckTimerLocked();
                postpone = _authenticating is not null || _status.Phase is UpdatePhase.Downloading or UpdatePhase.Installing || _staged is not null;
            }
            if (postpone)
            {
                Schedule(CheckInterval);
                return;
            }
            _ = ObserveAsync(CheckAsync());
        }
        catch (Exception ex)
        {
            LogError("Updates: automatic check failed to start", ex);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* l'errore e' gia' nello stato e nel log */ }
    }

    private void CancelCheckTimerLocked()
    {
        _checkTimerGeneration++;
        _checkTimer?.Dispose();
        _checkTimer = null;
    }

    private void OnCleanupTimer(object? state)
    {
        try
        {
            lock (_gate)
            {
                _cleanupTimer?.Dispose();
                _cleanupTimer = null;
                if (_disposed) return;
            }
            CleanObsoleteDownloads();
        }
        catch (Exception)
        {
            // La pulizia e' facoltativa: non deve mai rallentare o bloccare l'app.
        }
    }

    /// <summary>
    /// Elimina i vecchi download (<c>AIUsageMonitor-&lt;v&gt;-&lt;guid&gt;.exe(.part)</c>) di versioni non piu' recenti
    /// della corrente o piu' vecchi di 7 giorni, tranne l'eseguibile pronto per l'installazione.
    /// </summary>
    private void CleanObsoleteDownloads()
    {
        try
        {
            var directory = _options.DownloadsDirectory;
            if (!Directory.Exists(directory)) return;
            var current = UpdateSource.StableVersion(_options.CurrentVersion);
            var now = _options.Time.GetUtcNow();
            var removed = 0;
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Take(MaxCleanupEntries).ToList())
            {
                try
                {
                    var match = ObsoleteDownloadPattern().Match(Path.GetFileName(path));
                    if (!match.Success) continue;
                    var version = UpdateSource.StableVersion(match.Groups[1].Value);
                    if (version is null) continue;
                    string? stagedPath;
                    lock (_gate)
                    {
                        if (_disposed) return;
                        stagedPath = _staged?.Path;
                    }
                    if (stagedPath is not null && SamePath(path, stagedPath)) continue;
                    var info = new FileInfo(path);
                    if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) continue;
                    var obsolete = (current is not null && UpdateSource.CompareVersions(version, current) <= 0)
                                   || now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) > ObsoleteDownloadAge;
                    if (!obsolete) continue;
                    info.Delete();
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Windows tiene bloccato un eseguibile in uso: si riprova a un avvio successivo.
                }
            }
            if (removed > 0) LogInfo($"Updates: removed {removed} obsolete download(s)");
        }
        catch (Exception)
        {
            // La pulizia e' facoltativa: non deve mai rallentare o bloccare l'avvio.
        }
    }

    [GeneratedRegex(@"\A" + UpdateSource.PackageName + @"-([0-9]+\.[0-9]+\.[0-9]+)-[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\.exe(?:\.part)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex ObsoleteDownloadPattern();

    // ---- Stato ----

    /// <summary>
    /// Legge una volta preferenza, sessione e tipo di installazione. L'IO avviene fuori dal lock; se due thread caricano
    /// insieme vince il primo. Non incrementa la revisione: nessuna istantanea precedente e' mai stata esposta.
    /// </summary>
    private void EnsureLoaded()
    {
        if (_loaded) return;
        var autoCheck = ReadAutoCheck();
        var credential = ReadCredential();
        // Senza prova di scrittura: il caricamento avviene a ogni avvio, anche per chi non usa gli aggiornamenti.
        var installation = DetectInstallation(probeWritable: false);
        lock (_gate)
        {
            if (_loaded) return;
            _status = _status with
            {
                AutoCheck = autoCheck,
                AuthSource = credential.Source,
                GitHubAccount = credential.Account,
                Installation = installation
            };
            _loaded = true;
        }
    }

    private bool ReadAutoCheck()
    {
        try { return _options.AutoCheck(); }
        catch (Exception ex)
        {
            LogError("Updates: automatic check preference unreadable", ex);
            return false;
        }
    }

    private UpdateCredential ReadCredential()
    {
        try { return _options.Credentials.Read() ?? UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable); }
        catch (Exception ex)
        {
            // Il contratto dice che Read non lancia: per sicurezza una lettura fallita vale come sessione illeggibile.
            // Solo il tipo nel log: il messaggio potrebbe citare il contenuto della sessione.
            LogError($"Updates: saved GitHub session unreadable ({ex.GetType().Name})", null);
            return UpdateCredential.Missing(CredentialFailure.StoredCredentialUnavailable);
        }
    }

    private InstallationKind DetectInstallation(bool probeWritable)
    {
        try { return _options.Installer.DetectInstallation(probeWritable); }
        catch (Exception ex)
        {
            // Se non si riesce a verificare la cartella dell'eseguibile, niente sostituzione automatica.
            LogError("Updates: installation kind could not be detected", ex);
            return InstallationKind.ReadOnlyLocation;
        }
    }

    private static bool IsBusyPhase(UpdatePhase phase) =>
        phase is UpdatePhase.Authenticating or UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    private (bool CanDownload, bool CanInstall) CapabilitiesLocked(UpdateStatus state)
    {
        var supported = state.Installation == InstallationKind.Supported;
        var busy = _disposed || _checking is not null || _authenticating is not null || IsBusyPhase(state.Phase);
        return (supported && !busy && _candidate is not null && _staged is null, supported && !busy && _staged is not null);
    }

    /// <summary>Sotto <see cref="_gate"/>: applica lo stato, ricalcola i permessi e incrementa la revisione.</summary>
    private UpdateStatus CommitLocked(UpdateStatus next)
    {
        var (canDownload, canInstall) = CapabilitiesLocked(next);
        _status = next with { Revision = _status.Revision + 1, CanDownload = canDownload, CanInstall = canInstall };
        return _status;
    }

    /// <summary>Applica <paramref name="patch"/> sotto il lock e alza <see cref="Changed"/> fuori dal lock.</summary>
    private UpdateStatus Publish(Func<UpdateStatus, UpdateStatus> patch)
    {
        UpdateStatus snapshot;
        bool notify;
        lock (_gate)
        {
            snapshot = CommitLocked(patch(_status));
            notify = !_disposed;
        }
        if (notify) Raise(snapshot);
        return snapshot;
    }

    private void Raise(UpdateStatus snapshot)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<UpdateStatus>>())
        {
            try { handler(snapshot); }
            catch (Exception ex) { LogError("Updates: status subscriber failed", ex); }
        }
    }

    private UpdateException Fail(Exception error, string operation)
    {
        var safe = error switch
        {
            UpdateException update => update,
            OperationCanceledException when IsDisposed() => Closed(),
            _ => new UpdateException(CodeGeneric, UpdateMessages.GenericFailure)
        };
        return Fail(safe, operation, error);
    }

    private UpdateException Fail(UpdateException safe, string operation, Exception cause)
    {
        // Il messaggio con l'account collegato resta nella UI: nel log va l'errore originale, senza account.
        LogError($"Updates: {operation} failed ({safe.Code})", cause is LinkedAccountException linked ? linked.Access : cause);
        Publish(s => s with { Phase = UpdatePhase.Error, Message = safe.Message, ErrorCode = safe.Code });
        return safe;
    }

    /// <summary>Ultima difesa: nessuna eccezione diversa da <see cref="UpdateException"/> esce dalle API pubbliche.</summary>
    private UpdateException Unexpected(Exception error, string operation)
    {
        if (error is UpdateException update) return update;
        LogError($"Updates: {operation} failed unexpectedly", error);
        return new UpdateException(CodeGeneric, UpdateMessages.GenericFailure);
    }

    private async Task<UpdateStatus> GuardAsync(Func<Task<UpdateStatus>> operation, string name)
    {
        try { return await operation().ConfigureAwait(false); }
        catch (Exception ex) { throw Unexpected(ex, name); }
    }

    private static bool IsAuthCancellation(Exception error, CancellationTokenSource operation) =>
        error is UpdateException { Code: CodeAuthCancelled }
        || (error is OperationCanceledException && operation.IsCancellationRequested);

    private bool IsDisposed()
    {
        lock (_gate) return _disposed;
    }

    private static UpdateException Closed() => new(CodeClosed, UpdateMessages.Closing);

    private void ReleaseActive(CancellationTokenSource operation)
    {
        lock (_gate)
        {
            if (_active == operation) _active = null;
        }
    }

    /// <summary>Sotto <see cref="_gate"/>: toglie l'eseguibile pronto e restituisce il percorso da eliminare fuori dal lock.</summary>
    private string? TakeStagedLocked()
    {
        var path = _staged?.Path;
        _staged = null;
        return path;
    }

    private void RemoveStaged(StagedUpdate staged)
    {
        string? path = null;
        lock (_gate)
        {
            if (ReferenceEquals(_staged, staged)) path = TakeStagedLocked();
        }
        DeleteQuietly(path);
    }

    private static void DeleteQuietly(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); }
        catch (Exception) { /* un file bloccato viene rimosso dalla pulizia a un avvio successivo */ }
    }

    private void CancelQuietly(CancellationTokenSource? source)
    {
        if (source is null) return;
        try { source.Cancel(); }
        catch (Exception ex) { LogError("Updates: cancellation callback failed", ex); }
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private void LogInfo(string message)
    {
        try { _options.LogInfo(message); }
        catch (Exception) { /* un logger rotto non deve fermare l'updater */ }
    }

    private void LogError(string message, Exception? error)
    {
        try { _options.LogError(message, error); }
        catch (Exception) { /* un logger rotto non deve fermare l'updater */ }
    }

    /// <summary>Riporta l'avanzamento in modo sincrono: <c>Progress&lt;T&gt;</c> lo posterebbe dopo lo stato finale.</summary>
    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value)
        {
            try { report(value); }
            catch (Exception) { /* l'avanzamento e' solo informativo */ }
        }
    }

    /// <summary>L'eseguibile scaricato non e' piu' quello verificato: va eliminato e scaricato di nuovo.</summary>
    private sealed class StagedFileChangedException(string message) : UpdateException(CodeIntegrity, message);

    /// <summary>Errore di accesso con l'account collegato nel messaggio per l'utente; <see cref="Access"/> e' quello senza account, per il log.</summary>
    private sealed class LinkedAccountException(UpdateAccessException access, string? account)
        : UpdateException(access.Code, access.Message + (string.IsNullOrEmpty(account) ? "" : UpdateMessages.LinkedAccount(account)))
    {
        public UpdateAccessException Access { get; } = access;
    }
}
