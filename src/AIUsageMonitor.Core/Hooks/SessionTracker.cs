using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

public enum SessionChangeKind { Added, Updated, Removed }

public sealed record SessionChange(SessionChangeKind Kind, SessionState Session, SessionPhase? PreviousPhase);

/// <summary>State machine per (agent, session id). Thread-safe; Changed fires outside the lock on the caller's thread.</summary>
public sealed class SessionTracker
{
    public const int MaxMessageLength = 120;

    /// <summary>Finished subagents kept per session; older ones are dropped so a long session cannot grow without bound.</summary>
    public const int MaxDoneSubagents = 50;

    private static readonly HashSet<string> NeedsInputNotifications = new(StringComparer.OrdinalIgnoreCase)
    {
        "permission_prompt", "idle_prompt", "agent_needs_input", "elicitation_dialog", "elicitation_url_dialog"
    };

    private readonly IClock _clock;
    private readonly Func<AgentKind, string, string?> _cwdResolver;
    private readonly Dictionary<(AgentKind Agent, string SessionId), SessionState> _sessions = new();
    private readonly object _gate = new();

    public event Action<SessionChange>? Changed;

    /// <summary>Where a throwing Changed subscriber or cwd resolver is reported (the App wires a FileLogger); never rethrown.</summary>
    public Action<Exception>? OnError { get; init; }

    public SessionTracker(IClock clock, Func<AgentKind, string, string?>? cwdResolver = null)
    {
        _clock = clock;
        _cwdResolver = cwdResolver ?? ((_, _) => null);
    }

    public IReadOnlyList<SessionState> Sessions
    {
        get
        {
            lock (_gate)
                return _sessions.Values.OrderBy(s => s.Agent).ThenByDescending(s => s.LastEventAt).ToList();
        }
    }

    public SessionChange? Apply(HookEvent e)
    {
        SessionChange? change;
        lock (_gate) change = ApplyCore(e);
        if (change is not null) Raise(change);
        return change;
    }

    private SessionChange? ApplyCore(HookEvent e)
    {
        var key = (e.Agent, e.SessionId);
        _sessions.TryGetValue(key, out var existing);

        if (e.Event == "SessionEnd")
        {
            if (existing is null) return null;
            _sessions.Remove(key);
            return new SessionChange(SessionChangeKind.Removed, existing, existing.Phase);
        }

        if (e.Event is "SubagentStart" or "SubagentStop")
            return ApplySubagentEvent(e, key, existing);

        SessionPhase? phase = e.Event switch
        {
            "SessionStart" => existing?.Phase ?? SessionPhase.Idle,
            "UserPromptSubmit" => SessionPhase.Working,
            "PostToolUse" => SessionPhase.Working,
            "Stop" => SessionPhase.Idle,
            "StopFailure" => SessionPhase.Error,
            "Notification" => e.NotificationType is not null && NeedsInputNotifications.Contains(e.NotificationType) ? SessionPhase.NeedsInput : null,
            _ => null
        };
        if (phase is null) return null;

        string? message = e.Event switch
        {
            "SessionStart" => existing?.Message,
            "UserPromptSubmit" => null,
            "PostToolUse" => null,
            "Stop" => string.IsNullOrWhiteSpace(e.Message) ? "Turno completato" : Truncate(e.Message),
            "StopFailure" => string.IsNullOrWhiteSpace(e.Message) ? "Errore API" : Truncate(e.Message),
            "Notification" => string.IsNullOrWhiteSpace(e.Message) ? null : Truncate(e.Message),
            _ => existing?.Message
        };

        // A Stop that lands while background subagents are still running keeps the session Working and
        // remembers its message: the Idle transition (and the "finito" toast) waits for the last SubagentStop.
        var awaiting = existing?.AwaitingSubagents ?? false;
        if (e.Event == "UserPromptSubmit") awaiting = false;
        if (e.Event == "Stop" && existing is { ActiveSubagents: > 0 })
        {
            phase = SessionPhase.Working;
            awaiting = true;
        }

        var cwd = e.Cwd ?? existing?.Cwd ?? ResolveCwd(e.Agent, e.SessionId);
        // Every Claude hook payload carries the session transcript; an event without one (Codex, an older hook)
        // must not clear the path the token counter is already reading.
        var transcriptPath = e.TranscriptPath ?? existing?.TranscriptPath;
        // A `with` update on the existing record so state this state machine does not own (Tokens, Subagents,
        // and anything added later) survives every subsequent event instead of being silently reset by a
        // positional rebuild.
        var host = e.Host ?? existing?.Host;
        var updated = existing is null
            ? new SessionState(
                e.Agent, e.SessionId, DisplayNameFor(cwd, e.SessionId), cwd,
                phase.Value, message, e.Ts, e.Ts, TranscriptPath: transcriptPath, AwaitingSubagents: awaiting, Host: host)
            : existing with
            {
                DisplayName = DisplayNameFor(cwd, e.SessionId),
                Cwd = cwd,
                Phase = phase.Value,
                Message = message,
                LastEventAt = e.Ts,
                TranscriptPath = transcriptPath,
                AwaitingSubagents = awaiting,
                Host = host
            };
        _sessions[key] = updated;
        return new SessionChange(existing is null ? SessionChangeKind.Added : SessionChangeKind.Updated, updated, existing?.Phase);
    }

