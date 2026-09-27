using System.Collections.Concurrent;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Sessions;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>Feeds events.jsonl into the SessionTracker: silent replay at start, then FileSystemWatcher + 2 s poll, rotation and stale sweeps.</summary>
public sealed class HookEventPump : IDisposable
{
    private readonly HookEventReader _reader;
    private readonly SessionTracker _tracker;
    private readonly AppPaths _paths;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _poll;
    private DateTimeOffset _lastStaleSweep;
    private DateTimeOffset _lastCodexScan;
    private DateTimeOffset _lastTokenRefresh;
    private DateTimeOffset _lastLivenessSweep;
    private DateTimeOffset _lastRegistryScan;
    /// <summary>
    /// Sessions the hook bridge has reported, with the instant of their newest event: the Claude registry feed leaves
    /// them to the hooks. Kept for <see cref="SessionProcessRegistry.EndedMemory"/>, so a session ended by its own
    /// SessionEnd is not adopted back from a record its exiting process has not deleted yet.
    /// </summary>
    private readonly Dictionary<(AgentKind Agent, string SessionId), DateTimeOffset> _hookSessions = new();
    /// <summary>Events handed in by other sources (the cloud poller), applied by the next Pump on its own thread.</summary>
    private readonly ConcurrentQueue<(HookEvent Event, bool Silent)> _injected = new();
    /// <summary>
    /// Set by <see cref="Start"/> and spent by the first <see cref="Pump"/>: the sessions restored by the silent
    /// replay must get their token totals once, on the pump thread and without raising Changed.
    /// </summary>
    private bool _fillTokensOnFirstPump;
    /// <summary>
    /// Finds the transcript of a running Claude subagent, which has no path of its own until its SubagentStop. Used on
    /// the pump thread only, like every locator.
    /// </summary>
    private readonly ClaudeAgentTranscriptLocator _agentTranscripts = new();
    /// <summary>
    /// Claude subagents already closed from their transcript, per session: each one gets a single synthetic
    /// SubagentStop, even if a later SubagentStart brings it back while its transcript still ends the same way.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _endedFromTranscript = new(StringComparer.Ordinal);
    /// <summary>
    /// Running subagents whose name is settled, per session: named, or found without a name. They are not asked again;
    /// the others (no transcript, meta.json or rollout yet) are asked at every periodic pass while they run.
    /// </summary>
    private readonly Dictionary<(AgentKind Agent, string SessionId), HashSet<string>> _namesSettled = new();
    /// <summary>
    /// The subagents the silent replay of <see cref="Start"/> left running, with the start of that run: one of them
    /// found over in its transcript ended while the app was closed, so its synthetic SubagentStop is
    /// <see cref="HookEvent.Quiet"/> and the turn it may end is not announced. A later run of the same agent (another
    /// StartedAt) was seen live and is not quiet. Pruned at every pass to the runs still going.
    /// </summary>
    private readonly Dictionary<(AgentKind Agent, string SessionId, string AgentId), DateTimeOffset> _replayedAgents = new();
    /// <summary>
    /// Child thread ids this pump has announced per Codex session, with the instant of the announcement: only these
    /// are ever stopped here, and the instant says when a still-running child must be announced again so the
    /// subagent timeout cannot release a child the scanner can plainly see is alive.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _synthesisedChildren = new(StringComparer.Ordinal);
    /// <summary>
    /// Instant of the newest SubagentStart/SubagentStop the hook bridge reported for each Codex session: while it is
    /// younger than <see cref="CodexHookGrace"/> that session counts its subagents from the hooks alone.
    /// </summary>
    private readonly Dictionary<string, DateTimeOffset> _hookSubagents = new(StringComparer.Ordinal);
    /// <summary>
    /// Instant this pump last announced each Codex child it follows from the child's own rollout, per session: a child
    /// whose turn stays open is announced again once half the subagent timeout has passed, so the sweep cannot release it.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _followedChildren = new(StringComparer.Ordinal);
    /// <summary>
    /// The Codex children left Running by <see cref="Start"/>, per session: what the first live scan learns about them
    /// happened while the app was not looking, so the events it synthesises for them are Quiet. Spent by that scan.
    /// </summary>
    private Dictionary<string, HashSet<string>>? _replayedChildren;

