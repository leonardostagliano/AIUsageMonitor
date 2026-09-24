using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Sessions;

/// <summary>A live registry record: the session, its process and that process's creation time (FILETIME UTC) when known.</summary>
public sealed record LiveClaudeSession(string SessionId, int Pid, long? StartedAtFileTime, ClaudeSessionRecord Record);

/// <summary>
/// What one sync found: the events to apply, every live Claude Code session of the machine, and the sessions named only
/// by records their killed process left behind (<paramref name="Stale"/>, never one that is also live).
/// </summary>
public sealed record ClaudeRegistrySync(IReadOnlyList<HookEvent> Events, IReadOnlyList<LiveClaudeSession> Live, IReadOnlyList<string> Stale);

/// <summary>
/// Brings in the Claude Code sessions that no hook reports — the ones the desktop app starts through the SDK, which may
/// not load the user's hooks, or any session while the hooks are not installed — from the registry every Claude Code
/// process keeps in <c>~/.claude/sessions</c>, and turns the changes of their <c>status</c> into the same events the
/// hooks would have written: SessionStart when one appears, UserPromptSubmit on busy, a permission Notification on
/// waiting, Stop when a turn goes back to idle, SessionEnd when its record or its process goes away.
/// </summary>
/// <remarks>
/// A session any hook event has spoken for is left to the hooks: they carry more (messages, subagents, the host), and
/// two producers would toast twice. A new record is adopted only after <see cref="AdoptAfter"/>, so a CLI session gets
/// the time to send its own SessionStart first. A record whose pid is dead, or was recycled (the live process with that
/// pid was created after the record was written), is stale and ignored.
/// <para>Not thread-safe: the pump calls it on its own thread.</para>
/// </remarks>
public sealed class ClaudeRegistrySessionFeed
{
    /// <summary>FILETIME of 1970-01-01T00:00:00Z.</summary>
    private const long UnixEpochFileTime = 116_444_736_000_000_000;

    /// <summary>A process registers a moment after it starts; more than this after its record was written, it is another process.</summary>
    public static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(5);

    private readonly ClaudeSessionRegistryReader _reader;
    private readonly IProcessProbe _probe;
    private readonly IClock _clock;
    private readonly ClaudeTranscriptFinder? _transcripts;
    // Sessions this feed drives, with the status last turned into an event.
    private readonly Dictionary<string, string> _owned = new(StringComparer.Ordinal);

    public ClaudeRegistrySessionFeed(ClaudeSessionRegistryReader reader, IProcessProbe probe, IClock clock, ClaudeTranscriptFinder? transcripts = null)
    {
        _reader = reader;
        _probe = probe;
        _clock = clock;
        _transcripts = transcripts;
    }

    /// <summary>How old a record must be before a session no hook reported is adopted.</summary>
    public TimeSpan AdoptAfter { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Reads the registry and returns the events for the sessions this feed drives, plus every live session so the
    /// caller can tie the tracked ones to their process. <paramref name="hookOwned"/> tells which session ids the
    /// hooks have reported.
    /// </summary>
    public ClaudeRegistrySync Sync(IReadOnlyCollection<SessionState> sessions, Func<string, bool> hookOwned)
    {
        var now = _clock.UtcNow;
        var tracked = sessions.Where(s => s.Agent == AgentKind.Claude).Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);

        var live = new Dictionary<string, LiveClaudeSession>(StringComparer.Ordinal);
        var stale = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _reader.Read())
        {
            if (!record.IsConversation) continue;
            var seen = _probe.Query(record.Pid);
            if (seen.State == ProcessState.Dead
                || seen.StartedAtFileTime is { } created && record.StartedAt > 0
                   && ToUnixMs(created) > record.StartedAt + (long)StartSlack.TotalMilliseconds)
            {
                stale.Add(record.SessionId!);
                continue;
            }
            // A resumed session can be named by two records for a moment: the newer process is the one that holds it.
            if (live.TryGetValue(record.SessionId!, out var other) && other.Record.StartedAt >= record.StartedAt) continue;
            live[record.SessionId!] = new LiveClaudeSession(record.SessionId!, record.Pid, seen.StartedAtFileTime, record);
        }

        var events = new List<HookEvent>();
        foreach (var (sessionId, session) in live)
        {
            if (hookOwned(sessionId))
            {
                _owned.Remove(sessionId);
                continue;
            }
            var record = session.Record;
            var status = Normalize(record.Status);
            if (_owned.TryGetValue(sessionId, out var last) && tracked.Contains(sessionId))
            {
                if (last == status) continue;
                if (Transition(record, status, last, now) is { } change) events.Add(change);
                _owned[sessionId] = status;
                continue;
            }
            // A session tracked from another source is not taken over.
            if (tracked.Contains(sessionId)) continue;
            if (record.StartedAt > 0 && now - DateTimeOffset.FromUnixTimeMilliseconds(record.StartedAt) < AdoptAfter) continue;

            events.Add(Start(record, now));
            if (Transition(record, status, null, now) is { } phase) events.Add(phase);
            _owned[sessionId] = status;
        }

        foreach (var sessionId in _owned.Keys.Where(id => !live.ContainsKey(id)).ToList())
        {
            _owned.Remove(sessionId);
            if (tracked.Contains(sessionId))
                events.Add(new HookEvent(now, AgentKind.Claude, "SessionEnd", sessionId, null, null, null, "registry"));
        }

        stale.ExceptWith(live.Keys);
        return new ClaudeRegistrySync(events, live.Values.ToList(), stale.ToList());
    }

    private HookEvent Start(ClaudeSessionRecord record, DateTimeOffset now) =>
        new(now, AgentKind.Claude, "SessionStart", record.SessionId!, record.Cwd, null, null, "registry",
            TranscriptPath: _transcripts?.Find(record.Cwd, record.SessionId!),
            // The record's pid is the agent itself: walking up from it finds the terminal or the desktop app window.
            Host: new HostInfo(record.Pid, null, null, null, null, Entrypoint: record.Entrypoint),
            Origin: HostInfo.OriginOf(record.Entrypoint) ?? SessionOrigin.Terminal);

    /// <summary>The event that moves a session to <paramref name="status"/>; null when there is nothing to say (idle at adoption).</summary>
    private HookEvent? Transition(ClaudeSessionRecord record, string status, string? previous, DateTimeOffset now)
    {
        var transcript = _transcripts?.Find(record.Cwd, record.SessionId!);
        return status switch
        {
            "busy" or "shell" => Event("UserPromptSubmit"),
            "waiting" => Event("Notification", "permission_prompt", WaitingText(record.WaitingFor)),
            // Idle after a turn is the end of that turn; idle at adoption is just a session ready for a prompt.
            _ when previous is not null && previous != "idle" => Event("Stop"),
            _ => null
        };

        HookEvent Event(string name, string? notificationType = null, string? message = null) =>
            new(now, AgentKind.Claude, name, record.SessionId!, record.Cwd, notificationType, message, "registry", TranscriptPath: transcript);
    }

    private static string Normalize(string? status) => status is "busy" or "shell" or "waiting" ? status : "idle";

    private static string WaitingText(string? waitingFor) => waitingFor switch
    {
        "permission prompt" => "Permesso richiesto",
        _ => "Input richiesto"
    };

    private static long ToUnixMs(long fileTime) => (fileTime - UnixEpochFileTime) / TimeSpan.TicksPerMillisecond;
}
