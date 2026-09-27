using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>
/// The session a click on a session card acts on (spec 2026-09-27 §5.3). The board keeps a card, and the
/// <see cref="NotificationCard.Session"/> it was composed with, for as long as its state key does not change, so that
/// snapshot can be older than the session: renamed, bound to a new terminal host. The click takes the current state of
/// the session the card is for, found by the card's key (agent and session id), and falls back on the snapshot only
/// when the session is gone.
/// </summary>
public static class SessionCardTarget
{
    /// <summary>The current state of the card's session, else the card's snapshot; null for an app notice.</summary>
    public static SessionState? Resolve(NotificationCard card, IEnumerable<SessionState> sessions)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(sessions);
        foreach (var session in sessions)
            if (string.Equals(NotificationComposer.SessionKey(session.Agent, session.SessionId), card.Key, StringComparison.Ordinal))
                return session;
        return card.Session;
    }
}