    public TimeSpan ReplayWindow { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan StaleSweepEvery { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a session waits for a subagent that never sent its SubagentStop before being released.</summary>
    public TimeSpan SubagentTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Cadence of the Codex rollout scan. It is much shorter than <see cref="StaleSweepEvery"/> on purpose: a child
    /// thread writes its `task_complete` the instant it finishes, and scanning only every 5 minutes would keep the
    /// session at "al lavoro · N agenti" — and delay the "Turno completato" toast — for minutes after the real end.
    /// </summary>
    public TimeSpan CodexScanEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fallback used when Codex does not fire SubagentStart/SubagentStop for its child threads: the scanner reads the
    /// rollouts and the pump turns what it finds into the same events the hook bridge would have written. Null disables it.
    /// </summary>
    public CodexSubagentScanner? CodexSubagents { get; init; }

    /// <summary>
    /// Whether the rollout fallback may run at all (the App wires the user setting). It is read at every scan, so
    /// switching it off — or back on — takes effect without restarting the app; the children already announced are
    /// stopped as soon as it goes off. Null means always on.
    /// </summary>
    public Func<bool>? CodexSubagentsEnabled { get; init; }

    /// <summary>
    /// How long a SubagentStart/SubagentStop reported by the hook bridge keeps the rollout fallback out of that Codex
    /// session. Codex does emit the two events once the groups are approved with <c>/hooks</c>, and its
    /// <c>agent_id</c> is not necessarily the child thread id: with both producers live the same child would be
    /// counted twice (one child reading "al lavoro · 2 agenti"), the subagent summary would carry an entry no rollout
    /// total can match, and the deferred Idle would wait for two stops. The hooks win — they are the authoritative
    /// source — and the fallback comes back only if they go quiet for a whole grace window, which is deliberately
    /// long: it spans the idle stretches between two subagents of the same session, and any new hook event renews it.
    /// </summary>
    public TimeSpan CodexHookGrace { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// How often the token totals of the sessions that are still busy (Working or NeedsInput) are read again. Idle
    /// sessions are left alone: their transcript is not growing, and re-reading every one of them would spend IO on
    /// rows that cannot change. A session that ends is refreshed once by its Stop, which is what closes the count.
    /// The same periodic pass, with or without a <see cref="TokenSource"/>, closes the Claude subagents whose
    /// transcript says they are over.
    /// </summary>
    public TimeSpan TokenRefreshEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads the token totals of a session and of its subagents (the App combines the Claude transcript counter and
    /// the Codex rollout counter). Null disables token counting entirely. All its IO runs on the pump thread.
    /// </summary>
    public ITokenSource? TokenSource { get; init; }

    /// <summary>
    /// Ties sessions to the process of their agent and tells which ones lost it (terminal closed, crash, reboot): those
    /// are ended as if they had sent <c>SessionEnd</c>. Null keeps them until the <see cref="StaleAfter"/> sweep.
    /// </summary>
    public SessionProcessRegistry? Processes { get; init; }

    /// <summary>
    /// The Claude Code sessions no hook reports (the desktop app, the SDK, a machine without the hooks), read from the
    /// registry Claude Code keeps in <c>~/.claude/sessions</c>; it also ties every tracked Claude session to its process
    /// for <see cref="Processes"/>. Null disables both.
    /// </summary>
    public ClaudeRegistrySessionFeed? ClaudeRegistry { get; init; }

    /// <summary>How often the Claude session registry is read (a handful of small files).</summary>
    public TimeSpan RegistryScanEvery { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often the processes of the sessions are checked.</summary>
    public TimeSpan LivenessSweepEvery { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a finished session of the desktop app (or of an SDK host) stays after its last event. Those apps keep
    /// the process of a conversation open long after it is over, so neither its process nor a SessionEnd says when it
    /// is done; it comes back with its next event. Swept at the <see cref="LivenessSweepEvery"/> cadence.
    /// </summary>
    public TimeSpan AppIdleWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Tells what a live attention notification of Claude Code waits for (the tool_use pending in its transcript or in
    /// one of its agents'), so the event reaches the tracker with its <see cref="HookEvent.Attention"/>. Only the live
    /// paths use it: the silent replay and the other silent applies never read a transcript. Null disables it.
    /// </summary>
    public AttentionResolver? Attention { get; init; }

    /// <summary>Where unexpected failures go (the App wires a FileLogger): the pump never lets one escape a thread-pool callback.</summary>
    public Action<Exception>? OnError { get; init; }

    /// <summary>One line per notable decision (a session ended because its process died); the App wires the log.</summary>
    public Action<string>? OnInfo { get; init; }

    public HookEventPump(HookEventReader reader, SessionTracker tracker, AppPaths paths, IClock clock)
    {
        _reader = reader;
        _tracker = tracker;
        _paths = paths;
        _clock = clock;
    }

    public void Start()
    {
        Directory.CreateDirectory(_paths.MonitorDir);

        lock (_gate)
        {
            try
            {
                // Silent replay: no Changed events, so the UI does not toast history.
                var replayed = _reader.ReadAll(_clock.UtcNow - ReplayWindow).ToList();
                // Before the state is rebuilt: a Codex session whose hooks reported subagents within the replay
                // window must not get the rollout fallback on top of them at the very first scan.
                NoteHookSubagents(replayed);
                NoteHookSessions(replayed);
                _tracker.ApplySilently(replayed);
                _tracker.RemoveStaleSilently(StaleAfter);
                // Sessions whose terminal was closed (or the machine rebooted) while the app was not running: their
                // process is gone, and the replay must not bring them back.
                Processes?.Load();
                EndDeadSessions(silent: true);
                _tracker.RemoveIdle(SessionOrigin.App, AppIdleWindow, silent: true);
                // The sessions of the desktop app that are open right now, without toasting them as new.
                SyncClaudeRegistry(silent: true);
                _lastRegistryScan = _clock.UtcNow;
                // A session whose subagents stopped reporting before the app started must not come back as
                // "al lavoro · N agenti": release them here too, silently, so the replay toasts nothing.
                _tracker.SweepSubagentTimeoutsSilently(SubagentTimeout, SubagentActivity());
                // A Codex child thread that is running right now must be picked up before the first Changed is
                // raised, or the replayed session would flip Idle → Working → Idle and toast a turn it never ran.
                SyncCodexSubagents(silent: true);
                // The children still running now may have finished while the app was not running: the first live
                // scan must not announce the end of a turn that was over before the app was looking.
                _replayedChildren = RunningCodexChildren();
                _lastStaleSweep = _clock.UtcNow;
                _lastCodexScan = _clock.UtcNow;
                _lastTokenRefresh = _clock.UtcNow;
                _lastLivenessSweep = _clock.UtcNow;
                // The replayed sessions have no totals yet and most of them are Idle, so neither the periodic pass
                // (Working/NeedsInput only) nor an event-driven refresh would ever visit them: the first Pump fills
                // them all once. Not here: Start() runs on the UI thread and the first read of a large transcript
                // parses it whole, which would freeze the notch at launch.
                _fillTokensOnFirstPump = TokenSource is not null;
                // What the replay left running: if a transcript later says one of these is over, it ended while the
                // app was closed, and the turn it closes must not be announced now.
                RememberReplayedAgents();
            }
            catch (Exception ex)
            {
                // A locked or unreadable events file must never abort startup: log it and start with an empty state.
                Report(ex);
            }
        }

        _watcher = new FileSystemWatcher(_paths.MonitorDir, Path.GetFileName(_paths.EventsFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += (_, _) => Pump();
        _watcher.Created += (_, _) => Pump();
        _poll = new Timer(_ => Pump(), null, PollInterval, PollInterval);
    }

    public void Pump()
    {
        lock (_gate)
        {
            try
            {
                if (_fillTokensOnFirstPump)
                {
                    // Cleared before the loop, not after: the fill must stay one-shot even if something in it
                    // escapes, or every pump would re-read every transcript for the life of the process.
                    _fillTokensOnFirstPump = false;
                    foreach (var session in _tracker.Sessions) RefreshTokens(session, silent: true);
                }
                ApplyBatch(_reader.ReadNew().ToList());
                ApplyInjected();
                _reader.RotateIfNeeded();
                if (ClaudeRegistry is not null && _clock.UtcNow - _lastRegistryScan >= RegistryScanEvery)
                {
                    _lastRegistryScan = _clock.UtcNow;
                    SyncClaudeRegistry(silent: false);
                }
                if (_clock.UtcNow - _lastCodexScan >= CodexScanEvery)
                {
                    _lastCodexScan = _clock.UtcNow;
                    SyncCodexSubagents(silent: false);
                }
                if (_clock.UtcNow - _lastTokenRefresh >= TokenRefreshEvery)
                {
                    _lastTokenRefresh = _clock.UtcNow;
                    // Only the busy sessions: an Idle transcript is not growing any more, and its last total was
                    // already read by the Stop (or the last SubagentStop) that ended the turn.
                    if (TokenSource is not null)
                        foreach (var session in _tracker.Sessions.Where(s => s.Phase is SessionPhase.Working or SessionPhase.NeedsInput))
                            RefreshTokens(session);
                    // Same pass, with or without a token source: the Claude agents whose transcript says they are over.
                    EndTerminatedSubagents();
                    // Then the name of every running agent that has none yet.
                    ResolveSubagentNames();
                }
                if (_clock.UtcNow - _lastLivenessSweep >= LivenessSweepEvery)
                {
                    _lastLivenessSweep = _clock.UtcNow;
                    EndDeadSessions(silent: false);
                    _tracker.RemoveIdle(SessionOrigin.App, AppIdleWindow);
                }
                if (_clock.UtcNow - _lastStaleSweep >= StaleSweepEvery)
                {
                    _lastStaleSweep = _clock.UtcNow;
                    _tracker.RemoveStale(StaleAfter);
                    // Same cadence as the stale removal, on the sessions that survived it: a subagent that died
                    // without a SubagentStop would otherwise pin its session to "al lavoro" until the 12 h sweep.
                    _tracker.SweepSubagentTimeouts(SubagentTimeout, SubagentActivity());
                }
            }
            catch (Exception ex)
            {
                // Pump runs on FileSystemWatcher and Timer callbacks: an escaping exception would kill the process.
                // The hook may be mid-append, a resolver may misbehave: report and let the next poll retry.
                Report(ex);
            }
        }
    }

    /// <summary>
    /// Queues events from a source other than the hook bridge (the cloud sessions); the next <see cref="Pump"/> applies
    /// them in order after the hook lines it reads. <paramref name="silent"/> applies them without raising Changed —
    /// the first read after start-up, which must not toast what was already going on. Thread-safe.
    /// </summary>
    public void Inject(IEnumerable<HookEvent> events, bool silent = false)
    {
        foreach (var e in events) _injected.Enqueue((e, silent));
    }

    /// <summary>
    /// Refresh a comando: re-reads the tokens of EVERY session of <paramref name="agent"/> — the Idle ones too, which
    /// the periodic pass skips — on a pool thread under the pump lock, raising Changed for what moved. Never faults.
    /// </summary>
    public Task RefreshTokensNowAsync(AgentKind agent)
    {
        if (TokenSource is null) return Task.CompletedTask;
        return Task.Run(() =>
        {
            lock (_gate)
            {
                try
                {
                    foreach (var session in _tracker.Sessions.Where(s => s.Agent == agent)) RefreshTokens(session);
                }
                catch (Exception ex)
                {
                    Report(ex);
                }
            }
        });
    }

    /// <summary>
    /// Applies a batch of events, announcing the live Codex child threads immediately before the Stop that would
    /// otherwise be applied with no subagent in sight. Codex does not emit SubagentStart for its children, so a Stop
    /// applied before the scan takes the session to Idle and toasts "Turno completato" while a child is still
    /// running; the row would then flip back to "al lavoro" on the next scan and toast a second time at the real end.
    /// The scan runs per session and at most once per batch for each of them (it already covers every session it
    /// knows), so a session created by an earlier event of this very batch is covered too.
    /// </summary>
    private void ApplyBatch(IReadOnlyList<HookEvent> batch)
    {
        // Whole batch first: a SubagentStart that sits after the Stop in the same batch still proves the hooks are
        // reporting, and the scan the Stop triggers must already know it.
        NoteHookSubagents(batch);
        NoteHookSessions(batch);
        HashSet<string>? synced = null;
        foreach (var ev in batch)
        {
            if (CodexSubagents is not null && ev.Agent == AgentKind.Codex && ev.Event == "Stop")
            {
                synced ??= new HashSet<string>(StringComparer.Ordinal);
                if (synced.Add(ev.SessionId))
                {
                    SyncCodexSubagents(silent: false);
                    _lastCodexScan = _clock.UtcNow;
                    foreach (var id in _synthesisedChildren.Keys) synced.Add(id);
                }
            }
            ApplyTracked(ev);
        }
    }

    /// <summary>
    /// Applies one live event and, when it is the end of a turn or of a subagent, reads the token totals right away:
    /// those sessions leave the Working/NeedsInput set the periodic refresh visits, so this is the last chance to
    /// record what the turn really cost before the row goes quiet. An attention notification first gets the detail of
    /// its wait from <see cref="Attention"/>.
    /// </summary>
    private void ApplyTracked(HookEvent ev)
    {
        ev = WithAttention(ev);
        _tracker.Apply(ev);
        if (TokenSource is null || ev.Event is not ("Stop" or "StopFailure" or "SubagentStop")) return;
        var session = _tracker.Sessions.FirstOrDefault(s => s.Agent == ev.Agent && s.SessionId == ev.SessionId);
        if (session is not null) RefreshTokens(session);
    }

    /// <summary>
    /// The event with the detail of its wait when it is an attention notification and a resolver is wired; the event
    /// itself otherwise. The tracked session (before this event) is looked up for a Notification only.
    /// </summary>
    private HookEvent WithAttention(HookEvent ev)
    {
        if (Attention is null || ev.Event != "Notification") return ev;
        var session = _tracker.Sessions.FirstOrDefault(s => s.Agent == ev.Agent && s.SessionId == ev.SessionId);
        return Attention.Resolve(ev, session) is { } detail ? ev with { Attention = detail } : ev;
    }

    /// <summary>
    /// Closes the running Claude subagents whose transcript says they are over although their SubagentStop never came:
    /// interrupted by the user, stopped by an API error (the session limit), stopped from the task list
    /// (<see cref="SubagentTranscriptEnd"/>). Each one gets a single live synthetic SubagentStop (source "transcript"),
    /// which follows the usual rules: the last agent of a session waiting for its agents ends the turn. One the replay
    /// of <see cref="Start"/> restored gets a <see cref="HookEvent.Quiet"/> one: it ended while the app was closed.
    /// The paths the locator finds are handed to the tracker too, so an agent a workflow runs is known by its folder
    /// whatever its type says. Called by the periodic pass only, never by the silent replay of <see cref="Start"/>.
    /// Codex sessions are left to their own rules: their children are no Claude transcripts.
    /// </summary>
    private void EndTerminatedSubagents()
    {
        var sessions = _tracker.Sessions.Where(s => s.Agent == AgentKind.Claude).ToList();
        var live = sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _endedFromTranscript.Keys.Where(id => !live.Contains(id)).ToList())
            _endedFromTranscript.Remove(gone);
        PruneReplayedAgents();

        var now = _clock.UtcNow;
        foreach (var session in sessions)
        {
            // A finished agent is never searched again: its cached path would only keep the locator growing.
            foreach (var done in session.Subagents?.Where(s => s.Phase == SubagentPhase.Done) ?? [])
                _agentTranscripts.Forget(done.AgentId);

            _endedFromTranscript.TryGetValue(session.SessionId, out var ended);
            var located = new Dictionary<string, string>(StringComparer.Ordinal);
            var terminated = new List<(SubagentState Agent, string Path)>();
            foreach (var agent in session.RunningSubagents)
            {
                if (ended?.Contains(agent.AgentId) == true) continue;
                var path = agent.TranscriptPath;
                if (path is null && _agentTranscripts.Locate(session.TranscriptPath, session.SessionId, agent.AgentId) is { } found)
                    located[agent.AgentId] = path = found;
                if (path is not null && SubagentTranscriptEnd.IsTerminated(path)) terminated.Add((agent, path));
            }
            // The path found on disk becomes the agent's own: an agent run by a workflow lies under subagents/workflows/,
            // and the next Stop that names only its workflow keeps it running by that.
            if (located.Count > 0) _tracker.UpdateSubagentPaths(session.Agent, session.SessionId, located);

            foreach (var (agent, path) in terminated)
            {
                if (ended is null) _endedFromTranscript[session.SessionId] = ended = new HashSet<string>(StringComparer.Ordinal);
                ended.Add(agent.AgentId);
                ApplyTracked(new HookEvent(now, AgentKind.Claude, "SubagentStop", session.SessionId, session.Cwd, null, null,
                    "transcript", agent.AgentId, agent.AgentType, AgentTranscriptPath: path, Quiet: IsReplayedRun(session, agent)));
            }
        }
    }

    /// <summary>
    /// Gives each running subagent the name it was started with, once (<see cref="SubagentNames"/>): a Claude agent the
    /// description of the <c>agent-&lt;id&gt;.meta.json</c> next to its transcript (its own path, or the one the locator
    /// finds while it runs), a Codex child the nickname and agent path of its rollout
    /// (<see cref="CodexSubagentScanner.ChildInfo"/>). An agent whose files cannot tell yet is asked again at the next
    /// pass; one they answered, with or without a name, is not. Called by the periodic pass only: the replayed agents
    /// get their names at the first one. Names are never logged.
    /// </summary>
    private void ResolveSubagentNames()
    {
        var sessions = _tracker.Sessions;
        var live = sessions.Select(s => (s.Agent, s.SessionId)).ToHashSet();
        foreach (var gone in _namesSettled.Keys.Where(k => !live.Contains(k)).ToList()) _namesSettled.Remove(gone);

        foreach (var session in sessions)
        {
            var key = (session.Agent, session.SessionId);
            _namesSettled.TryGetValue(key, out var settled);
            // An agent that finished leaves the set: a Codex child that comes back for a new turn keeps its name
            // anyway, and a nameless one is simply asked again.
            settled?.RemoveWhere(id => !session.RunningSubagents.Any(s => s.AgentId == id));
            Dictionary<string, string>? names = null;
            foreach (var agent in session.RunningSubagents)
            {
                if (agent.Name is not null || settled?.Contains(agent.AgentId) == true) continue;
                if (!TryResolveName(session, agent, out var name)) continue;
                if (settled is null) _namesSettled[key] = settled = new HashSet<string>(StringComparer.Ordinal);
                settled.Add(agent.AgentId);
                if (name is not null) (names ??= new Dictionary<string, string>(StringComparer.Ordinal))[agent.AgentId] = name;
            }
            if (names is not null) _tracker.UpdateSubagentNames(session.Agent, session.SessionId, names);
        }
    }

    /// <summary>
    /// False while the agent's files cannot tell its name yet (no transcript found, no meta.json, no rollout); true
    /// once they did, with the name or null when the agent has none.
    /// </summary>
    private bool TryResolveName(SessionState session, SubagentState agent, out string? name)
    {
        name = null;
        if (session.Agent == AgentKind.Claude)
            return SubagentNames.TryReadClaude(
                agent.TranscriptPath ?? _agentTranscripts.Locate(session.TranscriptPath, session.SessionId, agent.AgentId), out name);
        if (session.Agent != AgentKind.Codex || CodexSubagents?.ChildInfo(agent.AgentId) is not { } child) return false;
        name = SubagentNames.FromCodex(child.Nickname, child.AgentPath);
        return true;
    }

    /// <summary>Records every subagent the silent replay left running, with the start of its run.</summary>
    private void RememberReplayedAgents()
    {
        _replayedAgents.Clear();
        foreach (var session in _tracker.Sessions)
            foreach (var agent in session.RunningSubagents)
                _replayedAgents[(session.Agent, session.SessionId, agent.AgentId)] = agent.StartedAt;
    }

    /// <summary>Forgets the replayed runs that are over (or whose session is gone): only running ones can still be closed.</summary>
    private void PruneReplayedAgents()
    {
        if (_replayedAgents.Count == 0) return;
        var running = _tracker.Sessions
            .SelectMany(s => s.RunningSubagents.Select(a => (s.Agent, s.SessionId, a.AgentId, a.StartedAt)))
            .ToHashSet();
        foreach (var key in _replayedAgents.Where(kv => !running.Contains((kv.Key.Agent, kv.Key.SessionId, kv.Key.AgentId, kv.Value)))
                     .Select(kv => kv.Key).ToList())
            _replayedAgents.Remove(key);
    }

    /// <summary>True when <paramref name="agent"/> is still the very run the replay of <see cref="Start"/> restored.</summary>
    private bool IsReplayedRun(SessionState session, SubagentState agent) =>
        _replayedAgents.TryGetValue((session.Agent, session.SessionId, agent.AgentId), out var startedAt) && startedAt == agent.StartedAt;

    /// <summary>
    /// Reads the totals for one session and hands them to the tracker, which raises Changed only if something moved.
    /// The source reads files: a locked transcript is reported and the other sessions keep their refresh.
    /// <paramref name="silent"/> is the startup fill: the totals land on the rows without a Changed, because the
    /// App's toasts gate only the Idle transition on the previous phase and would announce "Errore API" or
    /// "Input richiesto" for every session the replay restored in those phases.
    /// </summary>
    private void RefreshTokens(SessionState session, bool silent = false)
    {
        if (TokenSource is null) return;
        try
        {
            var sessionTokens = TokenSource.SessionTokens(session);
            var subagentTokens = TokenSource.SubagentTokens(session);
            var subagentModels = TokenSource.SubagentModels(session);
            var sessionLedger = TokenSource.SessionLedger(session);
            var subagentLedgers = TokenSource.SubagentLedgers(session);
            if (silent) _tracker.UpdateTokensSilently(session.Agent, session.SessionId, sessionTokens, subagentTokens, subagentModels, sessionLedger, subagentLedgers);
            else _tracker.UpdateTokens(session.Agent, session.SessionId, sessionTokens, subagentTokens, subagentModels, sessionLedger, subagentLedgers);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Turns the live child threads of every Codex session into SubagentStart/SubagentStop events, so the counter,
    /// the deferred Idle and the timeout live in the tracker alone. Only the children this pump announced are ever
    /// stopped by the fallback; the children the hooks report follow the turns of their own rollouts
    /// (<see cref="FollowKnownChildren"/>), on the live scans only.
    /// </summary>
    private void SyncCodexSubagents(bool silent)
    {
        if (CodexSubagents is null) return;

        var sessions = _tracker.Sessions.Where(s => s.Agent == AgentKind.Codex).ToList();
        var live = sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _synthesisedChildren.Keys.Where(id => !live.Contains(id)).ToList())
            _synthesisedChildren.Remove(gone);
        foreach (var gone in _hookSubagents.Keys.Where(id => !live.Contains(id)).ToList())
            _hookSubagents.Remove(gone);
        foreach (var gone in _followedChildren.Keys.Where(id => !live.Contains(id)).ToList())
            _followedChildren.Remove(gone);

        var enabled = CodexSubagentsEnabled?.Invoke() ?? true;
        var now = _clock.UtcNow;
        // Half the timeout: a child still running at that point is announced again, which refreshes
        // LastSubagentEventAt, so SweepSubagentTimeouts can only release children the scanner no longer sees.
        var refreshAfter = SubagentTimeout / 2;
        foreach (var session in sessions)
        {
            // Two producers for one child would double it: the fallback stands down for a session whose hooks report
            // subagents (and for all of them when the setting is off), releasing whatever it had announced. The
            // children the session knows are then followed turn by turn from their own rollouts.
            if (!enabled || HooksReportSubagents(session.SessionId, now))
            {
                ReleaseSynthesisedChildren(session, now, silent);
                if (!silent) FollowKnownChildren(session.SessionId, followSynthesised: enabled);
                continue;
            }

            if (!_synthesisedChildren.TryGetValue(session.SessionId, out var announced))
                _synthesisedChildren[session.SessionId] = announced = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

            // A scan that could not read the rollouts says nothing: keeping the announced children is the only safe
            // reading, and the timeout sweep stays the single thing that can release them.
            if (!CodexSubagents.TryGetActiveChildren(session.SessionId, out var children)) continue;
            var active = children.ToHashSet(StringComparer.Ordinal);

            var events = new List<HookEvent>();
            var next = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var child in active)
            {
                var known = announced.TryGetValue(child, out var at);
                // A re-announce also recovers a child the timeout already marked Done: UpsertSubagent is idempotent
                // for a known id, and ApplySubagentEvent takes an Idle session back to Working.
                if (!known || now - at >= refreshAfter || !IsRunning(session, child))
                {
                    events.Add(SyntheticSubagentEvent("SubagentStart", session, child, now));
                    next[child] = now;
                }
                else next[child] = at;
            }
            foreach (var child in announced.Keys.Where(c => !active.Contains(c)))
                events.Add(SyntheticSubagentEvent("SubagentStop", session, child, now));

            _synthesisedChildren[session.SessionId] = next;

            if (events.Count == 0) continue;
            if (silent) _tracker.ApplySilently(events);
            // ApplyTracked, not Apply: the SubagentStop that ends the last Codex child is synthesised here, and it is
            // the one that takes the session Idle — the totals must be read before the row stops being refreshed.
            else foreach (var e in events) ApplyTracked(e);
        }
        // Only the first live scan speaks for the time before the start: what the next ones find happened in front of the app.
        if (!silent) _replayedChildren = null;
    }

    /// <summary>The Codex children Running right now, per session; null when there is none (or no scanner to follow them).</summary>
    private Dictionary<string, HashSet<string>>? RunningCodexChildren()
    {
        if (CodexSubagents is null) return null;
        var running = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var session in _tracker.Sessions.Where(s => s.Agent == AgentKind.Codex))
        {
            var ids = session.Subagents?.Where(s => s.Phase == SubagentPhase.Running).Select(s => s.AgentId).ToHashSet(StringComparer.Ordinal);
            if (ids is { Count: > 0 }) running[session.SessionId] = ids;
        }
        return running.Count > 0 ? running : null;
    }

    /// <summary>
    /// Applies the injected events, then reads the tokens of the sessions they started: an idle one is otherwise
    /// never visited by the periodic refresh.
    /// </summary>
    private void ApplyInjected()
    {
        List<(string SessionId, bool Silent)>? started = null;
        while (_injected.TryDequeue(out var item))
        {
            if (item.Silent) _tracker.ApplySilently([item.Event]);
            else ApplyTracked(item.Event);
            if (item.Event.Event == "SessionStart") (started ??= []).Add((item.Event.SessionId, item.Silent));
        }
        if (started is null || TokenSource is null) return;
        foreach (var (sessionId, silent) in started.Distinct())
            if (_tracker.Sessions.FirstOrDefault(s => s.SessionId == sessionId) is { } session) RefreshTokens(session, silent);
    }

    /// <summary>Records the sessions the hook bridge reports: the registry feed must not drive them as well.</summary>
    private void NoteHookSessions(IEnumerable<HookEvent> events)
    {
        foreach (var ev in events)
        {
            var key = (ev.Agent, ev.SessionId);
            if (!_hookSessions.TryGetValue(key, out var at) || ev.Ts > at) _hookSessions[key] = ev.Ts;
        }
    }

    /// <summary>
    /// Applies what the Claude session registry says: adopts the sessions no hook reports and follows their status,
    /// reads the tokens of the ones it just adopted (an idle session is otherwise never visited), and ties every
    /// tracked Claude session to the process named by its record.
    /// </summary>
    private void SyncClaudeRegistry(bool silent)
    {
        if (ClaudeRegistry is null) return;
        var cutoff = _clock.UtcNow - SessionProcessRegistry.EndedMemory;
        foreach (var old in _hookSessions.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList()) _hookSessions.Remove(old);

        var sync = ClaudeRegistry.Sync(_tracker.Sessions, id => _hookSessions.ContainsKey((AgentKind.Claude, id)));
        if (silent) _tracker.ApplySilently(sync.Events);
        else foreach (var e in sync.Events) ApplyTracked(e);

        // At start-up only: a replayed session whose record was left behind by a process that no longer exists (the
        // terminal was closed while the app was not running, the machine rebooted) is over. Later on, a session gets
        // its process bound while it runs, and Processes watches it; here a resumed session could still be starting.
        if (silent && sync.Stale.Count > 0)
        {
            var stale = sync.Stale.ToHashSet(StringComparer.Ordinal);
            foreach (var session in _tracker.Sessions.Where(s => s.Agent == AgentKind.Claude && stale.Contains(s.SessionId)
                         && s.Origin is not (SessionOrigin.Cloud or SessionOrigin.Routine)))
            {
                Info($"Sessione {session.Agent} {session.SessionId} chiusa: il suo processo e' terminato mentre l'app era chiusa");
                _tracker.ApplySilently([new HookEvent(_clock.UtcNow, session.Agent, "SessionEnd", session.SessionId, null, null, null, "process_exited")]);
            }
        }

        var sessions = _tracker.Sessions.Where(s => s.Agent == AgentKind.Claude).ToDictionary(s => s.SessionId, StringComparer.Ordinal);
        // Not at start-up: Start runs on the UI thread, and the first Pump fills every session, adopted ones included.
        if (!silent)
            foreach (var adopted in sync.Events.Where(e => e.Event == "SessionStart").Select(e => e.SessionId).Distinct())
                if (sessions.TryGetValue(adopted, out var session)) RefreshTokens(session);
        if (Processes is null) return;
        foreach (var live in sync.Live)
            if (sessions.ContainsKey(live.SessionId))
                Processes.Bind(AgentKind.Claude, live.SessionId, live.Pid, BindingSource.Registry, live.StartedAtFileTime);
    }

    /// <summary>Records the Codex sessions whose subagents the hook bridge itself is reporting.</summary>
    private void NoteHookSubagents(IEnumerable<HookEvent> events)
    {
        foreach (var ev in events)
        {
            if (ev.Agent != AgentKind.Codex || ev.Event is not ("SubagentStart" or "SubagentStop")) continue;
            if (!_hookSubagents.TryGetValue(ev.SessionId, out var at) || ev.Ts > at) _hookSubagents[ev.SessionId] = ev.Ts;
        }
    }

    private bool HooksReportSubagents(string sessionId, DateTimeOffset now) =>
        _hookSubagents.TryGetValue(sessionId, out var at) && now - at < CodexHookGrace;

    /// <summary>
    /// Stops the children this pump had announced for a session the fallback no longer owns (its hooks took over, or
    /// the setting went off). Leaving them running would pin the session to "al lavoro" until the 30-minute timeout.
    /// A child the hooks now report under the same id (its type is no longer the fallback's) is theirs and is left alone:
    /// stopping it would end a thread that is still at work.
    /// </summary>
    private void ReleaseSynthesisedChildren(SessionState session, DateTimeOffset now, bool silent)
    {
        if (!_synthesisedChildren.Remove(session.SessionId, out var announced) || announced.Count == 0) return;
        var events = announced.Keys
            .Where(child => session.Subagents?.FirstOrDefault(s => s.AgentId == child) is not { } tracked
                            || tracked.AgentType == CodexSubagentScanner.SyntheticAgentType)
            .Select(child => SyntheticSubagentEvent("SubagentStop", session, child, now))
            .ToList();
        if (events.Count == 0) return;
        if (silent) _tracker.ApplySilently(events);
        else foreach (var e in events) ApplyTracked(e);
    }

    /// <summary>
    /// Follows, turn by turn, the Codex children a session already knows (reported by its hooks, or announced by the
    /// fallback before the hooks took over) from the newest turn event of each child's own rollout. A child is a
    /// thread that receives many turns, and Codex sends one SubagentStart for its whole life and a SubagentStop at the
    /// end of most, not all, of its turns: the hooks alone would keep it finished while it works on its next turn, and
    /// running after a turn whose stop never came. The hooks stay the fast signal and the rollout decides: a finished
    /// child whose newest turn started after its end is started again, a running child whose newest turn is over is
    /// stopped. A child without a readable rollout, or whose rollout is not a spawned child (a guardian thread), is
    /// left to the hooks. Live scans only: the startup replay must not read today's rollouts into history.
    /// </summary>
    /// <remarks>
    /// A running child whose rollout still shows the turn open is announced again once half the subagent timeout has
    /// passed since its last announcement, as the fallback does for its own children: the sweep judges these children
    /// by the session's newest subagent event, and a single turn can outlast the timeout. The events for a child the
    /// startup replay left running are Quiet on the first live scan: what they report happened before the app was
    /// looking.
    /// </remarks>
    /// <param name="followSynthesised">
    /// False while the fallback setting is off: a child only the rollouts ever reported is not brought back.
    /// </param>
    private void FollowKnownChildren(string sessionId, bool followSynthesised)
    {
        var session = _tracker.Sessions.FirstOrDefault(s => s.Agent == AgentKind.Codex && s.SessionId == sessionId);
        if (session?.Subagents is not { Count: > 0 } known) return;
        var now = _clock.UtcNow;
        var refreshAfter = SubagentTimeout / 2;
        if (!_followedChildren.TryGetValue(sessionId, out var announced))
            _followedChildren[sessionId] = announced = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var gone in announced.Keys.Where(id => !known.Any(s => s.AgentId == id)).ToList()) announced.Remove(gone);
        var replayed = _replayedChildren?.GetValueOrDefault(sessionId);
        var events = new List<HookEvent>();
        foreach (var child in known)
        {
            if (!followSynthesised && child.AgentType == CodexSubagentScanner.SyntheticAgentType) continue;
            if (CodexSubagents!.ChildInfo(child.AgentId) is not { StateAt: { } at } info) continue;
            var quiet = replayed?.Contains(child.AgentId) ?? false;
            // Strictly newer than the end, with no tolerance: the next turn can start 0.3 s after the hook's
            // SubagentStop (measured), while the task_started of the turn that stop closed is minutes older.
            if (child.Phase == SubagentPhase.Done && info.State == CodexTurnState.Running && at > (child.EndedAt ?? child.StartedAt))
            {
                // Stamped with the scan, like the fallback's announcements: the proof of life the timeout sweep reads
                // must be fresh, or a turn found running long after it began would be released at the next sweep.
                events.Add(RolloutChildEvent("SubagentStart", session, child, now, quiet));
                announced[child.AgentId] = now;
            }
            else if (child.Phase == SubagentPhase.Running && info.State == CodexTurnState.Finished)
                // Stamped with the end of the turn (never before the child started, never in the future): the row
                // shows how long the turn really took, and a task_started written right after it is newer than it.
                events.Add(RolloutChildEvent("SubagentStop", session, child, at < child.StartedAt ? child.StartedAt : at > now ? now : at, quiet));
            else if (child.Phase == SubagentPhase.Running && info.State == CodexTurnState.Running
                     && now - LastAnnounced(announced, child) >= refreshAfter)
            {
                // A turn longer than the timeout: the refresh keeps StartedAt (UpsertSubagent does, for a running agent)
                // and renews the proof of life the sweep reads.
                events.Add(RolloutChildEvent("SubagentStart", session, child, now, quiet));
                announced[child.AgentId] = now;
            }
        }
        // ApplyTracked, as for the fallback: the stop of the last child may end the session's turn, and the totals must
        // be read before the row stops being refreshed.
        foreach (var e in events) ApplyTracked(e);
    }

    /// <summary>
    /// The last time a followed child was announced: this pump's own announcement, or its start (a hook's
    /// SubagentStart, a restart) when that is newer.
    /// </summary>
    private static DateTimeOffset LastAnnounced(Dictionary<string, DateTimeOffset> announced, SubagentState child) =>
        announced.TryGetValue(child.AgentId, out var at) && at > child.StartedAt ? at : child.StartedAt;

    /// <summary>The SubagentStart/SubagentStop a child's rollout implies: its own id and type, source "rollout".</summary>
    private static HookEvent RolloutChildEvent(string name, SessionState session, SubagentState child, DateTimeOffset ts, bool quiet) =>
        new(ts, session.Agent, name, session.SessionId, session.Cwd, null, null, "rollout", child.AgentId, child.AgentType, Quiet: quiet);

    private static bool IsRunning(SessionState session, string agentId) =>
        session.Subagents?.Any(s => s.AgentId == agentId && s.Phase == SubagentPhase.Running) ?? false;

    private static HookEvent SyntheticSubagentEvent(string name, SessionState session, string childThreadId, DateTimeOffset ts) =>
        new(ts, session.Agent, name, session.SessionId, session.Cwd, null, null, null,
            childThreadId, CodexSubagentScanner.SyntheticAgentType);

    /// <summary>
    /// Ends the sessions whose agent process is gone, as a <c>SessionEnd</c> would: removed from the notch, their
    /// transcript state and terminal released by the Removed change. <paramref name="silent"/> is the startup sweep
    /// right after the replay, which raises nothing and needs no second look.
    /// </summary>
    private void EndDeadSessions(bool silent)
    {
        if (Processes is null) return;
        var ended = Processes.FindEnded(_tracker.Sessions, confirm: !silent);
        foreach (var session in ended)
        {
            Info($"Sessione {session.Agent} {session.SessionId} chiusa: il processo dell'agente non esiste piu'");
            var end = new HookEvent(_clock.UtcNow, session.Agent, "SessionEnd", session.SessionId, null, null, null, "process_exited");
            if (silent) _tracker.ApplySilently([end]);
            else _tracker.Apply(end);
        }
        Processes.Prune(_tracker.Sessions);
    }

    private void Info(string message)
    {
        try { OnInfo?.Invoke(message); } catch { /* a broken logger must not take the pump down */ }
    }

    /// <summary>The token source's view of subagent activity for the timeout sweep, null without a source.</summary>
    private Func<SessionState, SubagentState, DateTimeOffset?>? SubagentActivity() =>
        TokenSource is { } source ? source.SubagentLastActivity : null;

    private void Report(Exception ex)
    {
        try { OnError?.Invoke(ex); } catch { /* a broken logger must not take the pump down */ }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _poll?.Dispose();
    }
}
