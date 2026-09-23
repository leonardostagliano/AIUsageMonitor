using System.Diagnostics;
using System.Globalization;
using System.Windows;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.App.Updates;

/// <summary>
/// Gruppo "Aggiornamenti" delle Impostazioni (porting di ChessAdvisor <c>UpdatesSection</c>): mostra l'istantanea
/// dell'updater e ne esegue i comandi. Il servizio possiede ogni chiamata di rete, qui c'e' solo lo stato da mostrare.
/// Va eliminato alla chiusura della finestra per staccarsi da <see cref="UpdateService.Changed"/>.
/// </summary>
public sealed class UpdatesViewModel : ObservableObject, IDisposable
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private readonly UpdateService _updates;
    private readonly FileLogger _log;
    private readonly Action<UpdateStatus> _changed;
    private readonly RelayCommand[] _commands;
    private UpdateStatus _status;
    private string _commandError = "";
    private bool _pending;
    private bool _cancelling;
    private bool _disposed;

    public UpdatesViewModel(UpdateService updates, FileLogger log)
    {
        _updates = updates;
        _log = log;
        _status = updates.Status;

        ConnectCommand = new RelayCommand(() => _ = RunAsync(_updates.AuthenticateAsync, "collegamento GitHub"), () => !IsBusy);
        CheckCommand = new RelayCommand(() => _ = RunAsync(_updates.CheckAsync, "controllo"), () => !IsBusy && IsConnected);
        DownloadCommand = new RelayCommand(() => _ = RunAsync(_updates.DownloadAsync, "download"), () => !IsBusy && _status.CanDownload);
        InstallCommand = new RelayCommand(() => _ = RunAsync(_updates.InstallAsync, "installazione"), () => !IsBusy && _status.CanInstall);
        // Fuori dalla guardia busy: il collegamento in corso E' il comando pendente che questo pulsante deve interrompere.
        CancelAuthenticationCommand = new RelayCommand(() => _ = CancelAuthenticationAsync(), () => IsAuthenticating && !_cancelling);
        DisconnectCommand = new RelayCommand(() => _ = RunAsync(_updates.DisconnectAsync, "scollegamento"), () => !IsBusy && IsConnected);
        OpenReleaseCommand = new RelayCommand(OpenRelease, () => _status.Release is not null);
        _commands = [ConnectCommand, CheckCommand, DownloadCommand, InstallCommand, CancelAuthenticationCommand, DisconnectCommand, OpenReleaseCommand];

        // Changed arriva da thread qualsiasi e le istantanee possono arrivare fuori ordine: Apply scarta le vecchie.
        _changed = status => UiDispatcher.Post(() => Apply(status));
        updates.Changed += _changed;
        // Rilettura dopo l'abbonamento: un cambiamento avvenuto tra la prima lettura e l'abbonamento non va perso.
        Apply(updates.Status);
    }

    public RelayCommand ConnectCommand { get; }
    public RelayCommand CheckCommand { get; }
    public RelayCommand DownloadCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand CancelAuthenticationCommand { get; }
    public RelayCommand DisconnectCommand { get; }
    public RelayCommand OpenReleaseCommand { get; }

    public string VersionText
    {
        get
        {
            var version = string.IsNullOrWhiteSpace(_status.CurrentVersion) ? "—" : _status.CurrentVersion;
            var text = $"{version} · {BuildInfo.VariantLabel(_status.Variant)}";
            return _status.Installation == InstallationKind.Development ? $"{text} · build di sviluppo" : text;
        }
    }

    public bool IsConnected => _status.AuthSource == UpdateAuthSource.GitHubApp && !string.IsNullOrEmpty(_status.GitHubAccount);
    public string AccountText => IsConnected ? _status.GitHubAccount! : "Non collegato";
    public string LastCheckText => _status.CheckedAt is { } at ? at.ToLocalTime().ToString("g", Italian) : "Mai";
    public string PhaseText => PhaseLabel(_status.Phase);

    public string Message => _status.Message;

    /// <summary>Messaggio dello stato con lo stile normale (fasi diverse da Error).</summary>
    public Visibility MessageVisibility => HasMessage && !IsError ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Lo stesso messaggio con lo stile di errore, nella fase Error.</summary>
    public Visibility ErrorMessageVisibility => HasMessage && IsError ? Visibility.Visible : Visibility.Collapsed;

    public bool IsError => _status.Phase == UpdatePhase.Error;
    private bool HasMessage => !string.IsNullOrWhiteSpace(_status.Message);

    /// <summary>Errore dell'ultimo comando lanciato da qui (lo stato del servizio ha gia' il suo messaggio).</summary>
    public string CommandError
    {
        get => _commandError;
        private set
        {
            if (Set(ref _commandError, value)) Raise(nameof(CommandErrorVisibility));
        }
    }

    public Visibility CommandErrorVisibility => string.IsNullOrEmpty(_commandError) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility ProgressVisibility => ProgressPercent is null ? Visibility.Collapsed : Visibility.Visible;
    public double ProgressValue => ProgressPercent ?? 0;

    public string ProgressText
    {
        get
        {
            var download = _status.Download;
            if (ProgressPercent is not { } percent || download is null) return "";
            return $"{percent:0}% · {Megabytes(download.ReceivedBytes)} di {Megabytes(download.TotalBytes)}";
        }
    }

    public Visibility CheckVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DisconnectVisibility => IsConnected ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelAuthenticationVisibility => IsAuthenticating ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NotesVisibility => string.IsNullOrWhiteSpace(_status.Release?.Notes) ? Visibility.Collapsed : Visibility.Visible;
    public string NotesHeader => _status.Release is { } release ? $"Note della versione {release.Version}" : "";
    public string Notes => _status.Release?.Notes ?? "";

    private bool IsAuthenticating => _status.Phase == UpdatePhase.Authenticating;

    /// <summary>Stessa guardia di ChessAdvisor: un comando alla volta, e nessun comando durante il login nel browser.</summary>
    private bool IsBusy => _pending || IsAuthenticating;

    /// <summary>Percentuale solo durante un download con dimensione nota.</summary>
    private double? ProgressPercent =>
        _status.Phase == UpdatePhase.Downloading && _status.Download is { TotalBytes: > 0 } download
            ? Math.Clamp(download.Percent, 0, 100)
            : null;

    /// <summary>Etichetta italiana di ogni fase dell'updater.</summary>
    public static string PhaseLabel(UpdatePhase phase) => phase switch
    {
        UpdatePhase.Idle => "In attesa",
        UpdatePhase.Authenticating => "Collegamento GitHub in corso",
        UpdatePhase.Checking => "Verifica in corso",
        UpdatePhase.UpToDate => "Aggiornata",
        UpdatePhase.Available => "Aggiornamento disponibile",
        UpdatePhase.Downloading => "Download in corso",
        UpdatePhase.Downloaded => "Pronta da installare",
        UpdatePhase.Installing => "Installazione in corso",
        UpdatePhase.Error => "Errore",
        _ => phase.ToString()
    };

    /// <summary>
    /// Solo pagine del repository degli aggiornamenti su github.com in HTTPS: <c>UseShellExecute</c> aprirebbe
    /// qualsiasi cosa gli si passi, quindi l'indirizzo va verificato anche se arriva dal servizio.
    /// </summary>
    public static bool IsRepositoryPage(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.AbsolutePath.TrimEnd('/') + "/").StartsWith($"/{UpdateSource.Repository}/", StringComparison.OrdinalIgnoreCase);

    private void Apply(UpdateStatus status)
    {
        if (_disposed || status.Revision < _status.Revision) return;
        _status = status;
        Refresh();
    }

    /// <summary>Tutte le proprieta' dipendono dall'istantanea: un solo avviso "tutto cambiato" e i CanExecute riletti.</summary>
    private void Refresh()
    {
        if (_disposed) return;
        Raise(string.Empty);
        foreach (var command in _commands) command.RaiseCanExecuteChanged();
    }

    private async Task RunAsync(Func<Task<UpdateStatus>> command, string what)
    {
        if (_disposed || IsBusy) return;
        _pending = true;
        CommandError = "";
        Refresh();
        try
        {
            Apply(await command());
        }
        catch (UpdateException ex)
        {
            // Messaggio gia' pronto per l'utente e privo di segreti per contratto; il servizio ha gia' loggato.
            CommandError = ex.Message;
        }
        catch (Exception ex)
        {
            _log.Error($"Updater: {what} dalle Impostazioni non riuscito", ex);
            CommandError = UpdateMessages.GenericFailure;
        }
        finally
        {
            _pending = false;
            Refresh();
        }
    }

    private async Task CancelAuthenticationAsync()
    {
        if (_disposed || _cancelling) return;
        _cancelling = true;
        Refresh();
        try
        {
            Apply(await _updates.CancelAuthenticationAsync());
        }
        catch (UpdateException ex)
        {
            CommandError = ex.Message;
        }
        catch (Exception ex)
        {
            _log.Error("Updater: annullamento del collegamento GitHub non riuscito", ex);
            CommandError = UpdateMessages.GenericFailure;
        }
        finally
        {
            _cancelling = false;
            Refresh();
        }
    }

    private void OpenRelease()
    {
        CommandError = "";
        try
        {
            var url = _updates.ReleaseUrl();
            if (!IsRepositoryPage(url))
            {
                _log.Warn("Updater: indirizzo della release fuori dal repository, non aperto");
                CommandError = UpdateMessages.BrowserFailed;
                return;
            }
            // Browser di sistema, come ChessAdvisor con shell.openExternal: mai una finestra dell'app.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (UpdateException ex)
        {
            CommandError = ex.Message;
        }
        catch (Exception ex)
        {
            _log.Error("Updater: apertura della pagina della release non riuscita", ex);
            CommandError = UpdateMessages.BrowserFailed;
        }
    }

    private static string Megabytes(long bytes) => $"{(bytes / 1048576.0).ToString("0.0", Italian)} MB";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _updates.Changed -= _changed;
    }
}
