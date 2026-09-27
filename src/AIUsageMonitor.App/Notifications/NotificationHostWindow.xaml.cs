using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Controls;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.Core.Notch;
using Microsoft.Win32;

namespace AIUsageMonitor.App.Notifications;

/// <summary>
/// Finestra unica della pila delle notifiche (spec 2026-09-27 §5.3, §6): trasparente, sempre in primo piano, non prende
/// mai il focus e non compare in taskbar ne' in Alt+Tab (<c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>, come il notch).
/// Si ancora al notch con <see cref="NotificationPlacement"/> e si riposiziona a ogni
/// <see cref="INotchHost.AnchorChanged"/>, al cambio di altezza, di DPI e degli schermi. Cosa mostrare lo decide
/// <see cref="NotificationHostViewModel"/>: qui stanno solo posizione, animazioni e input.
/// </summary>
public sealed partial class NotificationHostWindow : Window
{
    /// <summary>Margine trasparente per l'ombra su ogni lato: la finestra e' larga 340 + 2 x 16 DIP.</summary>
    public const double ShadowMargin = 16;

    private const double SlideDistance = 24;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly Duration EnterDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration ExitDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly Duration ReflowDuration = new(TimeSpan.FromMilliseconds(180));

    /// <summary>
    /// Fra una card e l'altra c'e' uno spazio trasparente, che in una finestra a livelli non riceve il mouse: l'uscita
    /// conta solo se il mouse non rientra entro questo tempo, altrimenti ogni passaggio da una card all'altra farebbe
    /// ripartire i timer per un istante.
    /// </summary>
    private static readonly TimeSpan HoverLeaveDelay = TimeSpan.FromMilliseconds(150);

    private readonly NotificationHostViewModel _viewModel;
    private readonly INotchHost _notch;
    private readonly DispatcherTimer _hoverLeave;
    private readonly Dictionary<NotificationCardViewModel, FrameworkElement> _roots = new();
    private readonly Dictionary<NotificationCardViewModel, ScaleTransform> _timers = new();
    private readonly HashSet<NotificationCardViewModel> _entered = new();
    private NotificationCardViewModel? _pressed;
    private bool _reflowPending;
    private bool _closing;

    public NotificationHostWindow(NotificationHostViewModel viewModel, INotchHost notch)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _notch = notch;
        DataContext = viewModel;

        _hoverLeave = new DispatcherTimer { Interval = HoverLeaveDelay };
        _hoverLeave.Tick += (_, _) =>
        {
            _hoverLeave.Stop();
            if (!Stack.IsMouseOver) _viewModel.SetHover(false);
        };

        foreach (var card in viewModel.Cards) card.PropertyChanged += OnCardChanged;
        viewModel.Cards.CollectionChanged += OnCardsChanged;
        viewModel.PropertyChanged += OnViewModelChanged;
        notch.AnchorChanged += OnAnchorChanged;
        SizeChanged += (_, _) => Reposition();
        DpiChanged += (_, _) => Reposition();
        Loaded += (_, _) => Reposition();
        // Nascosta per qualunque motivo, la finestra chiude il passaggio del mouse da sola: WPF puo' non mandare
        // MouseLeave a una finestra nascosta sotto il mouse, e il board terrebbe fermi i timer.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) _hoverLeave.Stop();
            _viewModel.SetShown(IsVisible);
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>
    /// Da chiamare dopo ogni <see cref="NotificationHostViewModel.Sync"/>: senza card la finestra si nasconde (e il
    /// passaggio del mouse si azzera, altrimenti i timer del board resterebbero fermi); con almeno una card, anche se
    /// sta uscendo, si mostra senza attivarsi e si riposiziona. Dopo <see cref="Window.Close"/> non fa nulla.
    /// </summary>
    public void Refresh()
    {
        if (_closing) return;
        if (_viewModel.Cards.Count == 0)
        {
            _hoverLeave.Stop();
            _viewModel.SetHover(false);
            if (IsVisible) Hide();
            return;
        }
        if (!IsVisible) Show();
        Reposition();
    }

