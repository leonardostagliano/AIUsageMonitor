using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

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
    /// <summary>
    /// Child thread ids this pump has announced per Codex session, with the instant of the announcement: only these
    /// are ever stopped here, and the instant says when a still-running child must be announced again so the
    /// subagent timeout cannot release a child the scanner can plainly see is alive.
    /// </summary>
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _synthesisedChildren = new(StringComparer.Ordinal);

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

    /// <summary>Where unexpected failures go (the App wires a FileLogger): the pump never lets one escape a thread-pool callback.</summary>
    public Action<Exception>? OnError { get; init; }

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
                var replayed = _reader.ReadAll(_clock.UtcNow - ReplayWindow);
                _tracker.ApplySilently(replayed);
                _tracker.RemoveStaleSilently(StaleAfter);
                // A session whose subagents stopped reporting before the app started must not come back as
                // "al lavoro · N agenti": release them here too, silently, so the replay toasts nothing.
                _tracker.SweepSubagentTimeoutsSilently(SubagentTimeout);
                // A Codex child thread that is running right now must be picked up before the first Changed is
                // raised, or the replayed session would flip Idle → Working → Idle and toast a turn it never ran.
                SyncCodexSubagents(silent: true);
                _lastStaleSweep = _clock.UtcNow;
                _lastCodexScan = _clock.UtcNow;
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
                ApplyBatch(_reader.ReadNew().ToList());
                _reader.RotateIfNeeded();
                if (_clock.UtcNow - _lastCodexScan >= CodexScanEvery)
                {
                    _lastCodexScan = _clock.UtcNow;
                    SyncCodexSubagents(silent: false);
                }
                if (_clock.UtcNow - _lastStaleSweep >= StaleSweepEvery)
                {
                    _lastStaleSweep = _clock.UtcNow;
                    _tracker.RemoveStale(StaleAfter);
                    // Same cadence as the stale removal, on the sessions that survived it: a subagent that died
                    // without a SubagentStop would otherwise pin its session to "al lavoro" until the 12 h sweep.
                    _tracker.SweepSubagentTimeouts(SubagentTimeout);
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
    /// Applies a batch of events, announcing the live Codex child threads immediately before the Stop that would
    /// otherwise be applied with no subagent in sight. Codex does not emit SubagentStart for its children, so a Stop
    /// applied before the scan takes the session to Idle and toasts "Turno completato" while a child is still
    /// running; the row would then flip back to "al lavoro" on the next scan and toast a second time at the real end.
    /// The scan runs per session and at most once per batch for each of them (it already covers every session it
    /// knows), so a session created by an earlier event of this very batch is covered too.
    /// </summary>
    private void ApplyBatch(IReadOnlyList<HookEvent> batch)
    {
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
            _tracker.Apply(ev);
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

        var now = _clock.UtcNow;
        // Half the timeout: a child still running at that point is announced again, which refreshes
        // LastSubagentEventAt, so SweepSubagentTimeouts can only release children the scanner no longer sees.
        var refreshAfter = SubagentTimeout / 2;
        foreach (var session in sessions)
        {
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
            else foreach (var e in events) _tracker.Apply(e);
        }
    }

    private static bool IsRunning(SessionState session, string agentId) =>
        session.Subagents?.Any(s => s.AgentId == agentId && s.Phase == SubagentPhase.Running) ?? false;

    private static HookEvent SyntheticSubagentEvent(string name, SessionState session, string childThreadId, DateTimeOffset ts) =>
        new(ts, session.Agent, name, session.SessionId, session.Cwd, null, null, null,
            childThreadId, CodexSubagentScanner.SyntheticAgentType);

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
