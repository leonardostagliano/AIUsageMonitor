using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Usage;

public sealed class UsageService
{
    private readonly Dictionary<AgentKind, IUsageProvider> _providers;
    private readonly UsageCache _cache;
    private readonly IClock _clock;
    private readonly object _gate = new();
    private readonly Dictionary<AgentKind, UsageSnapshot> _current;

    /// <summary>Raised on the thread that completed the refresh (never the UI thread).</summary>
    public event Action<UsageSnapshot>? UsageUpdated;

    /// <summary>Where a cache write failure or a throwing UsageUpdated subscriber is reported (the App wires a FileLogger).</summary>
    public Action<Exception>? OnError { get; init; }

    public UsageService(IEnumerable<IUsageProvider> providers, UsageCache cache, IClock clock)
    {
        _providers = providers.ToDictionary(p => p.Agent);
        _cache = cache;
        _clock = clock;
        _current = _cache.Load();
        foreach (var (agent, snapshot) in _current.ToList())
            _current[agent] = snapshot with { Status = UsageStatus.Stale, StatusMessage = StaleMessage(snapshot.FetchedAt) };
    }

    public IReadOnlyDictionary<AgentKind, UsageSnapshot> Current
    {
        get { lock (_gate) return new Dictionary<AgentKind, UsageSnapshot>(_current); }
    }

    public async Task RefreshAsync(AgentKind agent, CancellationToken cancellationToken = default)
    {
        if (!_providers.TryGetValue(agent, out var provider)) return;

        UsageSnapshot fresh;
        try
        {
            fresh = await provider.FetchAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            fresh = UsageSnapshot.Empty(agent, UsageStatus.Error, ex.Message, _clock.UtcNow);
        }

        UsageSnapshot merged;
        lock (_gate)
        {
            _current.TryGetValue(agent, out var previous);
            merged = Merge(previous, fresh);
            _current[agent] = merged;
            if (fresh.Status == UsageStatus.Ok)
            {
                try { _cache.Save(_current.Values.Where(s => s.Windows.Count > 0)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(ex); }
            }
        }
        Raise(merged);
    }

    /// <summary>Raises UsageUpdated; one throwing subscriber is reported and never stops the others or the refresh loop.</summary>
    private void Raise(UsageSnapshot snapshot)
    {
        foreach (var handler in UsageUpdated?.GetInvocationList() ?? [])
        {
            try { ((Action<UsageSnapshot>)handler)(snapshot); }
            catch (Exception ex) { Report(ex); }
        }
    }

    private void Report(Exception ex)
    {
        try { OnError?.Invoke(ex); } catch { /* a broken logger must not take the refresh down */ }
    }

    /// <summary>A failed fetch keeps the previous windows: Error becomes Stale, other failure statuses keep their message.</summary>
    public static UsageSnapshot Merge(UsageSnapshot? previous, UsageSnapshot fresh)
    {
        if (fresh.Status == UsageStatus.Ok || previous is null || previous.Windows.Count == 0) return fresh;
        if (fresh.Status == UsageStatus.Error)
            return previous with { Status = UsageStatus.Stale, StatusMessage = StaleMessage(previous.FetchedAt) };
        return previous with { Status = fresh.Status, StatusMessage = fresh.StatusMessage };
    }

    public static string StaleMessage(DateTimeOffset fetchedAt) => $"Ultimo aggiornamento {fetchedAt.ToLocalTime():HH:mm}";
}
