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
        "permission_prompt", "idle_prompt", "agent_needs_input", "elicitation_dialog", "elicitation_url_dialog",
        "worker_permission_prompt"
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

        // idle_prompt ("Claude is waiting for your input") fires once the main session has been quiet for a while,
        // whether or not its background agents are still at work: while they work, so is the session.
        var idlePrompt = e.Event == "Notification" && string.Equals(e.NotificationType, "idle_prompt", StringComparison.OrdinalIgnoreCase);
        if (idlePrompt && existing is { } busy && (busy.ActiveSubagents > 0 || busy.AwaitingSubagents && busy.PendingWorkflows > 0))
            return null;

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

        // Claude Code 2.1+ lists on Stop the backgrounded agents and workflows still in flight. No foreground agent can
        // outlive the turn, so at that point the list is the whole truth: an agent it leaves out has finished even if
        // its SubagentStop never reached us (killed, interrupted, hook timed out), and one it names that we never saw
        // start is running. Without a list (Codex, older versions) the subagents stay as the events left them.
        var subagents = existing?.Subagents;
        var pendingWorkflows = existing?.PendingWorkflows ?? 0;
        var lastSubagentEventAt = existing?.LastSubagentEventAt;
        if (e.Event == "Stop" && e.BackgroundTasks is { } inFlight)
        {
            subagents = ReconcileSubagents(subagents, inFlight, e.Ts);
            pendingWorkflows = inFlight.Count(t => t.IsWorkflow);
            // Fresh proof that they are alive: the timeout sweep must not release them on the strength of an old start.
            if (inFlight.Any(t => t.IsAgent || t.IsWorkflow)) lastSubagentEventAt = e.Ts;
        }
        var running = subagents?.Count(s => s.Phase == SubagentPhase.Running) ?? 0;

        // A Stop that lands while background subagents (or a background workflow) are still running keeps the session
        // Working and remembers its message: the Idle transition (and the "finito" toast) waits for the last of them.
        var awaiting = existing?.AwaitingSubagents ?? false;
        if (e.Event == "UserPromptSubmit") awaiting = false;
        if (e.Event == "Stop")
        {
            if (running > 0 || (e.BackgroundTasks is not null && pendingWorkflows > 0))
            {
                phase = SessionPhase.Working;
                awaiting = true;
            }
            else
            {
                // Nothing left to wait for: a flag kept here would latch and fake a "finito" at the end of a later agent.
                awaiting = false;
            }
        }

        // SessionStart keeps the phase, and with it the reason of a wait; a notification (re)starts the wait.
        var awaitsPrompt = phase == SessionPhase.NeedsInput && (e.Event == "SessionStart" ? existing?.AwaitsPrompt ?? false : idlePrompt);
        DateTimeOffset? waitingSince = phase != SessionPhase.NeedsInput ? null : e.Event == "Notification" ? e.Ts : existing?.WaitingSince ?? e.Ts;

        var cwd = e.Cwd ?? existing?.Cwd ?? ResolveCwd(e.Agent, e.SessionId);
        // Every Claude hook payload carries the session transcript; an event without one (Codex, an older hook)
        // must not clear the path the token counter is already reading.
        var transcriptPath = e.TranscriptPath ?? existing?.TranscriptPath;
        // A `with` update on the existing record so state this state machine does not own (Tokens, Subagents,
        // and anything added later) survives every subsequent event instead of being silently reset by a
        // positional rebuild.
        var host = e.Host ?? existing?.Host;
        var title = e.Title ?? existing?.Title;
        var origin = OriginOf(e, existing);
        var updated = existing is null
            ? new SessionState(
                e.Agent, e.SessionId, NameFor(title, cwd, e.SessionId), cwd,
                phase.Value, message, e.Ts, e.Ts, TranscriptPath: transcriptPath, Subagents: subagents, AwaitingSubagents: awaiting,
                LastSubagentEventAt: lastSubagentEventAt, Host: host, Origin: origin, Title: title, PendingWorkflows: pendingWorkflows,
                AwaitsPrompt: awaitsPrompt, WaitingSince: waitingSince)
            : existing with
            {
                DisplayName = NameFor(title, cwd, e.SessionId),
                Cwd = cwd,
                Phase = phase.Value,
                AwaitsPrompt = awaitsPrompt,
                WaitingSince = waitingSince,
                Message = message,
                LastEventAt = e.Ts,
                TranscriptPath = transcriptPath,
                Subagents = subagents,
                AwaitingSubagents = awaiting,
                LastSubagentEventAt = lastSubagentEventAt,
                Host = host,
                Origin = origin,
                Title = title,
                PendingWorkflows = pendingWorkflows
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
        // SubagentStop lists the backgrounded work too, but not the foreground agents running next to the one that
        // stopped, so only its workflow count is used here: the agents are reconciled on Stop alone.
        var pendingWorkflows = existing?.PendingWorkflows ?? 0;
        if (e.Event == "SubagentStop" && e.BackgroundTasks is { } inFlight) pendingWorkflows = inFlight.Count(t => t.IsWorkflow);

        // A session merely waiting for its next prompt (idle_prompt) is back at work when an agent starts, like an Idle
        // one; a permission or a question still pending is not cleared by an agent a workflow happens to start.
        if (e.Event == "SubagentStart" && (phase == SessionPhase.Idle || phase == SessionPhase.NeedsInput && existing!.AwaitsPrompt))
        {
            // The session is Idle because the turn's Stop was already seen: an agent that starts afterwards
            // (a workflow step, a background task) re-arms the deferred Idle, so its SubagentStop takes the
            // session back to Idle instead of pinning it to "al lavoro" until the 12 h stale removal.
            phase = SessionPhase.Working;
            awaiting = true;
        }
        // A background workflow still in flight is between two phases: its next agents are about to start, and the
        // session wakes up (and sends its own Stop) when the workflow ends. Going Idle here would toast "finito" at
        // every phase boundary.
        if (e.Event == "SubagentStop" && running == 0 && awaiting && pendingWorkflows == 0)
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
        var title = e.Title ?? existing?.Title;
        var origin = OriginOf(e, existing);
        var updated = existing is null
            ? new SessionState(
                e.Agent, e.SessionId, NameFor(title, cwd, e.SessionId), cwd,
                phase, message, e.Ts, e.Ts,
                Subagents: subagents, AwaitingSubagents: awaiting, LastSubagentEventAt: e.Ts, Host: host,
                Origin: origin, Title: title, PendingWorkflows: pendingWorkflows)
            : existing with
            {
                DisplayName = NameFor(title, cwd, e.SessionId),
                Cwd = cwd,
                Phase = phase,
                AwaitsPrompt = phase == SessionPhase.NeedsInput && existing.AwaitsPrompt,
                WaitingSince = phase == SessionPhase.NeedsInput ? existing.WaitingSince : null,
                Message = message,
                LastEventAt = e.Ts,
                Subagents = subagents,
                AwaitingSubagents = awaiting,
                LastSubagentEventAt = e.Ts,
                Host = host,
                Origin = origin,
                Title = title,
                PendingWorkflows = pendingWorkflows
            };
        _sessions[key] = updated;
        return new SessionChange(existing is null ? SessionChangeKind.Added : SessionChangeKind.Updated, updated, existing?.Phase);
    }

    /// <summary>
    /// Applies the in-flight list of a Stop: a running agent it leaves out is Done, an agent it names that the session
    /// never saw start is added as Running. A finished agent it still names is left finished: SubagentStop may well
    /// have been written a moment before Claude Code updated the task, and bringing it back would pin the session.
    /// The children synthesised from Codex rollouts are not Claude's to judge. Returns <paramref name="current"/>
    /// itself when nothing changed.
    /// </summary>
    private static IReadOnlyList<SubagentState>? ReconcileSubagents(IReadOnlyList<SubagentState>? current,
        IReadOnlyList<BackgroundTask> inFlight, DateTimeOffset ts)
    {
        var agents = new Dictionary<string, BackgroundTask>(StringComparer.Ordinal);
        foreach (var task in inFlight.Where(t => t.IsAgent)) agents.TryAdd(task.Id, task);
        if ((current is null || current.Count == 0) && agents.Count == 0) return current;

        var list = current is null ? new List<SubagentState>() : new List<SubagentState>(current);
        var changed = false;
        for (var i = 0; i < list.Count; i++)
        {
            var known = list[i];
            var live = agents.Remove(known.AgentId);
            if (known.Phase != SubagentPhase.Running || live || known.AgentType == CodexSubagentScanner.SyntheticAgentType) continue;
            list[i] = known with { Phase = SubagentPhase.Done, EndedAt = ts };
            changed = true;
        }
        foreach (var task in agents.Values)
        {
            if (list.Any(s => s.AgentId == task.Id)) continue;
            list.Add(new SubagentState(task.Id, task.AgentType, SubagentPhase.Running, ts, null, null, TokenUsage.Zero));
            changed = true;
        }
        return changed ? TrimDone(list) : current;
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
    /// The ledgers follow the same rules: null keeps the stored one, an equal one is not a change.
    /// Returns null when nothing changed or the session is unknown.
    /// </summary>
    public SessionChange? UpdateTokens(AgentKind agent, string sessionId, TokenUsage? sessionTokens, IReadOnlyDictionary<string, TokenUsage>? subagentTokens,
        IReadOnlyDictionary<string, string>? subagentModels = null, UsageLedger? sessionLedger = null,
        IReadOnlyDictionary<string, UsageLedger>? subagentLedgers = null)
    {
        SessionChange? change;
        lock (_gate) change = UpdateTokensCore(agent, sessionId, sessionTokens, subagentTokens, subagentModels, sessionLedger, subagentLedgers);
        if (change is not null) Raise(change);
        return change;
    }

    /// <summary>
    /// Stores the totals without raising Changed: the pump's one-shot fill after the startup replay goes through
    /// here, so the restored rows get their token column without the App toasting "Errore API" or "Input richiesto"
    /// for a session whose event history was replayed rather than lived through.
    /// </summary>
    public void UpdateTokensSilently(AgentKind agent, string sessionId, TokenUsage? sessionTokens, IReadOnlyDictionary<string, TokenUsage>? subagentTokens,
        IReadOnlyDictionary<string, string>? subagentModels = null, UsageLedger? sessionLedger = null,
        IReadOnlyDictionary<string, UsageLedger>? subagentLedgers = null)
    {
        lock (_gate) UpdateTokensCore(agent, sessionId, sessionTokens, subagentTokens, subagentModels, sessionLedger, subagentLedgers);
    }

    private SessionChange? UpdateTokensCore(AgentKind agent, string sessionId, TokenUsage? sessionTokens,
        IReadOnlyDictionary<string, TokenUsage>? subagentTokens, IReadOnlyDictionary<string, string>? subagentModels,
        UsageLedger? sessionLedger, IReadOnlyDictionary<string, UsageLedger>? subagentLedgers)
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

        if (subagentModels is { Count: > 0 } && session.Subagents is { Count: > 0 } modelKnown)
        {
            List<SubagentState>? updatedList = null;
            for (var i = 0; i < modelKnown.Count; i++)
            {
                if (!subagentModels.TryGetValue(modelKnown[i].AgentId, out var model) || string.IsNullOrWhiteSpace(model) || model == modelKnown[i].Model) continue;
                updatedList ??= [.. subagents!];
                updatedList[i] = updatedList[i] with { Model = model };
            }
            if (updatedList is not null)
            {
                subagents = updatedList;
                changed = true;
            }
        }

        var ledger = session.Ledger;
        if (sessionLedger is not null && !sessionLedger.Equals(ledger))
        {
            ledger = sessionLedger;
            changed = true;
        }

        if (subagentLedgers is { Count: > 0 } && session.Subagents is { Count: > 0 } ledgerKnown)
        {
            List<SubagentState>? updatedList = null;
            for (var i = 0; i < ledgerKnown.Count; i++)
            {
                if (!subagentLedgers.TryGetValue(ledgerKnown[i].AgentId, out var subLedger) || subLedger.Equals(ledgerKnown[i].Ledger)) continue;
                updatedList ??= [.. subagents!];
                updatedList[i] = updatedList[i] with { Ledger = subLedger };
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
        var updated = session with { Tokens = tokens, Subagents = subagents, Ledger = ledger };
        _sessions[key] = updated;
        return new SessionChange(SessionChangeKind.Updated, updated, session.Phase);
    }

    /// <summary>
    /// Marks the subagents of every session that has heard nothing from them for <paramref name="timeout"/> as Done,
    /// so a subagent that died without a SubagentStop cannot pin its session to "al lavoro" forever.
    /// </summary>
    /// <remarks>
    /// <paramref name="lastActivity"/> tells, per running subagent, when it last showed signs of life (for Claude, the
    /// last write to its transcript): an agent busy on a single long task sends no event for a long while, and one
    /// active within <paramref name="timeout"/> keeps running. Null, or a null answer, means "no evidence".
    /// </remarks>
    public IReadOnlyList<SessionChange> SweepSubagentTimeouts(TimeSpan timeout,
        Func<SessionState, SubagentState, DateTimeOffset?>? lastActivity = null)
    {
        List<SessionChange> changes;
        lock (_gate) changes = SweepSubagentTimeoutsCore(timeout, lastActivity);
        foreach (var change in changes) Raise(change);
        return changes;
    }

    /// <summary>Sweeps the subagent timeouts without raising Changed (startup replay).</summary>
    public void SweepSubagentTimeoutsSilently(TimeSpan timeout, Func<SessionState, SubagentState, DateTimeOffset?>? lastActivity = null)
    {
        lock (_gate) SweepSubagentTimeoutsCore(timeout, lastActivity);
    }

    private List<SessionChange> SweepSubagentTimeoutsCore(TimeSpan timeout, Func<SessionState, SubagentState, DateTimeOffset?>? lastActivity)
    {
        var changes = new List<SessionChange>();
        var now = _clock.UtcNow;
        foreach (var (key, session) in _sessions.ToList())
        {
            // A session held Working by a background workflow alone is swept too: a workflow that died without
            // waking its session would otherwise keep it "al lavoro" until the 12 h removal.
            var waitsOnWorkflow = session.AwaitingSubagents && session.PendingWorkflows > 0;
            if (session.ActiveSubagents == 0 && !waitsOnWorkflow) continue;
            var last = session.LastSubagentEventAt ?? session.LastEventAt;
            if (now - last <= timeout) continue;

            var subagents = session.Subagents?
                .Select(s => s.Phase == SubagentPhase.Running && !IsActive(session, s) ? s with { Phase = SubagentPhase.Done, EndedAt = now } : s)
                .ToList();
            var stillRunning = subagents?.Count(s => s.Phase == SubagentPhase.Running) ?? 0;
            // Every agent is still visibly at work: nothing to change, the next sweep looks again.
            if (stillRunning > 0 && stillRunning == session.ActiveSubagents) continue;

            // Same rule as the last SubagentStop: only a Working session goes Idle, so an error or a pending
            // input that arrived while the agents were running survives the timeout.
            var release = stillRunning == 0 && session.AwaitingSubagents && session.Phase == SessionPhase.Working;
            var updated = session with
            {
                Phase = release ? SessionPhase.Idle : session.Phase,
                Message = release ? session.Message ?? "Turno completato" : session.Message,
                Subagents = subagents is null ? null : TrimDone(subagents),
                AwaitingSubagents = stillRunning > 0 && session.AwaitingSubagents,
                PendingWorkflows = stillRunning > 0 ? session.PendingWorkflows : 0
            };
            _sessions[key] = updated;
            changes.Add(new SessionChange(SessionChangeKind.Updated, updated, session.Phase));
        }
        return changes;

        bool IsActive(SessionState session, SubagentState subagent)
        {
            if (lastActivity is null) return false;
            try { return lastActivity(session, subagent) is { } at && now - at <= timeout; }
            catch (Exception ex) { Report(ex); return false; }
        }
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

    /// <summary>
    /// Most urgent phase among the agent's sessions: Error > NeedsInput (a permission or a question) > Working >
    /// NeedsInput (only waiting for the next prompt) > Idle; null when there are none. A session that finished its turn
    /// and waits for the next prompt must not paint the icon amber while another one is at work.
    /// </summary>
    public SessionPhase? AggregatePhase(AgentKind agent)
    {
        lock (_gate)
        {
            SessionState? worst = null;
            foreach (var session in _sessions.Values.Where(s => s.Agent == agent))
                if (worst is null || Urgency(session) > Urgency(worst)) worst = session;
            return worst?.Phase;
        }
    }

    /// <summary>Ranks a session for <see cref="AggregatePhase"/> and the notch summary.</summary>
    public static int Urgency(SessionState session) => session.Phase switch
    {
        SessionPhase.Error => 4,
        SessionPhase.NeedsInput when !session.AwaitsPrompt => 3,
        SessionPhase.Working => 2,
        SessionPhase.NeedsInput => 1,
        _ => 0
    };

    /// <summary>
    /// Removes the finished (Idle or Error) sessions of <paramref name="origin"/> quiet for longer than
    /// <paramref name="maxIdle"/>. An app keeps the process of a conversation open long after it is over, so an app
    /// session is shown while it works and for a while after its last turn, not until the app closes; it comes back
    /// with its next event. <paramref name="silent"/> removes them without raising Changed (startup replay).
    /// </summary>
    public IReadOnlyList<SessionChange> RemoveIdle(SessionOrigin origin, TimeSpan maxIdle, bool silent = false)
    {
        var removed = new List<SessionChange>();
        lock (_gate)
        {
            var cutoff = _clock.UtcNow - maxIdle;
            foreach (var (key, session) in _sessions.ToList())
            {
                if (session.Origin != origin || session.Phase is not (SessionPhase.Idle or SessionPhase.Error)) continue;
                if (session.ActiveSubagents > 0 || session.AwaitingSubagents || session.LastEventAt >= cutoff) continue;
                _sessions.Remove(key);
                removed.Add(new SessionChange(SessionChangeKind.Removed, session, session.Phase));
            }
        }
        if (!silent) foreach (var change in removed) Raise(change);
        return removed;
    }

    /// <summary>The title a source gave the session, otherwise the name derived from its cwd.</summary>
    private static string NameFor(string? title, string? cwd, string sessionId) =>
        string.IsNullOrWhiteSpace(title) ? DisplayNameFor(cwd, sessionId) : title.Trim();

    /// <summary>
    /// The source that created the event wins (registry, cloud); a hook line says where it runs through the
    /// entrypoint of its host; otherwise the session keeps what it has, and a new one is a terminal session.
    /// </summary>
    private static SessionOrigin OriginOf(HookEvent e, SessionState? existing) =>
        e.Origin ?? HostInfo.OriginOf(e.Host?.Entrypoint) ?? existing?.Origin ?? SessionOrigin.Terminal;

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
