using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Settings;
using WinForms = System.Windows.Forms;

namespace AIUsageMonitor.App.Notifications;

/// <summary>Turns session transitions into Windows toasts, honoring the per-event and per-agent settings.</summary>
public sealed class ToastService
{
    private static readonly TimeSpan MinGapPerAgent = TimeSpan.FromSeconds(3);
    private const int MaxTrackedSessions = 1000;

    private readonly AppServices _services;
    private readonly Action<string, string, WinForms.ToolTipIcon> _show;
    // Last observed state per session (phase + message) and whether that exact state was already toasted.
    private readonly Dictionary<(AgentKind Agent, string SessionId), (string Key, bool Toasted)> _lastState = new();
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
    /// riemette <c>idle_prompt</c> finche' il prompt resta senza risposta e ogni ripetizione avrebbe un <c>ts</c> nuovo.
    /// Lo stato della sessione viene registrato a OGNI cambiamento (anche quelli che non producono toast, come il
    /// passaggio ad "al lavoro"): cosi' una ripetizione identica dello stesso stato viene soppressa, mentre un nuovo
    /// turno (Working → Idle) che finisce di nuovo con "Turno completato" notifica ancora, perche' nel frattempo lo
    /// stato registrato e' cambiato. Una notifica scartata dalla finestra minima di 3 s resta con Toasted=false e puo'
    /// quindi emergere alla ripetizione successiva.
    /// </summary>
    private void OnSessionChanged(SessionChange change)
    {
        var session = change.Session;
        var id = (session.Agent, session.SessionId);
        if (change.Kind == SessionChangeKind.Removed)
        {
            lock (_gate) _lastState.Remove(id);
            return;
        }

        var settings = _services.Settings.Current;
        var toast = Describe(change, settings);
        var stateKey = $"{session.Phase}|{session.Message}";
        var now = _services.Clock.UtcNow;
        bool show;
        lock (_gate)
        {
            var alreadyToasted = _lastState.TryGetValue(id, out var previous) && previous.Key == stateKey && previous.Toasted;
            var rateLimited = _lastShown.TryGetValue(session.Agent, out var last) && now - last < MinGapPerAgent;
            show = toast is not null && !alreadyToasted && !rateLimited;
            if (_lastState.Count >= MaxTrackedSessions && !_lastState.ContainsKey(id)) _lastState.Clear();
            _lastState[id] = (stateKey, show || alreadyToasted);
            if (show) _lastShown[session.Agent] = now;
        }
        if (!show) return;

        var (title, text, icon) = toast!.Value;
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
