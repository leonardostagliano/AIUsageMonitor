namespace AIUsageMonitor.Tests.Helpers;

/// <summary>
/// <see cref="TimeProvider"/> finto: l'ora avanza solo con <see cref="Advance"/>, che fa scattare in ordine di scadenza i
/// timer scaduti, sul thread chiamante e fuori dal lock interno (un callback puo' creare o cambiare altri timer).
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;
    private long _sequence;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero)) { }

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _now.UtcTicks;
    }

    /// <summary>Timer creati, non eliminati e con una scadenza.</summary>
    public int ActiveTimers
    {
        get { lock (_gate) return _timers.Count(t => t.Due is not null); }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Porta avanti l'ora di <paramref name="by"/> facendo scattare, uno alla volta, i timer che scadono nel frattempo.</summary>
    public void Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_gate) target = _now + by;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers
                    .Where(t => t.Due is { } due && due <= target)
                    .OrderBy(t => t.Due)
                    .ThenBy(t => t.Order)
                    .FirstOrDefault();
                if (next is null)
                {
                    if (target > _now) _now = target;
                    return;
                }
                var fireAt = next.Due!.Value;
                if (fireAt > _now) _now = fireAt;
                if (next.Period > TimeSpan.Zero)
                {
                    next.Due = fireAt + next.Period;
                    next.Order = ++_sequence;
                }
                else
                {
                    next.Due = null;
                }
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        // Protetti dal lock del provider.
        public DateTimeOffset? Due { get; set; }
        public TimeSpan Period { get; set; }
        public long Order { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (!owner._timers.Contains(this)) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                Order = ++owner._sequence;
                return true;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                Due = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
