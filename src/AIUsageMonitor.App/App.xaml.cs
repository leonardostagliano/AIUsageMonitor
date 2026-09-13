using System.Windows;
using System.Windows.Threading;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.App.Tray;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App;

public partial class App : Application
{
    private SingleInstance? _single;
    private AppServices? _services;
    private TrayIconController? _tray;

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
            INotchHost notch = new StubNotchHost();
            _tray = new TrayIconController(_services, notch);
            _single.ShowNotchRequested += () => Dispatcher.BeginInvoke(notch.Pin);

            _services.Start();
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
        _tray?.Dispose();
        _services?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }

    private sealed class StubNotchHost : INotchHost
    {
        public bool IsNotchVisible => false;
        public void Pin() { }
        public void TogglePin() { }
        public void ToggleVisible() { }
    }
}