    /// <summary>
    /// SubagentStart/SubagentStop carry the parent session id plus the agent identity: they keep the per-session
    /// subagent list up to date and defer the Idle transition of a Stop that arrived while agents were still running.
    /// Only <c>agent_transcript_path</c> is taken from them (onto the subagent): whether their <c>transcript_path</c>
    /// is the parent session's file is unverified, so SessionState.TranscriptPath is left to the ordinary events.
    /// </summary>
    private SessionChange ApplySubagentEvent(HookEvent e, (AgentKind Agent, string SessionId) key, SessionState? existing)
    {
        var subagents = UpsertSubagent(existing?.Subagents, e);
        var running = subagents.Count(s => s.Phase == SubagentPhase.Running);
        var phase = existing?.Phase ?? SessionPhase.Idle;
        var awaiting = existing?.AwaitingSubagents ?? false;
        var message = existing?.Message;

        if (e.Event == "SubagentStart" && phase == SessionPhase.Idle)
        {
            // The session is Idle because the turn's Stop was already seen: an agent that starts afterwards
            // (a workflow step, a background task) re-arms the deferred Idle, so its SubagentStop takes the
            // session back to Idle instead of pinning it to "al lavoro" until the 12 h stale removal.
            phase = SessionPhase.Working;
            awaiting = true;
        }
        if (e.Event == "SubagentStop" && running == 0 && awaiting)
        {
            // The deferred Stop is spent as soon as the last agent finishes, exactly like the timeout sweep:
            // keeping the flag with no running agent left would latch it forever (the sweep only visits
            // sessions whose counter is above zero) and fake a "finito" at the end of a later agent.
            awaiting = false;
            // Only a Working session goes Idle here: an error or a pending input that arrived while the agents
            // were still running must survive the last SubagentStop.
            if (phase == SessionPhase.Working)
            {
                phase = SessionPhase.Idle;
                message ??= "Turno completato";
            }
        }

        var cwd = e.Cwd ?? existing?.Cwd ?? ResolveCwd(e.Agent, e.SessionId);
        var host = e.Host ?? existing?.Host;
        var updated = existing is null
            ? new SessionState(
                e.Agent, e.SessionId, DisplayNameFor(cwd, e.SessionId), cwd,
                phase, message, e.Ts, e.Ts,
                Subagents: subagents, AwaitingSubagents: awaiting, LastSubagentEventAt: e.Ts, Host: host)
            : existing with
            {
                DisplayName = DisplayNameFor(cwd, e.SessionId),
                Cwd = cwd,
                Phase = phase,
                Message = message,
                LastEventAt = e.Ts,
                Subagents = subagents,
                AwaitingSubagents = awaiting,
                LastSubagentEventAt = e.Ts,
                Host = host
            };
        _sessions[key] = updated;
        return new SessionChange(existing is null ? SessionChangeKind.Added : SessionChangeKind.Updated, updated, existing?.Phase);
    }

