using System.Windows;
using System.Windows.Threading;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.App.Notifications;
using AIUsageMonitor.App.Settings;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.App.Tray;
using AIUsageMonitor.App.Updates;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private AppServices? _services;
    private TrayIconController? _tray;
    private NotchWindow? _notch;
    private ToastService? _toasts;
    private AppNotificationSender? _notifications;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Avvio dall'updater con "--updated <pid>": la versione precedente si sta chiudendo e tiene ancora il mutex di
        // istanza singola. Si aspetta la sua uscita (al massimo 30 s, un pid gia' uscito va bene) e poi si riprova il
        // mutex per qualche secondo: senza, questa istanza si crederebbe un secondo avvio e l'utente resterebbe senza
        // app. Lettura del pid e attesa non lanciano mai (qui non ci sono ancora log ne' il try contro lo zombie); il
        // mutex si comporta come il TryAcquire di sempre.
        var previousPid = UpdateRelaunch.PreviousProcessId(e.Args);
        var relaunch = previousPid is { } pid ? UpdateRelaunch.WaitForPreviousInstance(pid, UpdateRelaunch.PreviousExitTimeout) : null;

        _single = previousPid is null ? SingleInstance.TryAcquire() : UpdateRelaunch.AcquireSingleInstance(UpdateRelaunch.AcquireRetry);
        if (_single is null)
        {
            SingleInstance.SignalShowNotch();
            Shutdown();
            return;
        }

        _services = AppServices.Create();
        if (relaunch is not null) _services.Log.Info($"Avvio dopo l'aggiornamento alla versione {BuildInfo.CurrentVersion}: {relaunch}");
        // Existing installations predate the explicit startup marker. Migrate only an already-enabled Run entry;
        // registry failures must not prevent the tray/notch from starting.
        try { AutoStart.EnsureStartupArgument(); }
        catch (Exception ex) { _services.Log.Error("AutoStart migration failed", ex); }
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _services.Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { _services.Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
        // Gli handler della NotifyIcon girano dentro NativeWindow.Callback di WinForms, che instrada le eccezioni a
        // WinForms.Application.ThreadException e NON a DispatcherUnhandledException: senza questo handler comparirebbe
        // la finestra "Unhandled exception" di WinForms, il cui pulsante Esci chiama Environment.Exit e salta OnExit.
        WinForms.Application.ThreadException += (_, args) => _services.Log.Error("Unhandled WinForms exception", args.Exception);

        try
        {
            _notifications = new AppNotificationSender(_services.Paths.LocalAppDataDir, _services.Log, () => _notch?.Pin(), ShowUpdatePrompt);
            var notch = new NotchWindow(_services, new NotchViewModel(_services));
            _notch = notch;
            // Explorer starts Run entries after the interactive desktop is ready, but a persisted hidden state can
            // otherwise make an auto-started instance look like a tray-only process. The explicit startup marker
            // restores the primary surface for login launches; normal manual launches still honor the user's choice.
            if (_services.Settings.Current.NotchVisible || e.Args.Contains(AutoStart.StartupArgument, StringComparer.OrdinalIgnoreCase))
                notch.Show();
            _tray = new TrayIconController(_services, notch, _notifications);
            _tray.OpenSettings = () => SettingsWindow.ShowSingleton(_services);
            _tray.OpenUpdatePrompt = ShowUpdatePrompt;
            // Dopo il login GitHub nel browser si torna alle Impostazioni, da cui e' partito il collegamento.
            _services.ReturnToApp = SettingsWindow.BringToFrontIfOpen;
            _single.ShowNotchRequested += () => Dispatcher.BeginInvoke(notch.Pin);

            _services.Start();
            // Dopo Start(): il replay silenzioso della pump e' gia' finito, quindi la cronologia non genera toast.
            _toasts = new ToastService(_services, (title, text, icon) => UiDispatcher.Post(() => _tray?.ShowNotification(title, text, icon)));

            if (relaunch is not null)
                Dispatcher.BeginInvoke(() => _tray?.ShowNotification("AIUsageMonitor aggiornato",
                    $"Ora è in uso la versione {BuildInfo.CurrentVersion}. Impostazioni e hook sono stati conservati.", WinForms.ToolTipIcon.Info),
                    DispatcherPriority.Background);

            if (e.Args.Contains("--test-notification", StringComparer.OrdinalIgnoreCase))
                Dispatcher.BeginInvoke(() => _tray?.ShowNotification("AIUsageMonitor · verifica icona",
                    "Questa notifica usa il logo aggiornato. Clicca per aprire il notch.", WinForms.ToolTipIcon.Info), DispatcherPriority.Background);

            // Argomento di debug: apre subito le impostazioni, utile per verificare l'aspetto senza passare dal tray.
            if (e.Args.Contains("--settings")) SettingsWindow.ShowSingleton(_services);
            // Argomento di debug: apre le impostazioni gia' scorse al gruppo AGGIORNAMENTI.
            if (e.Args.Contains("--updates")) SettingsWindow.ShowSingleton(_services, showUpdates: true);
            // Argomento di debug: apre il menu del tray al centro dello schermo, per fotografarlo senza dover
            // pilotare il click destro sull'area di notifica. Va rimandato a fine avvio, quando la finestra
            // nascosta di TrayMenuHost ha gia' un HWND da portare in primo piano.
            if (e.Args.Contains("--tray-menu"))
                Dispatcher.BeginInvoke(() => _tray?.ShowMenuAtScreenCentre(), DispatcherPriority.Background);
            // Argomento di debug: porta in primo piano il terminale di una sessione senza passare dal click nel
            // notch, per verificare la catena delle strategie dal log.
            var focus = Array.IndexOf(e.Args, "--focus-session");
            if (focus >= 0 && focus + 1 < e.Args.Length) _ = FocusSessionAsync(_services, e.Args[focus + 1]);
        }
        catch (Exception ex)
        {
            // OnStartup gira come dispatcher operation: senza questo catch l'eccezione finirebbe in
            // OnDispatcherUnhandledException, verrebbe marcata Handled e l'app resterebbe viva senza icona
            // (o senza servizi) trattenendo il mutex di istanza singola: uno zombie invisibile.
            _services.Log.Error("Startup failed", ex);
            _tray?.Dispose();
            _tray = null;
            Shutdown(1);
            return;
        }

        _services.Log.Info("AIUsageMonitor started");
        // A ogni avvio riuscito, non solo dopo un aggiornamento: l'exe .old-* della versione precedente si libera solo
        // quando quel processo e' uscito, e un avvio precedente puo' averlo trovato ancora bloccato.
        var log = _services.Log;
        UpdateRelaunch.ScheduleLeftoverCleanup(log.Info, (message, ex) => log.Error(message, ex));
    }

    /// <summary>
    /// Voce della tray e click sulla notifica "aggiornamento disponibile": apre la conferma se c'e' una versione da
    /// offrire, altrimenti le Impostazioni sul gruppo AGGIORNAMENTI (es. una notifica rimasta nel centro notifiche da un
    /// avvio precedente, quando il nuovo controllo non e' ancora arrivato).
    /// </summary>
    private void ShowUpdatePrompt()
    {
        if (_services is not { } services) return;
        if (!UpdatePromptWindow.ShowSingleton(services)) SettingsWindow.ShowSingleton(services, showUpdates: true);
    }

    /// <summary>
    /// Corpo di <c>--focus-session</c>: aspetta che il replay silenzioso della pump abbia ricostruito le sessioni,
    /// poi chiede il focus e ne scrive l'esito nel log. Solo diagnostica: non tocca la UI.
    /// </summary>
    private static async Task FocusSessionAsync(AppServices services, string sessionId)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            var session = services.Sessions.Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (session is null)
            {
                services.Log.Warn($"--focus-session {sessionId}: sessione sconosciuta ({services.Sessions.Sessions.Count} sessioni note)");
                return;
            }
            var focused = await services.FocusTerminalAsync(session);
            services.Log.Info($"--focus-session {sessionId}: {(focused ? "terminale in primo piano" : "Terminale non trovato")}");
        }
        catch (Exception ex)
        {
            services.Log.Error("--focus-session", ex);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        _services?.Log.Error("Unhandled UI exception", args.Exception);
        args.Handled = true;
        // Se l'icona non esiste ancora non c'è nessuna UI da cui uscire (ShutdownMode=OnExplicitShutdown):
        // meglio terminare che restare vivi trattenendo il mutex di istanza singola.
        if (_tray is null) Shutdown(1);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notch?.Close();
        _tray?.Dispose();
        _notifications?.Dispose();
        _services?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
