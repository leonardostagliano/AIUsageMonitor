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

    /// <summary>Where loop failures are reported (the App wires a FileLogger): a loop is never allowed to die silently.</summary>
    public Action<Exception>? OnError { get; init; }

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
            TimeSpan? interval;
            try { interval = _intervalFor(agent); }
            catch (Exception ex) { Report(ex); interval = null; } // treat a broken settings read as "disabled, retry soon"

            if (interval is not null)
            {
                try { await _service.RefreshAsync(agent, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Report(ex); } // RefreshAsync already converts failures; never let the loop die
            }

            try { await wakeup.WaitAsync(interval ?? DisabledPoll, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    private void Report(Exception ex)
    {
        try { OnError?.Invoke(ex); } catch { /* a broken logger must not take the loop down */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