    /// <summary>Prefix of the synthetic id given to a subagent event that carries no agent_id.</summary>
    private const string AnonymousIdPrefix = "anon:";

    /// <summary>
    /// Adds or updates the subagent with this agent id (a SubagentStop whose Start was never seen lands as Done).
    /// Events without an agent_id (hook.cjs forwards them with agent_id null, and Codex is unprobed) get one
    /// synthetic id each instead of sharing a single entry: otherwise two concurrent anonymous agents would be
    /// counted as one and the first SubagentStop would declare the turn finished while the other was still running.
    /// An anonymous SubagentStop closes the oldest anonymous agent still running.
    /// </summary>
    private static IReadOnlyList<SubagentState> UpsertSubagent(IReadOnlyList<SubagentState>? current, HookEvent e)
    {
        var started = e.Event == "SubagentStart";
        var list = current is null ? [] : new List<SubagentState>(current);
        var index = e.AgentId is { } id
            ? list.FindIndex(s => s.AgentId == id)
            : started ? -1 : IndexOfOldestRunningAnonymous(list);
        if (index >= 0)
        {
            var known = list[index];
            // agent_transcript_path is what SubagentStop carries (SubagentStart has none): the token counter reads
            // it, so it is kept once known and never cleared by a later event that omits it.
            list[index] = started
                // A SubagentStart for an agent that is already Running is a refresh (the Codex rollout fallback
                // re-announces a long-lived child), not a new run: its StartedAt must not jump forward.
                ? known with
                {
                    AgentType = e.AgentType ?? known.AgentType,
                    Phase = SubagentPhase.Running,
                    StartedAt = known.Phase == SubagentPhase.Running ? known.StartedAt : e.Ts,
                    EndedAt = null,
                    TranscriptPath = e.AgentTranscriptPath ?? known.TranscriptPath
                }
                : known with
                {
                    AgentType = e.AgentType ?? known.AgentType,
                    Phase = SubagentPhase.Done,
                    EndedAt = e.Ts,
                    TranscriptPath = e.AgentTranscriptPath ?? known.TranscriptPath
                };
        }
        else
        {
            list.Add(new SubagentState(
                e.AgentId ?? NextAnonymousId(list, e.Ts), e.AgentType, started ? SubagentPhase.Running : SubagentPhase.Done,
                e.Ts, started ? null : e.Ts, e.AgentTranscriptPath, TokenUsage.Zero));
        }
        return TrimDone(list);
    }

