using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;
using AIUsageMonitor.Core.Settings;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>
/// Turns a session change into what the notification board must do (spec §5.1, §5.4): the card of the session's new
/// state, a retire when the state that justified its card is over, or nothing. Also builds the cards of the app's own
/// notices, and gives every card its sound (spec §8).
/// </summary>
public static class NotificationComposer
{
    public static readonly TimeSpan FinishedAutoClose = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan InfoAutoClose = TimeSpan.FromSeconds(6);

    private const string AppName = "AIUsageMonitor";

    /// <summary>"session:claude:&lt;id&gt;": the one slot of a session on the board.</summary>
    public static string SessionKey(AgentKind agent, string sessionId) => $"session:{agent.Key()}:{sessionId}";

    /// <summary>
    /// A wait (permission, plan, question, input) or an error shows its card, and so does a turn that ran from Working to
    /// Idle unless the change is <see cref="SessionChange.Silent"/>; back at work, a wait or an error that ends, a session
    /// that ends, an agent or an event the settings mute retire the card. Any other change (a token update, an Idle
    /// session seen again) leaves the board alone: null.
    /// </summary>
    public static NotificationIntent? Compose(SessionChange change, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(settings);
        var session = change.Session;
        var retire = new RetireIntent(SessionKey(session.Agent, session.SessionId));
        if (change.Kind == SessionChangeKind.Removed || !Notifies(session.Agent, settings)) return retire;

        return session.Phase switch
        {
            SessionPhase.NeedsInput => settings.NotifyNeedsInput ? new ShowIntent(Waiting(session, settings)) : retire,
            SessionPhase.Error => settings.NotifyError ? new ShowIntent(Failed(session, settings)) : retire,
            SessionPhase.Idle when change.PreviousPhase == SessionPhase.Working && !change.Silent =>
                settings.NotifyTurnCompleted ? new ShowIntent(Finished(session, settings)) : retire,
            // A permission denied or an error, then the end of the turn: the card is over, and "Finito" is only for a turn
            // that ran to its end.
            SessionPhase.Idle when change.PreviousPhase is SessionPhase.NeedsInput or SessionPhase.Error => retire,
            SessionPhase.Working => retire,
            _ => null
        };
    }

    /// <summary>
    /// The card of an app notice: one per distinct title and text, labelled with the title under the app's name; an
    /// Info notice closes after <see cref="InfoAutoClose"/>, a warning or an error stays. Notices are always silent.
    /// </summary>
    public static NotificationCard Notice(string title, string text, NoticeSeverity severity, NotificationAction action)
    {
        var key = $"notice:{title}|{text}";
        var tone = severity switch
        {
            NoticeSeverity.Warning => NotificationTone.Warning,
            NoticeSeverity.Error => NotificationTone.Danger,
            _ => NotificationTone.Neutral
        };
        TimeSpan? autoClose = severity == NoticeSeverity.Info ? InfoAutoClose : null;
        return new NotificationCard(key, NotificationKind.Notice, tone, null, AppName, title, text, key, autoClose,
            NotificationSoundKind.None, action);
    }

    /// <summary>Length of a turn: "42s" under a minute, "4m 05s" under an hour, "1h 03m" beyond; a negative span is "0s".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var seconds = duration <= TimeSpan.Zero ? 0L : (long)duration.TotalSeconds;
        if (seconds < 60) return $"{seconds}s";
        if (seconds < 3600) return $"{seconds / 60}m {seconds % 60:00}s";
        return $"{seconds / 3600}h {seconds % 3600 / 60:00}m";
    }

    private static bool Notifies(AgentKind agent, AppSettings settings) => agent switch
    {
        AgentKind.Claude => settings.NotifyClaude,
        AgentKind.Codex => settings.NotifyCodex,
        _ => true
    };

    /// <summary>
    /// What the session waits for, from the detail the pump read in the transcript; without one (no pending tool_use
    /// found) it is a permission. Claude's own text for a permission is always the generic "Claude needs your
    /// permission", so only the detail says what is asked.
    /// </summary>
    private static NotificationCard Waiting(SessionState session, AppSettings settings)
    {
        var detail = session.Attention ?? new AttentionDetail(AttentionKind.Permission);
        var summary = NonBlank(detail.Summary);
        return detail.Kind switch
        {
            AttentionKind.Plan => SessionCard(session, settings, NotificationKind.Plan, NotificationTone.Warning,
                "Piano da approvare", summary ?? "Claude ha preparato un piano", null),
            AttentionKind.Question => SessionCard(session, settings, NotificationKind.Question, NotificationTone.Question,
                "Domanda", summary ?? NonBlank(session.Message) ?? "Claude ti fa una domanda", null),
            AttentionKind.Input => SessionCard(session, settings, NotificationKind.Question, NotificationTone.Question,
                detail.Background ? "Un agente attende input" : "Attende input",
                summary ?? NonBlank(session.Message) ?? "Input richiesto", null),
            _ => SessionCard(session, settings, NotificationKind.Permission, NotificationTone.Warning,
                PermissionLabel(detail), summary ?? "Permesso richiesto", null)
        };
    }

    /// <summary>"Permesso", "Permesso · Bash", "Permesso · agente in background", "Permesso · Bash · agente in background".</summary>
    private static string PermissionLabel(AttentionDetail detail)
    {
        var label = NonBlank(detail.Tool) is { } tool ? $"Permesso · {tool}" : "Permesso";
        return detail.Background ? $"{label} · agente in background" : label;
    }

    private static NotificationCard Failed(SessionState session, AppSettings settings) =>
        SessionCard(session, settings, NotificationKind.Error, NotificationTone.Danger, "Errore",
            NonBlank(session.Message) ?? "Errore API", null);

    /// <summary>
    /// "Finito · 4m 12s" from the start of the turn; just "Finito" when the start is unknown (a session born from the
    /// replay or the registry already at work) or later than the end.
    /// </summary>
    private static NotificationCard Finished(SessionState session, AppSettings settings)
    {
        var duration = session.LastEventAt - session.TurnStartedAt;
        var label = duration is { } turn && turn >= TimeSpan.Zero ? $"Finito · {FormatDuration(turn)}" : "Finito";
        return SessionCard(session, settings, NotificationKind.Finished, NotificationTone.Success, label,
            NonBlank(session.Message) ?? "Turno completato", FinishedAutoClose);
    }

    private static NotificationCard SessionCard(SessionState session, AppSettings settings, NotificationKind kind,
        NotificationTone tone, string label, string message, TimeSpan? autoClose)
    {
        // "cloud · Deploy": where the session runs when it is not a terminal.
        var title = NotchPresentation.OriginLabel(session.Origin) is { } origin ? $"{origin} · {session.DisplayName}" : session.DisplayName;
        return new NotificationCard(SessionKey(session.Agent, session.SessionId), kind, tone, session.Agent, title, label, message,
            $"{session.Phase}|{label}|{message}", autoClose, SoundOf(kind, settings), NotificationAction.FocusSession, session);
    }

    /// <summary>
    /// Spec §8: the soft sound for a finished turn, the marked one for a card that waits for the user or reports an error;
    /// none at all with the "Suono" switch off.
    /// </summary>
    private static NotificationSoundKind SoundOf(NotificationKind kind, AppSettings settings) =>
        !settings.NotifySound ? NotificationSoundKind.None
        : kind == NotificationKind.Finished ? NotificationSoundKind.Done
        : NotificationSoundKind.Attention;

    private static string? NonBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text;
}
