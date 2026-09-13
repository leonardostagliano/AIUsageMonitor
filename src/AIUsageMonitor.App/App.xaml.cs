using System.Windows;
using System.Windows.Threading;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.App.Notifications;
using AIUsageMonitor.App.Settings;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.App.Tray;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private AppServices? _services;
    private TrayIconController? _tray;
    private NotchWindow? _notch;
    private ToastService? _toasts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _single = SingleInstance.TryAcquire();
        if (_single is null)
        {
            SingleInstance.SignalShowNotch();
            Shutdown();
            return;
        }

        _services = AppServices.Create();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => _services.Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => { _services.Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
        // Gli handler della NotifyIcon girano dentro NativeWindow.Callback di WinForms, che instrada le eccezioni a
        // WinForms.Application.ThreadException e NON a DispatcherUnhandledException: senza questo handler comparirebbe
        // la finestra "Unhandled exception" di WinForms, il cui pulsante Esci chiama Environment.Exit e salta OnExit.
        WinForms.Application.ThreadException += (_, args) => _services.Log.Error("Unhandled WinForms exception", args.Exception);

        try
        {
            var notch = new NotchWindow(_services, new NotchViewModel(_services));
            _notch = notch;
            if (_services.Settings.Current.NotchVisible) notch.Show();
            _tray = new TrayIconController(_services, notch);
            _tray.OpenSettings = () => SettingsWindow.ShowSingleton(_services);
            _single.ShowNotchRequested += () => Dispatcher.BeginInvoke(notch.Pin);

            _services.Start();
            // Dopo Start(): il replay silenzioso della pump e' gia' finito, quindi la cronologia non genera toast.
            _toasts = new ToastService(_services, (title, text, icon) => UiDispatcher.Post(() => _tray?.ShowBalloon(title, text, icon)));

            // Argomento di debug: apre subito le impostazioni, utile per verificare l'aspetto senza passare dal tray.
            if (e.Args.Contains("--settings")) SettingsWindow.ShowSingleton(_services);
            // Argomento di debug: apre il menu del tray al centro dello schermo, per fotografarlo senza dover
            // pilotare il click destro sull'area di notifica. Va rimandato a fine avvio, quando la finestra
            // nascosta di TrayMenuHost ha gia' un HWND da portare in primo piano.
            if (e.Args.Contains("--tray-menu"))
                Dispatcher.BeginInvoke(() => _tray?.ShowMenuAtScreenCentre(), DispatcherPriority.Background);
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
        _services?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
