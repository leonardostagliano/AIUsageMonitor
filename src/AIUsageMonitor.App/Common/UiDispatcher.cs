using System.Windows;
using System.Windows.Threading;

namespace AIUsageMonitor.App.Common;

/// <summary>Runs an action on the WPF UI thread; safe to call from Core background threads.</summary>
public static class UiDispatcher
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
    }
}
