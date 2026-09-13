using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Settings;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App.Notifications;

/// <summary>Turns session transitions into Windows toasts (balloon tips), honoring the per-event and per-agent settings.</summary>
public sealed class ToastService
{
    private static readonly TimeSpan MinGapPerAgent = TimeSpan.FromSeconds(3);

    private readonly AppServices _services;
    private readonly Action<string, string, WinForms.ToolTipIcon> _show;
    private readonly HashSet<string> _seen = new();
    private readonly Dictionary<AgentKind, DateTimeOffset> _lastShown = new();
    private readonly object _gate = new();

    public ToastService(AppServices services, Action<string, string, WinForms.ToolTipIcon> show)
    {
        _services = services;
        _show = show;
        services.Sessions.Changed += OnSessionChanged;
    }

    private void OnSessionChanged(SessionChange change)
    {
        var settings = _services.Settings.Current;
        var toast = Describe(change, settings);
        if (toast is null) return;

        var key = $"{change.Session.Agent}|{change.Session.SessionId}|{change.Session.Phase}|{change.Session.LastEventAt:O}";
        var now = _services.Clock.UtcNow;
        lock (_gate)
        {
            if (!_seen.Add(key)) return;
            if (_seen.Count > 1000) _seen.Clear();
            if (_lastShown.TryGetValue(change.Session.Agent, out var last) && now - last < MinGapPerAgent) return;
            _lastShown[change.Session.Agent] = now;
        }

        var (title, text, icon) = toast.Value;
        _show(title, text, icon);
    }

    public static (string Title, string Text, WinForms.ToolTipIcon Icon)? Describe(SessionChange change, AppSettings settings)
    {
        if (change.Kind == SessionChangeKind.Removed) return null;
        var session = change.Session;
        if (session.Agent == AgentKind.Claude && !settings.NotifyClaude) return null;
        if (session.Agent == AgentKind.Codex && !settings.NotifyCodex) return null;

        var title = $"{session.Agent.DisplayName()} · {session.DisplayName}";
        return session.Phase switch
        {
            SessionPhase.NeedsInput when settings.NotifyNeedsInput =>
                (title, session.Message ?? "Input richiesto", WinForms.ToolTipIcon.Warning),
            SessionPhase.Idle when settings.NotifyTurnCompleted && change.PreviousPhase == SessionPhase.Working =>
                (title, session.Message ?? "Turno completato", WinForms.ToolTipIcon.Info),
            SessionPhase.Error when settings.NotifyError =>
                (title, session.Message ?? "Errore API", WinForms.ToolTipIcon.Error),
            _ => null
        };
    }
}
