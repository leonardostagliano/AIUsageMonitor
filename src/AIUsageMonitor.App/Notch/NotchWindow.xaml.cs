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

    /// <summary>Ancora la finestra al bordo destro del monitor configurato, centrata verticalmente piu' l'offset configurato.</summary>
    public void Reposition()
    {
        var settings = _services.Settings.Current;
        var screens = WinForms.Screen.AllScreens;
        if (screens.Length == 0) return;
        var screen = settings.MonitorIndex < screens.Length ? screens[settings.MonitorIndex] : WinForms.Screen.PrimaryScreen ?? screens[0];
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = screen.WorkingArea;

        PanelScroll.MaxHeight = Math.Max(120, area.Height / scale * 0.8 - 60);
        var height = ActualHeight > 0 ? ActualHeight : MinHeight;
        var (left, top) = NotchPlacement.Compute(area.Left, area.Top, area.Width, area.Height, scale, Width, height, settings.VerticalOffset);
        Left = left;
        Top = top;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