    /// <summary>
    /// Come <c>NotchWindow.Reposition</c>: la posizione si calcola in pixel fisici con la scala del monitor del notch
    /// (scala 1 sull'area, gia' in pixel, e ogni misura in DIP moltiplicata per la scala) e si applica con
    /// <c>SetWindowPos</c>, perche' Left/Top verrebbero convertiti con la scala del monitor su cui la finestra si trova
    /// ora. Se lo spostamento cambia monitor arriva un WM_DPICHANGED e <see cref="Window.DpiChanged"/> richiama questo
    /// metodo come seconda passata, a finestra ormai ridimensionata con la nuova scala.
    /// </summary>
    private void Reposition()
    {
        var anchor = _notch.Anchor;
        var height = ActualHeight > 0 ? ActualHeight : 2 * ShadowMargin;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            // Prima di OnSourceInitialized non c'e' un HWND: si ripiega su Left/Top, tanto Loaded richiama Reposition.
            var (leftDip, topDip) = NotificationPlacement.Compute(anchor, Width, height, ShadowMargin);
            Left = leftDip;
            Top = topDip;
            return;
        }

        var scale = anchor.DpiScale > 0 ? anchor.DpiScale : 1.0;
        var inPixels = anchor with { DpiScale = 1.0, RightInsetDip = anchor.RightInsetDip * scale };
        var (left, top) = NotificationPlacement.Compute(
            inPixels, Width * scale, height * scale, ShadowMargin * scale, NotificationPlacement.GapDip * scale);
        SetWindowPos(handle, IntPtr.Zero, (int)Math.Round(left), (int)Math.Round(top), 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

        // Notch nascosto: meta' del margine dell'ombra sporge oltre l'area di lavoro. L'ombra e' tenue ma non trasparente
        // fino al bordo della finestra, e una finestra a livelli prende il mouse dove l'alfa non e' zero: senza taglio
        // ruberebbe i clic al monitor accanto (o a una taskbar a destra). Si taglia solo la parte che sporge; la
        // finestra resta dov'e', quindi DPI e posizione delle card non cambiano.
        var overhang = NotificationPlacement.RightOverhang(inPixels, Math.Round(left), Width * scale) / scale;
        Stack.Clip = overhang > 0
            ? new RectangleGeometry(new Rect(-ShadowMargin, -ShadowMargin, Math.Max(0, Width - overhang), height))
            : null;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        // Da qui Refresh non mostra piu' la finestra: Show() su una finestra in chiusura lancia.
        if (!e.Cancel) _closing = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _closing = true;
        _notch.AnchorChanged -= OnAnchorChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _hoverLeave.Stop();
        base.OnClosed(e);
    }