    /// <summary>Index of the anonymous subagent that has been running longest, or -1 when there is none.</summary>
    private static int IndexOfOldestRunningAnonymous(List<SubagentState> list)
    {
        var index = -1;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Phase != SubagentPhase.Running || !list[i].AgentId.StartsWith(AnonymousIdPrefix, StringComparison.Ordinal)) continue;
            if (index < 0 || list[i].StartedAt < list[index].StartedAt) index = i;
        }
        return index;
    }

    /// <summary>A synthetic id for an agent_id-less subagent, unique within the session's list.</summary>
    private static string NextAnonymousId(List<SubagentState> list, DateTimeOffset ts)
    {
        var baseId = AnonymousIdPrefix + ts.UtcTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var id = baseId;
        for (var n = 1; list.Any(s => s.AgentId == id); n++) id = $"{baseId}#{n}";
        return id;
    }

    /// <summary>Keeps at most MaxDoneSubagents finished subagents, dropping the ones that finished first.</summary>
    private static List<SubagentState> TrimDone(List<SubagentState> list)
    {
        var excess = list.Count(s => s.Phase == SubagentPhase.Done) - MaxDoneSubagents;
        if (excess <= 0) return list;
        foreach (var oldest in list.Where(s => s.Phase == SubagentPhase.Done)
                                   .OrderBy(s => s.EndedAt ?? s.StartedAt)
                                   .Take(excess)
                                   .ToList())
            list.Remove(oldest);
        return list;
    }

    /// <summary>
    /// Stores the token totals read from the transcripts (or the Codex rollouts) for a session and, by agent id, for
    /// its subagents. A null total means "unknown right now" and keeps the value already stored: a transcript that
    /// could not be read must never blank a row. Changed fires once, as <see cref="SessionChangeKind.Updated"/> with
    /// the current phase as the previous one, only when at least one total really moved — the counters run every few
    /// seconds and an unconditional event would repaint the notch (and re-evaluate the toasts) for nothing.
    /// Returns null when nothing changed or the session is unknown.
    /// </summary>
    public SessionChange? UpdateTokens(AgentKind agent, string sessionId, TokenUsage? sessionTokens, IReadOnlyDictionary<string, TokenUsage>? subagentTokens)
    {
        SessionChange? change;
        lock (_gate) change = UpdateTokensCore(agent, sessionId, sessionTokens, subagentTokens);
        if (change is not null) Raise(change);
        return change;
    }

    /// <summary>
    /// Stores the totals without raising Changed: the pump's one-shot fill after the startup replay goes through
    /// here, so the restored rows get their token column without the App toasting "Errore API" or "Input richiesto"
    /// for a session whose event history was replayed rather than lived through.
    /// </summary>
    public void UpdateTokensSilently(AgentKind agent, string sessionId, TokenUsage? sessionTokens, IReadOnlyDictionary<string, TokenUsage>? subagentTokens)
    {
        lock (_gate) UpdateTokensCore(agent, sessionId, sessionTokens, subagentTokens);
    }

    private SessionChange? UpdateTokensCore(AgentKind agent, string sessionId, TokenUsage? sessionTokens, IReadOnlyDictionary<string, TokenUsage>? subagentTokens)
    {
        var key = (agent, sessionId);
        if (!_sessions.TryGetValue(key, out var session)) return null;

        var tokens = session.Tokens;
        var changed = false;
        if (sessionTokens is not null && sessionTokens != tokens)
        {
            tokens = sessionTokens;
            changed = true;
        }

        var subagents = session.Subagents;
        if (subagentTokens is { Count: > 0 } && session.Subagents is { Count: > 0 } known)
        {
            List<SubagentState>? updatedList = null;
            for (var i = 0; i < known.Count; i++)
            {
                // Ids the source does not know about keep what they have; ids it reports that the session never saw
                // are ignored (the subagent list is owned by the events, not by the counters).
                if (!subagentTokens.TryGetValue(known[i].AgentId, out var usage) || usage == known[i].Tokens) continue;
                updatedList ??= [.. known];
                updatedList[i] = known[i] with { Tokens = usage };
            }
            if (updatedList is not null)
            {
                subagents = updatedList;
                changed = true;
            }
        }

        if (!changed) return null;

        // LastEventAt is deliberately left alone: a token refresh is not session activity, and pushing it forward
        // would keep a dead session out of the 12 h stale sweep forever.
        var updated = session with { Tokens = tokens, Subagents = subagents };
        _sessions[key] = updated;
        return new SessionChange(SessionChangeKind.Updated, updated, session.Phase);
    }

    /// <summary>
    /// Marks the subagents of every session that has heard nothing from them for <paramref name="timeout"/> as Done,
    /// so a subagent that died without a SubagentStop cannot pin its session to "al lavoro" forever.
    /// </summary>
    public IReadOnlyList<SessionChange> SweepSubagentTimeouts(TimeSpan timeout)
    {
        List<SessionChange> changes;
        lock (_gate) changes = SweepSubagentTimeoutsCore(timeout);
        foreach (var change in changes) Raise(change);
        return changes;
    }

    /// <summary>Sweeps the subagent timeouts without raising Changed (startup replay).</summary>
    public void SweepSubagentTimeoutsSilently(TimeSpan timeout)
    {
        lock (_gate) SweepSubagentTimeoutsCore(timeout);
    }

    private List<SessionChange> SweepSubagentTimeoutsCore(TimeSpan timeout)
    {
        var changes = new List<SessionChange>();
        var now = _clock.UtcNow;
        foreach (var (key, session) in _sessions.ToList())
        {
            if (session.ActiveSubagents == 0 || session.LastSubagentEventAt is not { } last || now - last <= timeout) continue;

            var subagents = TrimDone(session.Subagents!
                .Select(s => s.Phase == SubagentPhase.Running ? s with { Phase = SubagentPhase.Done, EndedAt = now } : s)
                .ToList());
            // Same rule as the last SubagentStop: only a Working session goes Idle, so an error or a pending
            // input that arrived while the agents were running survives the timeout.
            var release = session.AwaitingSubagents && session.Phase == SessionPhase.Working;
            var updated = session with
            {
                Phase = release ? SessionPhase.Idle : session.Phase,
                Message = release ? session.Message ?? "Turno completato" : session.Message,
                Subagents = subagents,
                AwaitingSubagents = false
            };
            _sessions[key] = updated;
            changes.Add(new SessionChange(SessionChangeKind.Updated, updated, session.Phase));
        }
        return changes;
    }

    /// <summary>Applies events without raising Changed (startup replay).</summary>
    public void ApplySilently(IEnumerable<HookEvent> events)
    {
        lock (_gate)
        {
            foreach (var e in events) ApplyCore(e);
        }
    }

    /// <summary>Removes stale sessions without raising Changed (startup replay).</summary>
    public void RemoveStaleSilently(TimeSpan maxAge)
    {
        lock (_gate)
        {
            var cutoff = _clock.UtcNow - maxAge;
            foreach (var key in _sessions.Where(kv => kv.Value.LastEventAt < cutoff).Select(kv => kv.Key).ToList())
                _sessions.Remove(key);
        }
    }

    public IReadOnlyList<SessionChange> RemoveStale(TimeSpan maxAge)
    {
        var removed = new List<SessionChange>();
        lock (_gate)
        {
            var cutoff = _clock.UtcNow - maxAge;
            foreach (var (key, session) in _sessions.Where(kv => kv.Value.LastEventAt < cutoff).ToList())
            {
                _sessions.Remove(key);
                removed.Add(new SessionChange(SessionChangeKind.Removed, session, session.Phase));
            }
        }
        foreach (var change in removed) Raise(change);
        return removed;
    }

    /// <summary>Worst phase among the agent's sessions: Error > NeedsInput > Working > Idle; null when there are none.</summary>
    public SessionPhase? AggregatePhase(AgentKind agent)
    {
        lock (_gate)
        {
            var phases = _sessions.Values.Where(s => s.Agent == agent).Select(s => s.Phase).ToList();
            if (phases.Count == 0) return null;
            if (phases.Contains(SessionPhase.Error)) return SessionPhase.Error;
            if (phases.Contains(SessionPhase.NeedsInput)) return SessionPhase.NeedsInput;
            if (phases.Contains(SessionPhase.Working)) return SessionPhase.Working;
            return SessionPhase.Idle;
        }
    }

    public static string DisplayNameFor(string? cwd, string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            var last = cwd.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(last)) return last;
        }
        return sessionId.Length > 8 ? sessionId[..8] : sessionId;
    }

    private string? ResolveCwd(AgentKind agent, string sessionId)
    {
        // The resolver is supplied by the App and reads the disk: it must never take down the pump thread.
        try { return _cwdResolver(agent, sessionId); }
        catch (Exception ex) { Report(ex); return null; }
    }

    /// <summary>Raises Changed; one throwing subscriber is reported and never stops the other subscribers or the caller.</summary>
    private void Raise(SessionChange change)
    {
        foreach (var handler in Changed?.GetInvocationList() ?? [])
        {
            try { ((Action<SessionChange>)handler)(change); }
            catch (Exception ex) { Report(ex); }
        }
    }

    private void Report(Exception ex)
    {
        try { OnError?.Invoke(ex); } catch { /* a broken logger must not take the tracker down */ }
    }

    private static string Truncate(string s) => s.Length <= MaxMessageLength ? s : s[..MaxMessageLength];
}
