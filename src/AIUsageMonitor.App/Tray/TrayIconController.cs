using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Models;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App.Tray;

public sealed class TrayIconController : IDisposable
{
    private readonly AppServices _services;
    private readonly INotchHost _notch;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _toggleNotch;
    private readonly WinForms.ToolStripMenuItem _autoStart;
    private readonly WinForms.ToolStripMenuItem _hooksClaude;
    private readonly WinForms.ToolStripMenuItem _hooksCodex;
    private readonly Action<string, string, NoticeKind> _notice;
    private TrayIconRenderer.RenderedIcon? _rendered;

    /// <summary>Set by App.xaml.cs once the settings window exists (Task 7). Null-safe.</summary>
    public Action? OpenSettings { get; set; }

    public TrayIconController(AppServices services, INotchHost notch)
    {
        _services = services;
        _notch = notch;

        _menu = new WinForms.ContextMenuStrip();
        _menu.Items.Add("Aggiorna ora", null, (_, _) => services.RefreshAll());
        _toggleNotch = new WinForms.ToolStripMenuItem("Nascondi notch", null, (_, _) => notch.ToggleVisible());
        _menu.Items.Add(_toggleNotch);

        var hooks = new WinForms.ToolStripMenuItem("Installa hook");
        _hooksClaude = new WinForms.ToolStripMenuItem("Claude Code", null, (_, _) => services.InstallHooks(AgentKind.Claude));
        _hooksCodex = new WinForms.ToolStripMenuItem("Codex", null, (_, _) => services.InstallHooks(AgentKind.Codex));
        hooks.DropDownItems.Add(_hooksClaude);
        hooks.DropDownItems.Add(_hooksCodex);
        _menu.Items.Add(hooks);

        _autoStart = new WinForms.ToolStripMenuItem("Avvio automatico", null, (_, _) => ToggleAutoStart());
        _menu.Items.Add(_autoStart);
        _menu.Items.Add("Impostazioni…", null, (_, _) => OpenSettings?.Invoke());
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add("Esci", null, (_, _) => System.Windows.Application.Current.Shutdown());
        _menu.Opening += (_, _) => RefreshMenuState();

        _icon = new WinForms.NotifyIcon { Visible = true, Text = "AIUsageMonitor", ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) notch.TogglePin(); };
        _icon.DoubleClick += (_, _) => OpenSettings?.Invoke();
        _icon.BalloonTipClicked += (_, _) => notch.Pin();

        services.StateChanged += () => UiDispatcher.Post(UpdateIcon);
        // Unico renderer dei messaggi utente: AppServices.InstallHooks alza il Notice da entrambi i punti di ingresso
        // (menu tray e link "installa hook" nella card del notch), cosi' lo stesso click dice sempre la stessa cosa.
        _notice = (title, text, kind) => UiDispatcher.Post(() => ShowBalloon(title, text, BalloonIcon(kind)));
        services.Notice += _notice;
        UpdateIcon();
    }

    public void ShowBalloon(string title, string text, WinForms.ToolTipIcon kind) =>
        _icon.ShowBalloonTip(5000, title, text, kind);

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
    /// </summary>
    private void RefreshMenuState()
    {
        _toggleNotch.Text = _notch.IsNotchVisible ? "Nascondi notch" : "Mostra notch";
        _autoStart.Checked = Safe(AutoStart.IsEnabled, false, "AutoStart.IsEnabled");
        _hooksClaude.Text = $"Claude Code — {StatusLabel(AgentKind.Claude)}";
        _hooksCodex.Text = $"Codex — {StatusLabel(AgentKind.Codex)}";
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
        _icon.Icon = next.Icon;
        _rendered?.Dispose();
        _rendered = next;
        var text = _services.TooltipSummary();
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void Dispose()
    {
        _services.Notice -= _notice;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _rendered?.Dispose();
    }
}
