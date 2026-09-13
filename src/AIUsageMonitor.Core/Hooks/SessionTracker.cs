using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

public enum SessionChangeKind { Added, Updated, Removed }

public sealed record SessionChange(SessionChangeKind Kind, SessionState Session, SessionPhase? PreviousPhase);

/// <summary>State machine per (agent, session id). Thread-safe; Changed fires outside the lock on the caller's thread.</summary>
public sealed class SessionTracker
{
    public const int MaxMessageLength = 120;

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

        var cwd = e.Cwd ?? existing?.Cwd ?? ResolveCwd(e.Agent, e.SessionId);
        var updated = new SessionState(
            e.Agent, e.SessionId, DisplayNameFor(cwd, e.SessionId), cwd,
            phase.Value, message, e.Ts, existing?.StartedAt ?? e.Ts);
        _sessions[key] = updated;
        return new SessionChange(existing is null ? SessionChangeKind.Added : SessionChangeKind.Updated, updated, existing?.Phase);
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
