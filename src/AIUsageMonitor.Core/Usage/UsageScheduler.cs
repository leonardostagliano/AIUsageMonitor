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
    private readonly Dictionary<AgentKind, List<TaskCompletionSource>> _waiters = new();

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

    /// <summary>
    /// Wakes the agent loop and completes when the NEXT refresh of that agent has finished — one that starts after
    /// this call, so the result reflects the click even when a periodic refresh was already running. Completes at once
    /// for a disabled agent; cancelled when the scheduler is disposed.
    /// </summary>
    public Task RefreshNowAsync(AgentKind agent)
    {
        TimeSpan? interval;
        try { interval = _intervalFor(agent); }
        catch (Exception ex) { Report(ex); interval = null; }
        if (interval is null) return Task.CompletedTask;

        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_waiters)
        {
            // Checked under the lock Dispose sweeps with: a click racing Dispose is either seen here or swept there.
            if (_cts.IsCancellationRequested) return Task.CompletedTask;
            if (!_waiters.TryGetValue(agent, out var list)) _waiters[agent] = list = [];
            list.Add(waiter);
        }
        RefreshNow(agent);
        return waiter.Task;
    }

    /// <summary>The clicks registered so far for <paramref name="agent"/>, handed to the refresh about to start.</summary>
    private List<TaskCompletionSource> TakeWaiters(AgentKind agent)
    {
        lock (_waiters)
        {
            if (!_waiters.TryGetValue(agent, out var list) || list.Count == 0) return [];
            _waiters[agent] = [];
            return list;
        }
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
                // Taken before the refresh starts: a click that lands while it runs waits for the next one.
                var waiters = TakeWaiters(agent);
                try { await _service.RefreshAsync(agent, _cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    foreach (var waiter in waiters) waiter.TrySetCanceled();
                    return;
                }
                catch (Exception ex) { Report(ex); } // RefreshAsync already converts failures; never let the loop die
                if (_cts.IsCancellationRequested)
                {
                    // RefreshAsync swallows the cancellation of its fetch: the refresh was cut short by Dispose.
                    foreach (var waiter in waiters) waiter.TrySetCanceled();
                    return;
                }
                foreach (var waiter in waiters) waiter.TrySetResult();
            }
            else
            {
                // Disabled meanwhile: nothing will refresh, release whoever was waiting.
                foreach (var waiter in TakeWaiters(agent)) waiter.TrySetResult();
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
        lock (_waiters)
        {
            foreach (var waiter in _waiters.Values.SelectMany(list => list)) waiter.TrySetCanceled();
            _waiters.Clear();
        }
        _cts.Dispose();
    }
}
