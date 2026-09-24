using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Controls;
using AIUsageMonitor.App.Startup;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App.Settings;

public partial class SettingsWindow : Window
{
    /// <summary>Indice della scheda Aggiornamenti nel TabControl della XAML.</summary>
    public const int UpdatesTabIndex = 5;

    private static SettingsWindow? _instance;

    private SettingsWindow(AppServices services)
    {
        InitializeComponent();
        var vm = new SettingsViewModel(services);
        vm.Saved += Close;
        DataContext = vm;
        SizeChanged += (_, _) => KeepInsideWorkArea();
        Closed += (_, _) =>
        {
            _instance = null;
            // Stacca il gruppo aggiornamenti da UpdateService.Changed: il servizio vive quanto l'app, la finestra no.
            vm.Dispose();
        };
    }

    /// <param name="showUpdates">Apre la scheda Aggiornamenti (argomento --updates, notifica di una release passata).</param>
    public static void ShowSingleton(AppServices services, bool showUpdates = false)
    {
        if (_instance is null)
        {
            var window = new SettingsWindow(services);
            _instance = window;
            if (showUpdates) ((SettingsViewModel)window.DataContext).SelectedTabIndex = UpdatesTabIndex;
            window.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            if (showUpdates) ((SettingsViewModel)_instance.DataContext).SelectedTabIndex = UpdatesTabIndex;
        }
    }

    /// <summary>
    /// Riporta in primo piano la finestra, se aperta, quando il login GitHub nel browser e' finito. Il browser ha il
    /// primo piano e Windows puo' limitarsi a far lampeggiare il pulsante nella barra: Topmost acceso e spento la porta
    /// comunque sopra, senza lasciarla fissa in cima.
    /// </summary>
    public static void BringToFrontIfOpen()
    {
        if (_instance is not { } window) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
    }

    /// <summary>Il contenuto della scheda nuova entra in dissolvenza (150 ms); niente con le animazioni di Windows spente.</summary>
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged risale anche dalle ComboBox dentro le schede: conta solo quello del TabControl.
        if (!ReferenceEquals(e.OriginalSource, Tabs) || !MotionSettings.IsEnabled) return;
        if (Tabs.Template?.FindName("PART_SelectedContentHost", Tabs) is UIElement host)
            host.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150))));
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
    /// Limita l'altezza della finestra all'area di lavoro del monitor su cui e' apparsa, cosi' le
    /// <c>ScrollViewer</c> delle schede mostrano la barra invece di far tagliare il contenuto. Va calcolato qui e non con
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
