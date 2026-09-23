using System.Windows;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.App.Updates;

/// <summary>
/// Conferma "scarica e riavvia" (porting di ChessAdvisor <c>AppUpdatePrompt</c>): un solo consenso copre download e
/// installazione della versione offerta da <see cref="UpdatePromptController"/>. Chiede la chiusura della finestra
/// quando non c'e' piu' nulla da offrire; durante l'operazione la finestra resta bloccata finche' l'app si chiude.
/// </summary>
public sealed class UpdatePromptViewModel : ObservableObject, IDisposable
{
    private readonly UpdatePromptController _controller;
    private readonly FileLogger _log;
    private readonly Action<UpdatePromptState> _changed;
    private UpdatePromptState _state;
    private string _localError = "";
    private bool _confirming;
    private bool _disposed;

    public UpdatePromptViewModel(UpdatePromptController controller, FileLogger log)
    {
        _controller = controller;
        _log = log;
        LaterCommand = new RelayCommand(Later, () => !IsBusy);
        ConfirmCommand = new RelayCommand(() => _ = ConfirmAsync(), () => CanConfirm);
        // Si rilegge lo stato corrente del controller invece di usare l'istantanea dell'evento: gli eventi arrivano da
        // thread diversi e l'ordine in cui raggiungono il thread UI non e' garantito. Prima l'abbonamento, poi la
        // lettura: un cambiamento nel mezzo non va perso.
        _changed = _ => UiDispatcher.Post(Refresh);
        controller.Changed += _changed;
        _state = controller.State;
    }

    /// <summary>
    /// Riallinea lo stato dopo che la finestra e' visibile: se nel frattempo l'offerta e' sparita chiede la chiusura,
    /// che la finestra ora puo' eseguire (nel costruttore nessuno ascolterebbe <see cref="CloseRequested"/>).
    /// </summary>
    public void Sync() => Refresh();

    /// <summary>Nessuna versione da offrire (rimandata, gia' installata, preferenza spenta): la finestra va chiusa.</summary>
    public event Action? CloseRequested;

    public RelayCommand LaterCommand { get; }
    public RelayCommand ConfirmCommand { get; }

    public string Lead => $"È disponibile AIUsageMonitor {_state.Version}.";

    public string Note =>
        "L'app si chiude e riparte da sola con la nuova versione: impostazioni e hook vengono conservati. " +
        "Puoi rimandare e aggiornare in seguito dalle Impostazioni.";

    /// <summary>Download o installazione in corso: la finestra non si chiude e "Più tardi" e' disattivato.</summary>
    public bool IsBusy => _state.Pending is not null || _confirming;

    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public string ProgressLabel => _state.Pending == UpdatePromptPending.Downloading
        ? "Download e verifica della nuova versione…"
        : "Installazione e riavvio dell'app…";

    public string PercentText => Percent is { } percent ? $"{percent:0}%" : "";
    public double ProgressValue => Percent ?? 0;
    public bool IsIndeterminate => Percent is null;

    public string Error => !string.IsNullOrEmpty(_state.Error) ? _state.Error : _localError;
    public Visibility ErrorVisibility => string.IsNullOrEmpty(Error) ? Visibility.Collapsed : Visibility.Visible;

    public string ConfirmLabel => _state.Pending switch
    {
        UpdatePromptPending.Downloading => "Download in corso…",
        UpdatePromptPending.Installing => "Riavvio in corso…",
        _ when _confirming => "Attendi…",
        _ => _state.Status?.CanInstall == true ? "Installa e riavvia" : "Scarica e riavvia"
    };

    /// <summary>Solo per la versione mostrata, e solo se il servizio sa ancora scaricarla o installarla.</summary>
    private bool CanConfirm =>
        !IsBusy
        && _state.Version is { } version
        && _state.Status is { } status
        && status.Release?.Version == version
        && (status.CanDownload || status.CanInstall);

    private double? Percent =>
        _state.Pending == UpdatePromptPending.Downloading && _state.Status?.Download is { TotalBytes: > 0 } download
            ? Math.Clamp(download.Percent, 0, 100)
            : null;

    /// <summary>"Più tardi" dalla chiusura della finestra (X): ignora questa versione fino al prossimo avvio.</summary>
    public void Dismiss()
    {
        if (_disposed || IsBusy) return;
        try { _controller.Dismiss(); }
        catch (Exception ex) { _log.Error("Updater: rinvio dell'aggiornamento non riuscito", ex); }
    }

    private void Later()
    {
        if (IsBusy) return;
        Dismiss();
        CloseRequested?.Invoke();
    }

    private async Task ConfirmAsync()
    {
        if (_disposed || !CanConfirm) return;
        _confirming = true;
        _localError = "";
        Refresh();
        try
        {
            await _controller.ConfirmAsync();
        }
        catch (Exception ex)
        {
            // Per contratto gli errori finiscono in UpdatePromptState.Error: questo e' solo il paracadute.
            _log.Error("Updater: conferma dell'aggiornamento non riuscita", ex);
            _localError = ex is UpdateException update ? update.Message : UpdateMessages.GenericFailure;
        }
        finally
        {
            _confirming = false;
            Refresh();
        }
    }

    private void Refresh()
    {
        if (_disposed) return;
        _state = _controller.State;
        if (_state.Version is null && _state.Pending is null && !_confirming)
        {
            CloseRequested?.Invoke();
            return;
        }
        Raise(string.Empty);
        LaterCommand.RaiseCanExecuteChanged();
        ConfirmCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.Changed -= _changed;
    }
}
