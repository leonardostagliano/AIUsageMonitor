using System.Windows;
using AIUsageMonitor.App.Startup;

namespace AIUsageMonitor.App.Settings;

public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;

    private SettingsWindow(AppServices services)
    {
        InitializeComponent();
        var vm = new SettingsViewModel(services);
        vm.Saved += Close;
        DataContext = vm;
        Closed += (_, _) => _instance = null;
    }

    public static void ShowSingleton(AppServices services)
    {
        if (_instance is null)
        {
            _instance = new SettingsWindow(services);
            _instance.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
