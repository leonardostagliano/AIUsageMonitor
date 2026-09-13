using System.Windows;
using System.Windows.Threading;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.App.Tray;

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

        INotchHost notch = new StubNotchHost();
        _tray = new TrayIconController(_services, notch);
        _single.ShowNotchRequested += () => Dispatcher.BeginInvoke(notch.Pin);

        _services.Start();
        _services.Log.Info("AIUsageMonitor started");
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        _services?.Log.Error("Unhandled UI exception", args.Exception);
        args.Handled = true;
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
