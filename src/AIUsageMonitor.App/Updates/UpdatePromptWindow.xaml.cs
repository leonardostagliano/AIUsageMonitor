using System.ComponentModel;
using System.Windows;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Startup;

namespace AIUsageMonitor.App.Updates;

/// <summary>
/// Finestra singola della conferma "scarica e riavvia", aperta dalla voce della tray o dal click sulla notifica. La
/// chiusura con X o Esc equivale a "Più tardi"; durante download e installazione non si chiude.
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private static UpdatePromptWindow? _instance;

    private readonly UpdatePromptViewModel _vm;
    private bool _closing;
    private bool _closed;
    private bool _skipDismiss;

    private UpdatePromptWindow(AppServices services)
    {
        InitializeComponent();
        _vm = new UpdatePromptViewModel(services.UpdatePrompt, services.Log);
        _vm.CloseRequested += OnCloseRequested;
        DataContext = _vm;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true;
            _vm.Dispose();
            if (ReferenceEquals(_instance, this)) _instance = null;
        };
    }

    /// <summary>
    /// Apre la conferma o la riporta in primo piano. False, senza aprire nulla, se non c'e' una versione da offrire (per
    /// esempio il click su una notifica rimasta nel centro notifiche da un avvio precedente).
    /// </summary>
    public static bool ShowSingleton(AppServices services)
    {
        if (_instance is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return true;
        }
        if (services.UpdatePrompt.State.Version is null) return false;
        var window = new UpdatePromptWindow(services);
        _instance = window;
        window.Show();
        window.Activate();
        // L'offerta puo' essere sparita tra il controllo qui sopra e l'abbonamento del ViewModel: in quel caso Sync
        // chiude subito la finestra invece di lasciare una conferma senza versione.
        window._vm.Sync();
        return true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        DarkTitleBar.Apply(this);
    }

    /// <summary>Il ViewModel non ha piu' nulla da offrire: chiusura senza un secondo "Più tardi".</summary>
    private void OnCloseRequested()
    {
        // Close dentro Closing lancerebbe InvalidOperationException: il Dismiss della X rientra qui in modo sincrono.
        if (_closing || _closed) return;
        _skipDismiss = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Il consenso e' gia' dato e l'app si chiudera' da sola. Allo spegnimento dell'app WPF chiude le finestre
        // ignorando Cancel, quindi QuitForInstall non viene bloccato da qui.
        if (_vm.IsBusy)
        {
            e.Cancel = true;
            return;
        }
        _closing = true;
        if (!_skipDismiss) _vm.Dismiss();
    }
}
