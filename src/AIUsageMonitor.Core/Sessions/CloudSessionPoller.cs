using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Sessions;

/// <summary>
/// Reads the cloud sessions on its own background loop and hands the resulting events to the pump, which applies them
/// on its thread like the hook lines. The first read after start-up (or after the option is switched back on) is
/// applied silently, so what was already going on in the cloud is shown without toasts; when the option goes off the
/// sessions on show are ended. A rejected token or an unavailable endpoint waits <see cref="BackoffAfterRejection"/>.
/// </summary>
public sealed class CloudSessionPoller : IDisposable
{
    private readonly Func<CancellationToken, Task<CloudFetchResult>> _fetch;
    private readonly CloudSessionFeed _feed;
    private readonly HookEventPump _pump;
    private readonly IClock _clock;
    private readonly Func<TimeSpan?> _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private CancellationTokenSource _wake = new();
    private bool _first = true;
    private string? _lastOutcome;

    /// <param name="interval">Time between two reads; null while the cloud sessions are switched off. Read at every turn.</param>
    public CloudSessionPoller(Func<CancellationToken, Task<CloudFetchResult>> fetch, CloudSessionFeed feed, HookEventPump pump,
        IClock clock, Func<TimeSpan?> interval)
    {
        _fetch = fetch;
        _feed = feed;
        _pump = pump;
        _clock = clock;
        _interval = interval;
    }

    public TimeSpan BackoffAfterRejection { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often a switched-off poller looks at the option again.</summary>
    public TimeSpan OffCheckEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>One line when the outcome of the reads changes (ok, rejected, unavailable); the App wires the log.</summary>
    public Action<string>? OnInfo { get; init; }

    public Action<Exception>? OnError { get; init; }

    /// <summary>Raised after a read changed the sessions, silent reads included (the UI refreshes on it).</summary>
    public event Action? Applied;

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>Reads again right away (refresh button, option changed).</summary>
    public void PollNow()
    {
        lock (_gate) _wake.Cancel();
    }

    /// <summary>One turn of the loop: a read, or the clear-out when switched off. Returns the wait before the next.</summary>
    public async Task<TimeSpan> PollOnceAsync(CancellationToken cancellationToken)
    {
        if (_interval() is not { } interval)
        {
            if (_feed.HasSessions) Apply(_feed.Clear(_clock.UtcNow), silent: false);
            _first = true;
            return OffCheckEvery;
        }

        var result = await _fetch(cancellationToken).ConfigureAwait(false);
        var outcome = result.Detail is null ? result.Status.ToString() : $"{result.Status} ({result.Detail})";
        if (outcome != _lastOutcome) Info($"Sessioni cloud: {outcome}");
        _lastOutcome = outcome;

        if (result.Status == CloudFetchStatus.Ok)
        {
            Apply(_feed.Diff(result.Sessions, result.RoutinesKnown, _clock.UtcNow), silent: _first);
            _first = false;
            return interval;
        }
        return result.Status is CloudFetchStatus.Unauthorized or CloudFetchStatus.Unavailable && interval < BackoffAfterRejection
            ? BackoffAfterRejection
            : interval;
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                delay = await PollOnceAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let the loop die: a bug in a read would otherwise stop the cloud sessions for good.
                Report(ex);
                delay = TimeSpan.FromMinutes(1);
            }

            CancellationTokenSource wake;
            lock (_gate)
            {
                if (_wake.IsCancellationRequested)
                {
                    _wake.Dispose();
                    _wake = new CancellationTokenSource();
                }
                wake = _wake;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, wake.Token);
            try
            {
                await Task.Delay(delay, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (_stop.IsCancellationRequested) return;
            }
        }
    }

    private void Apply(IReadOnlyList<HookEvent> events, bool silent)
    {
        if (events.Count == 0) return;
        _pump.Inject(events, silent);
        _pump.Pump();
        try { Applied?.Invoke(); }
        catch (Exception ex) { Report(ex); }
    }

    private void Info(string message)
    {
        try { OnInfo?.Invoke(message); } catch { /* a broken logger must not stop the loop */ }
    }

    private void Report(Exception ex)
    {
        try { OnError?.Invoke(ex); } catch { /* a broken logger must not stop the loop */ }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate) _wake.Cancel();
    }
}
