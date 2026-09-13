using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AIUsageMonitor.App.Common;
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
        SizeChanged += (_, _) => KeepInsideWorkArea();
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
        // Prima del tetto all'altezza: qui l'HWND esiste e la barra del titolo non e' ancora stata disegnata chiara.
        DarkTitleBar.Apply(this);
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

    /// <summary>
    /// Riporta la finestra dentro l'area di lavoro dopo ogni cambio di dimensione. Serve perche'
    /// <c>WindowStartupLocation="CenterScreen"</c> centra sull'altezza DESIDERATA dal contenuto, calcolata prima che
    /// <see cref="ApplyWorkAreaCap"/> applichi <c>MaxHeight</c>: con un contenuto piu' alto dello schermo il Top
    /// risultante e' negativo e la barra del titolo finisce fuori dallo schermo, irraggiungibile. Copre anche i casi in
    /// cui il testo di stato degli hook va a capo e fa crescere la finestra verso il basso.
    /// </summary>
    private void KeepInsideWorkArea()
    {
        if (WindowState != WindowState.Normal || double.IsNaN(Top) || double.IsNaN(Left)) return;
        var handle = new WindowInteropHelper(this).Handle;
        var screen = handle == IntPtr.Zero ? WinForms.Screen.PrimaryScreen : WinForms.Screen.FromHandle(handle);
        if (screen is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var scaleX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
        var scaleY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
        var area = screen.WorkingArea;
        var top = area.Top / scaleY;
        var left = area.Left / scaleX;
        // Math.Max per ultimo: se la finestra e' comunque piu' grande dell'area di lavoro vince il bordo alto/sinistro,
        // quello che tiene visibili barra del titolo e pulsanti.
        Top = Math.Max(top, Math.Min(Top, area.Bottom / scaleY - ActualHeight));
        Left = Math.Max(left, Math.Min(Left, area.Right / scaleX - ActualWidth));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