    private void OnAnchorChanged()
    {
        if (IsVisible) Reposition();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => UiDispatcher.Post(() =>
    {
        if (IsVisible) Reposition();
    });

    // --- Card: entrata, uscita, scorrimento -------------------------------------------------------------------------

    /// <summary>
    /// Entrata (spec §6): da 24 DIP a destra, in dissolvenza, 220 ms CubicEase out; con le animazioni di Windows spente
    /// solo la dissolvenza. Una card gia' entrata che viene ricaricata (spostata nella pila) non rientra: torna subito
    /// al suo stato finale.
    /// </summary>
    private void Card_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NotificationCardViewModel card } root) return;
        _roots[card] = root;
        if (card.IsLeaving)
        {
            PlayExit(card);
            return;
        }
        if (_entered.Add(card))
        {
            PlayEnter(root);
            return;
        }
        var slide = Slide(root);
        root.BeginAnimation(OpacityProperty, null);
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        root.Opacity = 1;
        slide.X = 0;
    }

    private void Card_Unloaded(object sender, RoutedEventArgs e)
    {
        foreach (var (card, root) in _roots.ToList())
            if (ReferenceEquals(root, sender)) _roots.Remove(card);
    }

    private static void PlayEnter(FrameworkElement root)
    {
        root.IsHitTestVisible = true;
        var slide = Slide(root);
        root.BeginAnimation(OpacityProperty, new DoubleAnimation(1, EnterDuration) { EasingFunction = EaseOut() });
        if (MotionSettings.IsEnabled)
        {
            slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, EnterDuration) { EasingFunction = EaseOut() });
            return;
        }
        slide.BeginAnimation(TranslateTransform.XProperty, null);
        slide.X = 0;
    }

    /// <summary>
    /// Uscita (spec §6): 160 ms verso destra in dissolvenza (solo dissolvenza con le animazioni spente), poi la card
    /// lascia la pila. A finestra nascosta, o per una card mai caricata, esce subito. Una card uscente non riceve piu'
    /// clic.
    /// </summary>
    private void PlayExit(NotificationCardViewModel card)
    {
        if (!IsVisible || !_roots.TryGetValue(card, out var root))
        {
            FinishExit(card);
            return;
        }
        root.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, ExitDuration) { EasingFunction = EaseIn() };
        fade.Completed += (_, _) => FinishExit(card);
        root.BeginAnimation(OpacityProperty, fade);
        if (MotionSettings.IsEnabled)
            Slide(root).BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(SlideDistance, ExitDuration) { EasingFunction = EaseIn() });
    }

    /// <summary>Toglie la card uscita (se il board non l'ha fatta tornare) e, a finestra visibile, la nasconde se era l'ultima.</summary>
    private void FinishExit(NotificationCardViewModel card)
    {
        if (!card.IsLeaving) return;
        _viewModel.CompleteExit(card);
        if (IsVisible) Refresh();
    }

    private void OnCardsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Move)
        {
            foreach (NotificationCardViewModel card in e.OldItems ?? Array.Empty<object>())
            {
                card.PropertyChanged -= OnCardChanged;
                _entered.Remove(card);
                _roots.Remove(card);
                _timers.Remove(card);
            }
            foreach (NotificationCardViewModel card in e.NewItems ?? Array.Empty<object>())
                card.PropertyChanged += OnCardChanged;
        }
        CaptureReflow();
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not NotificationCardViewModel card) return;
        switch (e.PropertyName)
        {
            case nameof(NotificationCardViewModel.IsLeaving):
                if (card.IsLeaving) PlayExit(card);
                else if (_roots.TryGetValue(card, out var root)) PlayEnter(root);
                break;
            case nameof(NotificationCardViewModel.TimerFraction):
            case nameof(NotificationCardViewModel.HasTimer):
                if (_timers.TryGetValue(card, out var scale)) RunTimer(card, scale, fromCurrent: false);
                break;
        }
    }

    /// <summary>
    /// Le card rimaste scorrono al nuovo posto in 180 ms (spec §6). Prima del layout si annota dove sta ogni card sullo
    /// schermo (compresa la traslazione di uno scorrimento ancora in corso); al primo LayoutUpdated dopo il cambiamento
    /// si riposiziona la finestra (l'altezza nuova la sposta, perche' la pila e' centrata sulla linguetta) e ogni card
    /// che si e' mossa riparte dalla vecchia posizione con una traslazione verticale che torna a zero. Con le
    /// animazioni di Windows spente le card saltano al loro posto.
    /// </summary>
    private void CaptureReflow()
    {
        if (_reflowPending || !IsVisible || !MotionSettings.IsEnabled) return;
        var before = new Dictionary<NotificationCardViewModel, double>();
        foreach (var (card, root) in _roots)
            if (ScreenTop(root) is { } top) before[card] = top + Slide(root).Y;
        if (before.Count == 0) return;
        _reflowPending = true;
        LayoutUpdated += Play;

        void Play(object? s, EventArgs a)
        {
            LayoutUpdated -= Play;
            _reflowPending = false;
            Reposition();
            foreach (var (card, oldTop) in before)
            {
                if (!_roots.TryGetValue(card, out var root) || ScreenTop(root) is not { } newTop) continue;
                var delta = oldTop - newTop;
                if (Math.Abs(delta) < 0.5) continue;
                Slide(root).BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(delta, 0, ReflowDuration) { EasingFunction = EaseOut() });
            }
        }
    }

    /// <summary>Bordo superiore del posto della card sullo schermo, in DIP, senza le sue traslazioni; null fuori dall'albero.</summary>
    private double? ScreenTop(FrameworkElement root)
    {
        if (VisualTreeHelper.GetParent(root) is not Visual container || !container.IsDescendantOf(this)) return null;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var rect)) return null;
        var offset = container.TransformToAncestor(this).Transform(new Point(0, 0));
        return rect.Top / VisualTreeHelper.GetDpi(this).DpiScaleY + offset.Y;
    }

    /// <summary>
    /// La traslazione della radice della card, sempre modificabile: una trasformata dichiarata in un template potrebbe
    /// arrivare congelata, e <c>BeginAnimation</c> su un Freezable congelato lancia.
    /// </summary>
    private static TranslateTransform Slide(FrameworkElement root)
    {
        if (root.RenderTransform is TranslateTransform { IsFrozen: false } slide) return slide;
        var fresh = root.RenderTransform is TranslateTransform frozen ? new TranslateTransform(frozen.X, frozen.Y) : new TranslateTransform();
        root.RenderTransform = fresh;
        return fresh;
    }

    private static IEasingFunction EaseOut() => new CubicEase { EasingMode = EasingMode.EaseOut };
    private static IEasingFunction EaseIn() => new CubicEase { EasingMode = EasingMode.EaseIn };

    // --- Barra del timer -------------------------------------------------------------------------------------------

    private void TimerBar_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: NotificationCardViewModel card } bar) return;
        if (bar.RenderTransform is not ScaleTransform { IsFrozen: false } scale)
        {
            scale = new ScaleTransform();
            bar.RenderTransform = scale;
        }
        _timers[card] = scale;
        RunTimer(card, scale, fromCurrent: false);
    }

    private void TimerBar_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement bar) return;
        foreach (var (card, scale) in _timers.ToList())
            if (ReferenceEquals(scale, bar.RenderTransform)) _timers.Remove(card);
    }

    /// <summary>
    /// La barra del timer (spec §5.2) parte dalla frazione rimasta e si svuota a velocita' costante, 1/AutoClose al
    /// secondo, fino a zero: non serve un tick per muoverla. Ogni nuovo <see cref="NotificationCardViewModel.TimerFraction"/>
    /// (il tick da 1 s del servizio) la riallinea al valore esatto del board. Col mouse sopra la pila si ferma dov'e'
    /// (<paramref name="fromCurrent"/>) e all'uscita riparte da li'. Con le animazioni di Windows spente segue solo i tick.
    /// </summary>
    private void RunTimer(NotificationCardViewModel card, ScaleTransform scale, bool fromCurrent)
    {
        var current = scale.ScaleX;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        if (card.Card.AutoClose is not { } total || total <= TimeSpan.Zero)
        {
            scale.ScaleX = 1;
            return;
        }
        var from = Math.Clamp(fromCurrent ? current : card.TimerFraction, 0, 1);
        scale.ScaleX = from;
        if (_viewModel.IsHovering || !MotionSettings.IsEnabled || from <= 0) return;
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(from, 0, new Duration(TimeSpan.FromTicks((long)(total.Ticks * from)))));
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NotificationHostViewModel.IsHovering)) return;
        foreach (var (card, scale) in _timers) RunTimer(card, scale, fromCurrent: true);
    }

    // --- Input -----------------------------------------------------------------------------------------------------

    private void Stack_MouseEnter(object sender, MouseEventArgs e)
    {
        _hoverLeave.Stop();
        _viewModel.SetHover(true);
    }

    private void Stack_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverLeave.Stop();
        _hoverLeave.Start();
    }

    /// <summary>Il clic parte al rilascio sulla stessa card, come per un pulsante: un drag partito altrove non attiva nulla.</summary>
    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressed = (sender as FrameworkElement)?.DataContext as NotificationCardViewModel;
        e.Handled = true;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var pressed = _pressed;
        _pressed = null;
        if ((sender as FrameworkElement)?.DataContext is not NotificationCardViewModel card || !ReferenceEquals(card, pressed)) return;
        e.Handled = true;
        _viewModel.Activate(card);
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is NotificationCardViewModel card) _viewModel.Dismiss(card);
    }

    private void More_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _viewModel.ActivateMore();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}
