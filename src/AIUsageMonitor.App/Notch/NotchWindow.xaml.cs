using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Notch;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App.Notch;

public partial class NotchWindow : Window, INotchHost
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly Duration AnimationDuration = new(TimeSpan.FromMilliseconds(150));

    private readonly AppServices _services;
    private readonly DispatcherTimer _collapseTimer;
    private bool _expanded;
    private bool _pinned;

    public NotchWindow(AppServices services, object viewModel)
    {
        InitializeComponent();
        _services = services;
        DataContext = viewModel;

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(services.Settings.Current.CollapseDelayMs) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_pinned && !IsMouseOver) Collapse();
        };

        MouseEnter += (_, _) => _collapseTimer.Stop();
        MouseLeave += (_, _) => ScheduleCollapse();
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Reposition();
        Loaded += (_, _) => Reposition();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        services.Settings.Changed += _ => UiDispatcher.Post(ApplySettings);
    }

    public bool IsNotchVisible => IsVisible;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        base.OnClosed(e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => UiDispatcher.Post(Reposition);

    private void Tab_MouseEnter(object sender, MouseEventArgs e) => Expand();

    /// <summary>
    /// Gestisce il click sia sulla linguetta sia sullo sfondo del pannello: appena il pannello si apre copre la
    /// linguetta (che viene nascosta a fine animazione), quindi senza il secondo handler il click di fissaggio —
    /// e soprattutto quello di sblocco — sarebbe raggiungibile solo nei 150 ms dell'animazione. I controlli
    /// interattivi dentro il pannello marcano l'evento Handled e non arrivano qui.
    /// </summary>
    private void Tab_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        TogglePin();
        e.Handled = true;
    }

    public void Pin()
    {
        _pinned = true;
        PinGlyph.Visibility = Visibility.Visible;
        if (!IsVisible) Show();
        Expand();
    }

    public void Unpin()
    {
        _pinned = false;
        PinGlyph.Visibility = Visibility.Collapsed;
        if (!IsMouseOver) Collapse();
    }

    public void TogglePin()
    {
        if (_pinned) Unpin();
        else Pin();
    }

    public void ToggleVisible()
    {
        if (IsVisible) Hide();
        else Show();
        var settings = _services.Settings.Current.Clone();
        settings.NotchVisible = IsVisible;
        _services.Settings.Save(settings);
    }

    private void Expand()
    {
        if (_expanded) return;
        _expanded = true;
        _collapseTimer.Stop();
        Panel.Visibility = Visibility.Visible;
        Animate(PanelSlide, TranslateTransform.XProperty, 0);
        Animate(Panel, OpacityProperty, 1, () => { if (_expanded) Tab.Visibility = Visibility.Hidden; });
    }

    private void Collapse()
    {
        if (!_expanded || _pinned) return;
        _expanded = false;
        Tab.Visibility = Visibility.Visible;
        Animate(PanelSlide, TranslateTransform.XProperty, Panel.ActualWidth > 0 ? Panel.ActualWidth : Width);
        Animate(Panel, OpacityProperty, 0, () => { if (!_expanded) Panel.Visibility = Visibility.Hidden; });
    }

    private void ScheduleCollapse()
    {
        if (_pinned) return;
        _collapseTimer.Stop();
        _collapseTimer.Start();
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, AnimationDuration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (completed is not null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation);
    }

    private void ApplySettings()
    {
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(_services.Settings.Current.CollapseDelayMs);
        Reposition();
    }

    /// <summary>
    /// Ancora la finestra al bordo destro del monitor configurato, centrata verticalmente piu' l'offset configurato.
    /// La scala DPI e' quella del monitor di destinazione, non di quello su cui la finestra si trova ora: sono diverse
    /// appena <c>MonitorIndex</c> punta a uno schermo con un fattore di scala differente. Per lo stesso motivo la
    /// posizione viene applicata in pixel fisici con <c>SetWindowPos</c> invece che via <c>Left</c>/<c>Top</c>, che WPF
    /// convertirebbe in pixel usando la scala del monitor corrente, spedendo la finestra fuori da ogni schermo.
    /// Lo spostamento genera un WM_DPICHANGED: l'handler <c>DpiChanged</c> richiama questo metodo come seconda passata
    /// quando la finestra e' ormai ridimensionata con la scala di destinazione.
    /// </summary>
    public void Reposition()
    {
        var settings = _services.Settings.Current;
        var screens = WinForms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var screen = settings.MonitorIndex < screens.Length ? screens[settings.MonitorIndex] : WinForms.Screen.PrimaryScreen ?? screens[0];
        var area = screen.WorkingArea;
        var scale = GetScaleFor(area);

        PanelScroll.MaxHeight = Math.Max(120, area.Height / scale * 0.8 - 60);
        var height = ActualHeight > 0 ? ActualHeight : MinHeight;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            // Prima di OnSourceInitialized non c'e' un HWND: si ripiega su Left/Top, tanto Loaded richiama Reposition.
            var (leftDip, topDip) = NotchPlacement.Compute(area.Left, area.Top, area.Width, area.Height, scale, Width, height, settings.VerticalOffset);
            Left = leftDip;
            Top = topDip;
            return;
        }

        // Compute in pixel fisici: scala 1 sull'area (gia' in pixel) e misure della finestra convertite con la scala di destinazione.
        var (left, top) = NotchPlacement.Compute(
            area.Left, area.Top, area.Width, area.Height,
            1.0, Width * scale, height * scale, settings.VerticalOffset * scale);
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Scala DPI effettiva del monitor che contiene l'area indicata; ripiega sulla scala della finestra corrente.</summary>
    private double GetScaleFor(System.Drawing.Rectangle area)
    {
        var centre = new POINT { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 };
        var monitor = MonitorFromPoint(centre, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
            return dpiX / 96.0;
        return VisualTreeHelper.GetDpi(this).DpiScaleX;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
