using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>
/// The sample cards of <c>--test-notification</c> (spec 2026-09-27 §9), built from synthetic sessions only: invented
/// names and ids, no cwd, transcript or host (a click never reaches a real terminal) and never a cloud origin (a click
/// never opens a browser). Two waves, because only three cards are visible at once and cards that stay open come
/// first: the first wave shows the auto-closing "Finito" next to a permission, the second a question and an error.
/// Inside a wave the cards come <see cref="CardInterval"/> apart, so both app sounds can be heard: "Finito" plays the
/// soft one, the others the stronger one.
/// </summary>
public static class NotificationDemo
{
    /// <summary>
    /// Delay of the second wave: the first wave's Finished (its first card, 8 s) and Info notice (6 s), both shown at
    /// once, have closed by then.
    /// </summary>
    public static readonly TimeSpan SecondWaveDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Time between two cards of a wave: longer than the longest app sound (done.wav, 520 ms), because a new sound cuts
    /// the one playing.
    /// </summary>
    public static readonly TimeSpan CardInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>The demo quits after this long even if some cards are still open.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(60);

    /// <summary>Length of the sample finished turn: its card reads "Finito · 4m 12s".</summary>
    public static readonly TimeSpan TurnDuration = new(0, 4, 12);

    /// <summary>The app notice shown with the first wave (Info: closes by itself).</summary>
    public const string NoticeTitle = "AIUsageMonitor · prova";
    public const string NoticeText = "Card di esempio: clic per andare alla sessione, ✕ per chiudere.";

    /// <summary>What Claude Code's permission_prompt always says: the card must show the detail instead.</summary>
    private const string GenericPermissionMessage = "Claude needs your permission";

    /// <summary>A finished turn and a Bash permission, both from Claude Code sessions in a terminal.</summary>
    public static IReadOnlyList<SessionChange> FirstWave(DateTimeOffset now) =>
    [
        Change(Session(AgentKind.Claude, "demo-finished", "webshop", SessionPhase.Idle, now,
            message: "Ho aggiornato il carrello e i test passano.", turnStartedAt: now - TurnDuration)),
        Change(Session(AgentKind.Claude, "demo-permission", "api-gateway", SessionPhase.NeedsInput, now,
            message: GenericPermissionMessage,
            attention: new AttentionDetail(AttentionKind.Permission, "Bash", "git push origin main")))
    ];

    /// <summary>A question from a Claude desktop app session and an API error from a Codex session.</summary>
    public static IReadOnlyList<SessionChange> SecondWave(DateTimeOffset now) =>
    [
        Change(Session(AgentKind.Claude, "demo-question", "Presentazione Q3", SessionPhase.NeedsInput, now,
            message: GenericPermissionMessage,
            attention: new AttentionDetail(AttentionKind.Question, Summary: "Quale database usiamo per i test di integrazione?"),
            origin: SessionOrigin.App)),
        Change(Session(AgentKind.Codex, "demo-error", "billing-service", SessionPhase.Error, now,
            message: "API Error: 529 overloaded"))
    ];

    /// <summary>Every sample leaves a working turn, as the live transitions do.</summary>
    private static SessionChange Change(SessionState session) =>
        new(SessionChangeKind.Updated, session, SessionPhase.Working);

    private static SessionState Session(AgentKind agent, string sessionId, string name, SessionPhase phase, DateTimeOffset now,
        string? message = null, AttentionDetail? attention = null, DateTimeOffset? turnStartedAt = null,
        SessionOrigin origin = SessionOrigin.Terminal) =>
        new(
            Agent: agent,
            SessionId: sessionId,
            DisplayName: name,
            Cwd: null,
            Phase: phase,
            Message: message,
            LastEventAt: now,
            StartedAt: now - TimeSpan.FromMinutes(30),
            Origin: origin,
            WaitingSince: phase == SessionPhase.NeedsInput ? now : null,
            Attention: attention,
            TurnStartedAt: turnStartedAt);
}
