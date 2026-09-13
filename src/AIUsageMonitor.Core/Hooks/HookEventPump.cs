using AIUsageMonitor.Core.Infrastructure;

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

    public TimeSpan ReplayWindow { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan StaleAfter { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan StaleSweepEvery { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a session waits for a subagent that never sent its SubagentStop before being released.</summary>
    public TimeSpan SubagentTimeout { get; init; } = TimeSpan.FromMinutes(30);

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
                _lastStaleSweep = _clock.UtcNow;
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
                foreach (var ev in _reader.ReadNew()) _tracker.Apply(ev);
                _reader.RotateIfNeeded();
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
