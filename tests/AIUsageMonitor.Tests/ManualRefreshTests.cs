using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Usage;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ManualRefreshTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A provider whose fetches block until the test releases them, one gate per call.</summary>
    private sealed class GatedProvider : IUsageProvider
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource> _pending = [];
        public AgentKind Agent => AgentKind.Claude;
        public int Calls { get; private set; }
        public int Completed { get; private set; }

        public async Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default)
        {
            TaskCompletionSource release;
            lock (_gate)
            {
                Calls++;
                release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending.Add(release);
            }
            await release.Task.WaitAsync(cancellationToken);
            lock (_gate) Completed++;
            return new UsageSnapshot(AgentKind.Claude, [], null, null, UsageStatus.Ok, null, Now);
        }

        public void ReleaseAll()
        {
            lock (_gate)
            {
                foreach (var pending in _pending) pending.TrySetResult();
                _pending.Clear();
            }
        }
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 250 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    [Fact]
    public async Task RefreshNowAsync_waits_for_a_refresh_that_starts_after_the_click()
    {
        using var dir = new TempDir();
        var provider = new GatedProvider();
        var service = new UsageService([provider], new UsageCache(Path.Combine(dir.Path, "c.json")), new FakeClock(Now));
        using var scheduler = new UsageScheduler(service, _ => TimeSpan.FromHours(1));
        scheduler.Start([AgentKind.Claude]);
        await Eventually(() => provider.Calls == 1); // the periodic refresh at start is in flight

        var click = scheduler.RefreshNowAsync(AgentKind.Claude);
        provider.ReleaseAll();
        await Eventually(() => provider.Calls == 2);
        Assert.False(click.IsCompleted); // the refresh that was running when the user clicked does not count

        provider.ReleaseAll();
        await click.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, provider.Completed);
    }

    [Fact]
    public async Task RefreshNowAsync_completes_at_once_for_a_disabled_agent_and_is_cancelled_by_dispose()
    {
        using var dir = new TempDir();
        var provider = new GatedProvider();
        var service = new UsageService([provider], new UsageCache(Path.Combine(dir.Path, "c.json")), new FakeClock(Now));
        using (var disabled = new UsageScheduler(service, _ => null))
            Assert.True(disabled.RefreshNowAsync(AgentKind.Claude).IsCompleted);

        var scheduler = new UsageScheduler(service, _ => TimeSpan.FromHours(1)); // never started: nobody will refresh
        var pending = scheduler.RefreshNowAsync(AgentKind.Claude);
        scheduler.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class CountingSource : ITokenSource
    {
        private int _calls;
        public List<string> Reads { get; } = [];
        public TokenUsage? SessionTokens(SessionState session)
        {
            lock (Reads) Reads.Add(session.SessionId);
            // A new total at every read, so every refresh is a change the tracker reports.
            return new TokenUsage(Interlocked.Increment(ref _calls), 1, 0, 0);
        }
        public IReadOnlyDictionary<string, TokenUsage>? SubagentTokens(SessionState session) => null;
    }

    private static string Line(string evt, string sid, DateTimeOffset ts, string agent) =>
        $$"""{"ts":"{{ts:yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"{{agent}}","event":"{{evt}}","session_id":"{{sid}}","cwd":"C:\\demo\\proj","notification_type":null,"message":null,"source":null}""" + "\n";

    [Fact]
    public async Task RefreshTokensNowAsync_reads_every_session_of_the_agent_idle_ones_included()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(Now);
        var tracker = new SessionTracker(clock);
        var source = new CountingSource();
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            TokenRefreshEvery = TimeSpan.FromHours(1),
            TokenSource = source
        };
        pump.Start();
        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "idle", Now, "claude") + Line("Stop", "idle", Now.AddSeconds(1), "claude") +
            Line("UserPromptSubmit", "codex", Now, "codex"));
        pump.Pump();
        lock (source.Reads) source.Reads.Clear();
        var changes = new List<SessionChange>();
        tracker.Changed += c => { lock (changes) changes.Add(c); };

        await pump.RefreshTokensNowAsync(AgentKind.Claude).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(["idle"], source.Reads);
        Assert.Single(changes);
    }

    [Fact]
    public async Task Gate_accepts_one_refresh_at_a_time_then_cools_down()
    {
        var time = new ManualTimeProvider();
        var gate = new ManualRefreshGate(time, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));
        var states = 0;
        gate.StateChanged += () => states++;
        var work = new TaskCompletionSource();

        var first = gate.TryRunAsync(() => work.Task);
        Assert.True(gate.IsRefreshing);
        Assert.False(gate.CanStart);
        Assert.False(await gate.TryRunAsync(() => Task.CompletedTask)); // ignored while refreshing

        work.SetResult();
        Assert.True(await first);
        Assert.False(gate.IsRefreshing);
        Assert.Equal(time.GetUtcNow(), gate.LastCompletedAt);
        Assert.Equal(TimeSpan.FromSeconds(10), gate.CooldownRemaining);
        Assert.False(await gate.TryRunAsync(() => Task.CompletedTask)); // ignored while cooling down
        Assert.Equal(2, states);

        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(gate.CanStart);
        Assert.True(await gate.TryRunAsync(() => Task.CompletedTask));
    }

    [Fact]
    public async Task Gate_releases_the_button_after_the_timeout_and_reports_failures()
    {
        var time = new ManualTimeProvider();
        var gate = new ManualRefreshGate(time, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(15));

        var hung = gate.TryRunAsync(() => new TaskCompletionSource().Task);
        time.Advance(TimeSpan.FromSeconds(15));
        Assert.True(await hung.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(gate.IsRefreshing);

        time.Advance(TimeSpan.FromSeconds(10));
        var errors = new List<Exception>();
        Assert.True(await gate.TryRunAsync(() => throw new InvalidOperationException("boom"), errors.Add));
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        Assert.False(gate.IsRefreshing);
    }
}
