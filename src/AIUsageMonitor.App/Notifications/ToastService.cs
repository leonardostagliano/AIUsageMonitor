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
    private const int MaxTrackedSessions = 1000;

    private readonly AppServices _services;
    private readonly Action<string, string, WinForms.ToolTipIcon> _show;
    private readonly Dictionary<(AgentKind Agent, string SessionId), string> _lastKey = new();
    private readonly Dictionary<AgentKind, DateTimeOffset> _lastShown = new();
    private readonly object _gate = new();

    public ToastService(AppServices services, Action<string, string, WinForms.ToolTipIcon> show)
    {
        _services = services;
        _show = show;
        services.Sessions.Changed += OnSessionChanged;
    }

    /// <summary>
    /// Dedupe come da spec 9: la chiave e' (sessione, fase, messaggio), senza il timestamp dell'evento — Claude Code
    /// riemette <c>idle_prompt</c> finche' il prompt resta senza risposta e ogni ripetizione avrebbe un <c>ts</c> nuovo,
    /// quindi con il timestamp nella chiave la stessa toast tornava a ogni riemissione. La chiave viene confrontata con
    /// l'ultima toast mostrata per quella sessione (non con uno storico globale): cosi' una ripetizione identica viene
    /// soppressa, ma un nuovo turno che finisce di nuovo con lo stesso messaggio ("Turno completato") notifica ancora.
    /// Ordine delle guardie: la chiave viene registrata solo dopo che la finestra minima di 3 s ha lasciato passare la
    /// toast, altrimenti una notifica scartata dal rate limit resterebbe soppressa per sempre.
    /// </summary>
    private void OnSessionChanged(SessionChange change)
    {
        var session = change.Session;
        var id = (session.Agent, session.SessionId);
        if (change.Kind == SessionChangeKind.Removed)
        {
            lock (_gate) _lastKey.Remove(id);
            return;
        }

        var settings = _services.Settings.Current;
        var toast = Describe(change, settings);
        if (toast is null) return;

        var key = $"{session.Phase}|{toast.Value.Text}";
        var now = _services.Clock.UtcNow;
        lock (_gate)
        {
            if (_lastKey.TryGetValue(id, out var previous) && previous == key) return;
            if (_lastShown.TryGetValue(session.Agent, out var last) && now - last < MinGapPerAgent) return;
            if (_lastKey.Count >= MaxTrackedSessions) _lastKey.Clear();
            _lastKey[id] = key;
            _lastShown[session.Agent] = now;
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
