using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>The type of a card (spec §5.1): what happened, and so its colour, its sound and its timer.</summary>
public enum NotificationKind { Finished, Permission, Plan, Question, Error, Notice }

/// <summary>Colour family of the dot and of the label (row 2) of a card.</summary>
public enum NotificationTone { Success, Warning, Question, Danger, Neutral }

/// <summary>Severity of an app notice, the Core twin of the App's <c>NoticeKind</c>.</summary>
public enum NoticeSeverity { Info, Warning, Error }

/// <summary>What a click on the body of a card does.</summary>
public enum NotificationAction
{
    /// <summary>Brings the session to the front: its terminal, its cloud page or the desktop app.</summary>
    FocusSession,

    /// <summary>Opens the update confirmation.</summary>
    OpenUpdate,

    /// <summary>Pins the notch open.</summary>
    PinNotch
}

/// <summary>
/// The sound of a card (spec §8), declared from the weakest to the strongest: when one step would play two, the larger
/// value wins, so <see cref="Attention"/> outranks <see cref="Done"/>.
/// </summary>
public enum NotificationSoundKind
{
    /// <summary>Silent: app notices, and every card when the "Suono" switch is off.</summary>
    None,

    /// <summary>The soft sound of a finished turn (done.wav).</summary>
    Done,

    /// <summary>The marked sound of a permission, a plan, a question or an error (attention.wav).</summary>
    Attention
}

/// <summary>One card of the notification stack.</summary>
/// <param name="Key">
/// The slot of the card on the board: "session:&lt;agent key&gt;:&lt;session id&gt;" (one card per session, replaced in
/// place by its next state) or "notice:&lt;title&gt;|&lt;text&gt;" (one card per distinct app notice).
/// </param>
/// <param name="Agent">The agent of a session card; null for an app notice.</param>
/// <param name="Title">Row 1: the session name, after where it runs when that is not a terminal.</param>
/// <param name="Label">Row 2, next to the coloured dot: "Permesso · Bash", "Finito · 4m 12s".</param>
/// <param name="Message">Row 3: the command, the question, the last message of the turn.</param>
/// <param name="StateKey">
/// Dedupe key: "&lt;phase&gt;|&lt;label&gt;|&lt;message&gt;" for a session card, <see cref="Key"/> for a notice. A session card
/// closed with ✕ or expired does not come back with the same StateKey until its session changes state (spec §5.3).
/// </param>
/// <param name="AutoClose">How long the card stays on screen (8 s Finished, 6 s Info notice); null = until it is closed or retired.</param>
/// <param name="Sound">
/// What showing this card, or replacing another with it, plays: <see cref="NotificationSoundKind.Done"/> for a finished
/// turn, <see cref="NotificationSoundKind.Attention"/> for a permission, a plan, a question or an error,
/// <see cref="NotificationSoundKind.None"/> for a notice or with the sound switched off.
/// </param>
/// <param name="Session">The session state behind a session card, used by the click; null for a notice.</param>
public sealed record NotificationCard(
    string Key,
    NotificationKind Kind,
    NotificationTone Tone,
    AgentKind? Agent,
    string Title,
    string Label,
    string Message,
    string StateKey,
    TimeSpan? AutoClose,
    NotificationSoundKind Sound,
    NotificationAction Action,
    SessionState? Session = null);

/// <summary>What the composer asks of the board.</summary>
public abstract record NotificationIntent;

/// <summary>Show the card, or replace the content of the card that has the same Key.</summary>
public sealed record ShowIntent(NotificationCard Card) : NotificationIntent;

/// <summary>The state behind the card is over: remove it and forget a state of that Key closed with ✕.</summary>
public sealed record RetireIntent(string Key) : NotificationIntent;

/// <summary>A card on the board, as the stack draws it.</summary>
/// <param name="ShownAt">When the card arrived or last changed content (its age, its place in the stack).</param>
/// <param name="ExpiresAt">
/// When the running timer of a visible auto-close card fires; null for a persistent card, for one hidden or queued (its
/// timer starts in full when it shows) and while the mouse freezes the timers.
/// </param>
/// <param name="Remaining">Time left on a timer frozen by the mouse over the stack; null otherwise.</param>
public sealed record BoardEntry(NotificationCard Card, DateTimeOffset ShownAt, DateTimeOffset? ExpiresAt, TimeSpan? Remaining);

/// <summary>
/// Outcome of a board operation: whether the stack must be redrawn, and the sound to play
/// (<see cref="NotificationSoundKind.None"/> for none; always none while quiet).
/// </summary>
public readonly record struct BoardResult(bool Changed, NotificationSoundKind Sound);
