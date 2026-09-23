using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
        Closed += (_, _) =>
        {
            _instance = null;
            // Stacca il gruppo aggiornamenti da UpdateService.Changed: il servizio vive quanto l'app, la finestra no.
            vm.Dispose();
        };
    }

    /// <param name="showUpdates">Scorre al gruppo AGGIORNAMENTI (argomento --updates, notifica di una release passata).</param>
    public static void ShowSingleton(AppServices services, bool showUpdates = false)
    {
        if (_instance is null)
        {
            var window = new SettingsWindow(services);
            _instance = window;
            // Dopo Loaded la ScrollViewer ha misure e tetto all'altezza definitivi: prima non saprebbe dove scorrere.
            if (showUpdates) window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(() => window.ScrollToUpdates(), DispatcherPriority.Loaded);
            window.Show();
        }
        else
        {
            if (_instance.WindowState == WindowState.Minimized) _instance.WindowState = WindowState.Normal;
            _instance.Activate();
            if (showUpdates) _instance.ScrollToUpdates();
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

    /// <summary>Porta l'intestazione del gruppo AGGIORNAMENTI in cima all'area visibile (o il piu' in alto possibile).</summary>
    private void ScrollToUpdates()
    {
        var height = Math.Max(1, Scroll.ViewportHeight);
        UpdatesGroup.BringIntoView(new Rect(0, 0, Math.Max(1, UpdatesGroup.ActualWidth), height));
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

    // Click arriva prima del SaveCommand. Con Invio il pulsante IsDefault non prende il focus, quindi un binding
    // LostFocus (il tasso di riserva) non avrebbe ancora scritto nella bozza: lo si forza qui, prima del salvataggio.
    private void Save_Click(object sender, RoutedEventArgs e) =>
        (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
}
