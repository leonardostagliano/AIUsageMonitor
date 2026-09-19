using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.App.Notifications;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Models;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App.Tray;

public sealed class TrayIconController : IDisposable
{
    private readonly AppServices _services;
    private readonly INotchHost _notch;
    private readonly WinForms.NotifyIcon _icon;
    private readonly AppNotificationSender _notifications;
    private readonly TrayMenuHost _menuHost;
    private readonly ContextMenu _menu;
    private readonly MenuItem _toggleNotch;
    private readonly MenuItem _autoStart;
    private readonly MenuItem _hooksClaude;
    private readonly MenuItem _hooksCodex;
    private readonly Action<string, string, NoticeKind> _notice;
    private readonly DispatcherTimer _singleClickTimer;
    private TrayIconRenderer.RenderedIcon? _rendered;

    /// <summary>Set by App.xaml.cs once the settings window exists (Task 7). Null-safe.</summary>
    public Action? OpenSettings { get; set; }

    public TrayIconController(AppServices services, INotchHost notch, AppNotificationSender notifications)
    {
        _services = services;
        _notch = notch;
        _notifications = notifications;

        // Il menu e' un ContextMenu WPF (Tray/TrayMenu.xaml) e non piu' una ContextMenuStrip: la striscia WinForms non
        // prende l'aspetto del notch (cromatura chiara, angoli vivi, font suoi). La NotifyIcon resta solo per icona,
        // e tooltip, quindi non le si assegna piu' nessuna ContextMenuStrip.
        _menuHost = new TrayMenuHost();
        _menu = new ContextMenu { Style = Resource<Style>("TrayContextMenu") };
        _menu.Items.Add(Item("Aggiorna ora", services.RefreshAll));
        _toggleNotch = Item("Nascondi notch", notch.ToggleVisible);
        _menu.Items.Add(_toggleNotch);

        var hooks = Item("Installa hook", null);
        _hooksClaude = Item("Claude Code", () => services.InstallHooks(AgentKind.Claude));
        _hooksCodex = Item("Codex", () => services.InstallHooks(AgentKind.Codex));
        hooks.Items.Add(_hooksClaude);
        hooks.Items.Add(_hooksCodex);
        _menu.Items.Add(hooks);

        _autoStart = Item("Avvio automatico", ToggleAutoStart);
        _menu.Items.Add(_autoStart);
        _menu.Items.Add(Item("Impostazioni…", () => OpenSettings?.Invoke()));
        _menu.Items.Add(new Separator { Style = Resource<Style>("TrayMenuSeparator") });
        _menu.Items.Add(Item("Esci", () => System.Windows.Application.Current.Shutdown()));

        // Spec 8.1 assegna due azioni distinte a click e doppio click, ma NotifyIcon alza MouseClick gia' sul primo
        // click di un doppio click (sopprime solo il secondo WM_LBUTTONUP): senza attesa ogni doppio click aprirebbe
        // le impostazioni fissando anche il notch. Il fissaggio parte quindi solo se entro DoubleClickTime non arriva
        // il doppio click, che ferma il timer.
        _singleClickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, WinForms.SystemInformation.DoubleClickTime)) };
        _singleClickTimer.Tick += (_, _) =>
        {
            _singleClickTimer.Stop();
            notch.TogglePin();
        };

        // Assign the icon before registering the NotifyIcon with Explorer, so the first shell registration already
        // carries the application's glyph instead of the default/empty icon.
        _icon = new WinForms.NotifyIcon { Visible = false, Text = "AIUsageMonitor" };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Left) return;
            _singleClickTimer.Stop();
            _singleClickTimer.Start();
        };
        // Il menu ora lo apriamo noi: la NotifyIcon lo faceva da sola solo finche' aveva una ContextMenuStrip. Il
        // try/catch e' obbligatorio come per le altre voci, qui siamo dentro NativeWindow.Callback di WinForms e
        // un'eccezione finirebbe nella finestra "Unhandled exception" della libreria.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button != WinForms.MouseButtons.Right) return;
            try { ShowMenu(); }
            catch (Exception ex) { _services.Log.Error("Tray menu open failed", ex); }
        };
        _icon.DoubleClick += (_, _) =>
        {
            _singleClickTimer.Stop();
            OpenSettings?.Invoke();
        };
        services.StateChanged += () => UiDispatcher.Post(UpdateIcon);
        // Unico renderer dei messaggi utente: AppServices.InstallHooks alza il Notice da entrambi i punti di ingresso
        // (menu tray e link "installa hook" nella card del notch), cosi' lo stesso click dice sempre la stessa cosa.
        _notice = (title, text, kind) => UiDispatcher.Post(() => ShowNotification(title, text, BalloonIcon(kind)));
        services.Notice += _notice;
        UpdateIcon();
        _icon.Visible = true;
    }

    public void ShowNotification(string title, string text, WinForms.ToolTipIcon kind)
    {
        // Severity remains explicit in the title; the toast always supplies the current app logo.
        var displayTitle = kind switch
        {
            WinForms.ToolTipIcon.Warning => $"Attenzione · {title}",
            WinForms.ToolTipIcon.Error => $"Errore · {title}",
            _ => title
        };
        _notifications.Show(displayTitle, text);
    }

    /// <summary>Apre il menu del tray sul puntatore, con le etichette dinamiche appena rilette.</summary>
    public void ShowMenu()
    {
        RefreshMenuState();
        _menuHost.Open(_menu);
    }

    /// <summary>Apre il menu al centro dello schermo primario: serve all'argomento di debug --tray-menu.</summary>
    public void ShowMenuAtScreenCentre()
    {
        RefreshMenuState();
        _menuHost.OpenAt(_menu, SystemParameters.PrimaryScreenWidth / 2, SystemParameters.PrimaryScreenHeight / 2);
    }

    private static MenuItem Item(string header, Action? click)
    {
        var item = new MenuItem { Header = header, Style = Resource<Style>("TrayMenuItem") };
        if (click is not null) item.Click += (_, _) => click();
        return item;
    }

    private static T Resource<T>(string key) => (T)System.Windows.Application.Current.FindResource(key);

    private static WinForms.ToolTipIcon BalloonIcon(NoticeKind kind) => kind switch
    {
        NoticeKind.Error => WinForms.ToolTipIcon.Error,
        NoticeKind.Warning => WinForms.ToolTipIcon.Warning,
        _ => WinForms.ToolTipIcon.Info
    };

    /// <summary>
    /// Ogni voce è protetta singolarmente: il menu deve aprirsi anche se leggere lo stato hook o il registro fallisce
    /// (es. sharing violation mentre Claude Code riscrive settings.json). Un'eccezione qui finirebbe in
    /// WinForms.Application.ThreadException, non in DispatcherUnhandledException.
    /// Va chiamata PRIMA di aprire il menu e non sull'evento Opened: le etichette cambiano larghezza e quando Opened
    /// scatta il popup e' gia' posizionato.
    /// </summary>
    private void RefreshMenuState()
    {
        _toggleNotch.Header = _notch.IsNotchVisible ? "Nascondi notch" : "Mostra notch";
        _autoStart.IsChecked = Safe(AutoStart.IsEnabled, false, "AutoStart.IsEnabled");
        _hooksClaude.Header = $"Claude Code — {StatusLabel(AgentKind.Claude)}";
        _hooksCodex.Header = $"Codex — {StatusLabel(AgentKind.Codex)}";
    }

    private T Safe<T>(Func<T> read, T fallback, string what)
    {
        try { return read(); }
        catch (Exception ex) { _services.Log.Error($"{what} failed", ex); return fallback; }
    }

    private string StatusLabel(AgentKind agent) => Safe(() => _services.HookStatus(agent).Status switch
    {
        Core.Hooks.HookStatus.Installed => "installati",
        Core.Hooks.HookStatus.Partial => "parziali, completa",
        Core.Hooks.HookStatus.ConfigInvalid => "config non valida",
        _ => "installa"
    }, "stato non disponibile", $"HookStatus {agent}");

    private void ToggleAutoStart()
    {
        try { AutoStart.SetEnabled(!AutoStart.IsEnabled()); }
        catch (Exception ex) { _services.Log.Error("AutoStart toggle failed", ex); }
    }

    private void UpdateIcon()
    {
        var color = PhaseVisuals.DrawingColor(_services.WorstPhase());
        var size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
        var next = TrayIconRenderer.Render(color, size);
        try
        {
            _icon.Icon = next.Icon;
            var text = _services.TooltipSummary();
            _icon.Text = text.Length > 63 ? text[..63] : text;
            var previous = _rendered;
            _rendered = next;
            previous?.Dispose();
        }
        catch
        {
            next.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _services.Notice -= _notice;
        _singleClickTimer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.IsOpen = false;
        _menuHost.Dispose();
        _rendered?.Dispose();
    }
}
