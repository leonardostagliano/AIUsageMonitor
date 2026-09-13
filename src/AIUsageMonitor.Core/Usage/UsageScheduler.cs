using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Usage;

/// <summary>One refresh loop per agent. intervalFor returning null means "disabled, check again in 5 s".</summary>
public sealed class UsageScheduler : IDisposable
{
    private static readonly TimeSpan DisabledPoll = TimeSpan.FromSeconds(5);

    private readonly UsageService _service;
    private readonly Func<AgentKind, TimeSpan?> _intervalFor;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<AgentKind, SemaphoreSlim> _wakeups = new();

    public UsageScheduler(UsageService service, Func<AgentKind, TimeSpan?> intervalFor)
    {
        _service = service;
        _intervalFor = intervalFor;
    }

    public void Start(IEnumerable<AgentKind> agents)
    {
        foreach (var agent in agents) _ = RunLoopAsync(agent);
    }

    /// <summary>Wakes the agent loop now; the signal is remembered if the loop is busy refreshing.</summary>
    public void RefreshNow(AgentKind agent)
    {
        var wakeup = Wakeup(agent);
        if (wakeup.CurrentCount > 0) return;
        try { wakeup.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    private SemaphoreSlim Wakeup(AgentKind agent)
    {
        lock (_wakeups)
        {
            if (!_wakeups.TryGetValue(agent, out var wakeup)) _wakeups[agent] = wakeup = new SemaphoreSlim(0, 1);
            return wakeup;
        }
    }

    private async Task RunLoopAsync(AgentKind agent)
    {
        var wakeup = Wakeup(agent);
        while (!_cts.IsCancellationRequested)
        {
            var interval = _intervalFor(agent);
            if (interval is not null)
            {
                try { await _service.RefreshAsync(agent, _cts.Token).ConfigureAwait(false); }
                catch (Exception) { /* RefreshAsync already converts failures; never let the loop die */ }
            }

            try { await wakeup.WaitAsync(interval ?? DisabledPoll, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
