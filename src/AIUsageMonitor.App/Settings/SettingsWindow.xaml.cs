using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AIUsageMonitor.App.Startup;
using WinForms = System.Windows.Forms;

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyWorkAreaCap();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ApplyWorkAreaCap();
    }

    /// <summary>
    /// Limita l'altezza auto-dimensionata all'area di lavoro del monitor su cui la finestra e' apparsa, cosi' la
    /// <c>ScrollViewer</c> della XAML mostra la barra invece di far tagliare il contenuto. Va calcolato qui e non con
    /// <c>SystemParameters.WorkArea</c> (sempre il monitor primario) perche' su un secondo schermo piu' basso o con una
    /// scala DPI diversa il tetto sarebbe sbagliato. L'area di lavoro e' in pixel fisici: si converte in DIP con la
    /// scala del monitor corrente, disponibile dopo che la finestra ha un HWND.
    /// </summary>
    private void ApplyWorkAreaCap()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle == IntPtr.Zero ? WinForms.Screen.PrimaryScreen : WinForms.Screen.FromHandle(handle);
        if (screen is null) return;
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        if (scale <= 0) scale = 1.0;
        // 48 DIP lasciati a barra del titolo e bordi, con un minimo prudenziale per schermi assurdamente bassi.
        MaxHeight = Math.Max(240, screen.WorkingArea.Height / scale - 48);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
