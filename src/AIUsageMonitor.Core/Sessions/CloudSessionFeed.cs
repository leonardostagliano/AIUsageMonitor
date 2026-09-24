using System.Collections.Concurrent;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Sessions;

/// <summary>
/// Turns the successive reads of the cloud sessions into the events the tracker understands: SessionStart when one
/// shows up (with its title and origin), UserPromptSubmit when it starts working, a Notification when it waits for
/// the user, Stop when a turn ends, StopFailure when it fails, SessionEnd when it is archived, falls out of the list or
/// has been quiet for longer than <see cref="IdleWindow"/>. It also keeps the usage each session reports, which the
/// token source hands to the tracker like a transcript total.
/// </summary>
/// <remarks>
/// A cloud session is shown while it works or waits (for at most <see cref="ActiveWindow"/> without news, a backstop
/// against one that stopped reporting) and for <see cref="IdleWindow"/> after its last activity, long enough to see
/// it finish: the sessions API keeps finished sessions for weeks, and the notch is about what is going on now.
/// <para><see cref="Diff"/> and <see cref="Clear"/> are called by one poller at a time; <see cref="UsageOf"/> and
/// <see cref="ModelOf"/> may be read from the pump thread meanwhile.</para>
/// </remarks>
public sealed class CloudSessionFeed
{
    private readonly Dictionary<string, (string Id, CloudSessionStatus Status, SessionOrigin Origin, string? Title)> _shown = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (CloudUsage? Usage, string? Model)> _usage = new(StringComparer.Ordinal);

    public TimeSpan IdleWindow { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan ActiveWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Whether any cloud session is on show (the poller clears them when the option goes off).</summary>
    public bool HasSessions => _shown.Count > 0;

    public CloudUsage? UsageOf(string sessionId) => _usage.TryGetValue(CloudSession.KeyOf(sessionId), out var u) ? u.Usage : null;

    public string? ModelOf(string sessionId) => _usage.TryGetValue(CloudSession.KeyOf(sessionId), out var u) ? u.Model : null;

    /// <param name="routinesKnown">False when the routines could not be read: the runs on show are then left alone.</param>
    public IReadOnlyList<HookEvent> Diff(IReadOnlyList<CloudSession> sessions, bool routinesKnown, DateTimeOffset now)
    {
        var events = new List<HookEvent>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in sessions.OrderBy(s => s.LastActivity))
        {
            var key = CloudSession.KeyOf(session.Id);
            if (!seen.Add(key) || !Visible(session, now)) continue;
            _usage[key] = (session.Usage, session.Model);

            // Timestamps come from the session, never later than now: "finito · 3h" must say when it finished.
            var at = session.LastActivity > now ? now : session.LastActivity;
            if (!_shown.TryGetValue(key, out var shown))
            {
                events.Add(Event("SessionStart", session.Id, session, at));
                // A cloud session always ran a turn: idle means that turn is over ("finito"), not a fresh prompt.
                if (Phase(session.Id, session, at) is { } first) events.Add(first);
                _shown[key] = (session.Id, session.Status, session.Origin, session.Title);
                continue;
            }
            // The tracker knows the session under the id it first had ("session_" and "cse_" name the same one).
            if (shown.Status != session.Status && Phase(shown.Id, session, at) is { } change) events.Add(change);
            // A new title alone (the first turn names an "Untitled" session): SessionStart renames without a phase change.
            else if (shown.Title != session.Title && !string.IsNullOrWhiteSpace(session.Title)) events.Add(Event("SessionStart", shown.Id, session, at));
            _shown[key] = (shown.Id, session.Status, session.Origin, session.Title);
        }

        foreach (var (key, shown) in _shown.ToList())
        {
            if (seen.Contains(key)) continue;
            if (shown.Origin == SessionOrigin.Routine && !routinesKnown) continue;
            events.Add(End(shown.Id, now));
            _shown.Remove(key);
            _usage.TryRemove(key, out _);
        }
        return events;
    }

    /// <summary>Ends every session on show (the option went off, or Claude was disabled).</summary>
    public IReadOnlyList<HookEvent> Clear(DateTimeOffset now)
    {
        var events = _shown.Values.Select(s => End(s.Id, now)).ToList();
        _shown.Clear();
        _usage.Clear();
        return events;
    }

    private bool Visible(CloudSession session, DateTimeOffset now)
    {
        if (session.Status == CloudSessionStatus.Archived) return false;
        var quiet = now - session.LastActivity;
        return session.Status is CloudSessionStatus.Working or CloudSessionStatus.NeedsInput ? quiet <= ActiveWindow : quiet <= IdleWindow;
    }

    private static HookEvent? Phase(string id, CloudSession session, DateTimeOffset at) => session.Status switch
    {
        CloudSessionStatus.Working => Event("UserPromptSubmit", id, session, at),
        CloudSessionStatus.NeedsInput => Event("Notification", id, session, at) with { NotificationType = "permission_prompt" },
        CloudSessionStatus.Failed => Event("StopFailure", id, session, at),
        CloudSessionStatus.Idle => Event("Stop", id, session, at),
        _ => null
    };

    /// <summary>Every event carries origin and title: a new cloud session is "Untitled" until its first turn names it.</summary>
    private static HookEvent Event(string name, string id, CloudSession session, DateTimeOffset at) =>
        new(at, AgentKind.Claude, name, id, null, null, session.Message, "cloud", Origin: session.Origin, Title: session.Title);

    private static HookEvent End(string id, DateTimeOffset now) => new(now, AgentKind.Claude, "SessionEnd", id, null, null, null, "cloud");
}
