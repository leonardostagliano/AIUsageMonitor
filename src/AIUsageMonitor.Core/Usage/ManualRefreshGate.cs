namespace AIUsageMonitor.Core.Usage;

/// <summary>
/// State of a manual refresh button: one refresh at a time, a timeout after which the button comes back while the
/// work goes on in the background, and a cooldown before the next click is accepted. Thread-safe; StateChanged is
/// raised on the thread that changed the state.
/// </summary>
public sealed class ManualRefreshGate
{
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private bool _refreshing;
    private DateTimeOffset? _lastCompletedAt;
    private long? _completedTimestamp;

    public ManualRefreshGate(TimeProvider time, TimeSpan cooldown, TimeSpan timeout)
    {
        _time = time;
        Cooldown = cooldown;
        Timeout = timeout;
    }

    public TimeSpan Cooldown { get; }
    public TimeSpan Timeout { get; }

    public event Action? StateChanged;

    public bool IsRefreshing
    {
        get { lock (_gate) return _refreshing; }
    }

    public DateTimeOffset? LastCompletedAt
    {
        get { lock (_gate) return _lastCompletedAt; }
    }

    public TimeSpan CooldownRemaining
    {
        get { lock (_gate) return CooldownLeft(); }
    }

    public bool CanStart
    {
        get { lock (_gate) return !_refreshing && CooldownLeft() == TimeSpan.Zero; }
    }

    /// <summary>
    /// Runs <paramref name="refresh"/> when a click is accepted now; false (and nothing run) while refreshing or cooling
    /// down. Never throws: a failed or timed-out refresh still ends the wait and starts the cooldown.
    /// </summary>
    public async Task<bool> TryRunAsync(Func<Task> refresh, Action<Exception>? onError = null)
    {
        lock (_gate)
        {
            if (_refreshing || CooldownLeft() > TimeSpan.Zero) return false;
            _refreshing = true;
        }
        Raise();
        try
        {
            await refresh().WaitAsync(Timeout, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The refresh goes on in the background; the button comes back now.
        }
        catch (Exception ex)
        {
            try { onError?.Invoke(ex); } catch { /* a broken logger must not keep the button stuck */ }
        }
        finally
        {
            lock (_gate)
            {
                _refreshing = false;
                _lastCompletedAt = _time.GetUtcNow();
                _completedTimestamp = _time.GetTimestamp();
            }
            Raise();
        }
        return true;
    }

    /// <summary>
    /// On the monotonic clock: a wall clock set back (by hand, or by NTP after a resume) would stretch the cooldown by
    /// as much, one set forward would cut it short. <see cref="LastCompletedAt"/> keeps the wall time for the label.
    /// </summary>
    private TimeSpan CooldownLeft()
    {
        if (_completedTimestamp is not { } completed) return TimeSpan.Zero;
        var left = Cooldown - _time.GetElapsedTime(completed);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void Raise()
    {
        foreach (var handler in StateChanged?.GetInvocationList() ?? [])
        {
            try { ((Action)handler)(); } catch { /* a view model that throws must not break the gate */ }
        }
    }
}
