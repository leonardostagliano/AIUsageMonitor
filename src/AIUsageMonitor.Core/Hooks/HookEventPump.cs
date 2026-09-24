using System.Collections.Concurrent;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
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
                _lastStaleSweep = _clock.UtcNow;
                _lastCodexScan = _clock.UtcNow;
                _lastTokenRefresh = _clock.UtcNow;
                _lastLivenessSweep = _clock.UtcNow;
                // The replayed sessions have no totals yet and most of them are Idle, so neither the periodic pass
                // (Working/NeedsInput only) nor an event-driven refresh would ever visit them: the first Pump fills
                // them all once. Not here: Start() runs on the UI thread and the first read of a large transcript
                // parses it whole, which would freeze the notch at launch.
                _fillTokensOnFirstPump = TokenSource is not null;
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
                if (TokenSource is not null && _clock.UtcNow - _lastTokenRefresh >= TokenRefreshEvery)
                {
                    _lastTokenRefresh = _clock.UtcNow;
                    // Only the busy sessions: an Idle transcript is not growing any more, and its last total was
                    // already read by the Stop (or the last SubagentStop) that ended the turn.
                    foreach (var session in _tracker.Sessions.Where(s => s.Phase is SessionPhase.Working or SessionPhase.NeedsInput))
                        RefreshTokens(session);
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
    /// Applies one event and, when it is the end of a turn or of a subagent, reads the token totals right away: those
    /// sessions leave the Working/NeedsInput set the periodic refresh visits, so this is the last chance to record
    /// what the turn really cost before the row goes quiet.
    /// </summary>
    private void ApplyTracked(HookEvent ev)
    {
        _tracker.Apply(ev);
        if (TokenSource is null || ev.Event is not ("Stop" or "StopFailure" or "SubagentStop")) return;
        var session = _tracker.Sessions.FirstOrDefault(s => s.Agent == ev.Agent && s.SessionId == ev.SessionId);
        if (session is not null) RefreshTokens(session);
    }

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
    /// stopped here: a child reported by a real Codex hook stays under the hook's control.
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

        var enabled = CodexSubagentsEnabled?.Invoke() ?? true;
        var now = _clock.UtcNow;
        // Half the timeout: a child still running at that point is announced again, which refreshes
        // LastSubagentEventAt, so SweepSubagentTimeouts can only release children the scanner no longer sees.
        var refreshAfter = SubagentTimeout / 2;
        foreach (var session in sessions)
        {
            // Two producers for one child would double it: the fallback stands down for a session whose hooks report
            // subagents (and for all of them when the setting is off), releasing whatever it had announced.
            if (!enabled || HooksReportSubagents(session.SessionId, now))
            {
                ReleaseSynthesisedChildren(session, now, silent);
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
    /// </summary>
    private void ReleaseSynthesisedChildren(SessionState session, DateTimeOffset now, bool silent)
    {
        if (!_synthesisedChildren.Remove(session.SessionId, out var announced) || announced.Count == 0) return;
        var events = announced.Keys.Select(child => SyntheticSubagentEvent("SubagentStop", session, child, now)).ToList();
        if (silent) _tracker.ApplySilently(events);
        else foreach (var e in events) ApplyTracked(e);
    }

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
