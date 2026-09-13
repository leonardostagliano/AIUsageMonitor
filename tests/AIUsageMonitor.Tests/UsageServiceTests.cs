using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Usage;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class UsageServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private sealed class StubProvider : IUsageProvider
    {
        public StubProvider(AgentKind agent) => Agent = agent;
        public AgentKind Agent { get; }
        public Func<UsageSnapshot> Next { get; set; } = () => throw new InvalidOperationException("not configured");
        public int Calls { get; private set; }
        public Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default) { Calls++; return Task.FromResult(Next()); }
    }

    private static UsageSnapshot Ok(AgentKind agent, double pct, DateTimeOffset at) =>
        new(agent, [new UsageWindow("5h", pct, at.AddHours(2), SeverityRules.FromPercent(pct))], "Max 20x", null, UsageStatus.Ok, null, at);

    [Fact]
    public void Merge_keeps_previous_windows_when_fresh_fetch_failed()
    {
        var previous = Ok(AgentKind.Claude, 48, Now);
        var failed = UsageSnapshot.Empty(AgentKind.Claude, UsageStatus.Error, "HTTP 500", Now.AddMinutes(1));
        var merged = UsageService.Merge(previous, failed);
        Assert.Equal(UsageStatus.Stale, merged.Status);
        Assert.Equal(48, merged.Windows.Single().Percent);
        Assert.Equal(UsageService.StaleMessage(Now), merged.StatusMessage);
    }

    [Fact]
    public void Merge_keeps_previous_windows_with_token_expired_message()
    {
        var previous = Ok(AgentKind.Claude, 48, Now);
        var expired = UsageSnapshot.Empty(AgentKind.Claude, UsageStatus.TokenExpired, ClaudeUsageProvider.TokenExpiredMessage, Now);
        var merged = UsageService.Merge(previous, expired);
        Assert.Equal(UsageStatus.TokenExpired, merged.Status);
        Assert.Equal(ClaudeUsageProvider.TokenExpiredMessage, merged.StatusMessage);
        Assert.Single(merged.Windows);
    }

    [Fact]
    public void Merge_returns_fresh_when_ok_or_when_nothing_previous()
    {
        var fresh = Ok(AgentKind.Codex, 90, Now);
        Assert.Same(fresh, UsageService.Merge(Ok(AgentKind.Codex, 10, Now.AddHours(-1)), fresh));
        var failed = UsageSnapshot.Empty(AgentKind.Codex, UsageStatus.Error, "x", Now);
        Assert.Same(failed, UsageService.Merge(null, failed));
    }

    [Fact]
    public async Task Refresh_updates_current_raises_event_and_persists_cache()
    {
        using var dir = new TempDir();
        var cache = new UsageCache(Path.Combine(dir.Path, "usage-cache.json"));
        var claude = new StubProvider(AgentKind.Claude) { Next = () => Ok(AgentKind.Claude, 48, Now) };
        var service = new UsageService([claude], cache, new FakeClock(Now));
        UsageSnapshot? raised = null;
        service.UsageUpdated += s => raised = s;

        await service.RefreshAsync(AgentKind.Claude);

        Assert.Equal(48, service.Current[AgentKind.Claude].Windows.Single().Percent);
        Assert.Equal(48, raised!.Windows.Single().Percent);
        var reloaded = cache.Load();
        Assert.Equal(48, reloaded[AgentKind.Claude].Windows.Single().Percent);
        Assert.Equal("Max 20x", reloaded[AgentKind.Claude].PlanLabel);
    }

    [Fact]
    public async Task Service_starts_from_cache_marked_stale_and_failed_refresh_keeps_it()
    {
        using var dir = new TempDir();
        var cache = new UsageCache(Path.Combine(dir.Path, "usage-cache.json"));
        cache.Save([Ok(AgentKind.Claude, 33, Now.AddHours(-1))]);
        var claude = new StubProvider(AgentKind.Claude) { Next = () => UsageSnapshot.Empty(AgentKind.Claude, UsageStatus.Error, "Rete non disponibile", Now) };
        var service = new UsageService([claude], cache, new FakeClock(Now));

        Assert.Equal(UsageStatus.Stale, service.Current[AgentKind.Claude].Status);
        await service.RefreshAsync(AgentKind.Claude);
        Assert.Equal(UsageStatus.Stale, service.Current[AgentKind.Claude].Status);
        Assert.Equal(33, service.Current[AgentKind.Claude].Windows.Single().Percent);
    }

    [Fact]
    public async Task Provider_exceptions_become_error_snapshots()
    {
        using var dir = new TempDir();
        var claude = new StubProvider(AgentKind.Claude) { Next = () => throw new InvalidOperationException("boom") };
        var service = new UsageService([claude], new UsageCache(Path.Combine(dir.Path, "c.json")), new FakeClock(Now));
        await service.RefreshAsync(AgentKind.Claude);
        Assert.Equal(UsageStatus.Error, service.Current[AgentKind.Claude].Status);
        Assert.Equal("boom", service.Current[AgentKind.Claude].StatusMessage);
    }

    [Fact]
    public async Task Scheduler_polls_on_interval_and_refreshes_on_demand()
    {
        using var dir = new TempDir();
        var claude = new StubProvider(AgentKind.Claude) { Next = () => Ok(AgentKind.Claude, 1, Now) };
        var service = new UsageService([claude], new UsageCache(Path.Combine(dir.Path, "c.json")), new FakeClock(Now));
        using var scheduler = new UsageScheduler(service, _ => TimeSpan.FromMilliseconds(80));

        scheduler.Start([AgentKind.Claude]);
        await Task.Delay(300);
        Assert.InRange(claude.Calls, 2, 10);

        var before = claude.Calls;
        scheduler.RefreshNow(AgentKind.Claude);
        await Task.Delay(50);
        Assert.True(claude.Calls > before);
    }

    [Fact]
    public async Task Scheduler_skips_disabled_agents()
    {
        using var dir = new TempDir();
        var claude = new StubProvider(AgentKind.Claude) { Next = () => Ok(AgentKind.Claude, 1, Now) };
        var service = new UsageService([claude], new UsageCache(Path.Combine(dir.Path, "c.json")), new FakeClock(Now));
        using var scheduler = new UsageScheduler(service, _ => null);
        scheduler.Start([AgentKind.Claude]);
        await Task.Delay(150);
        Assert.Equal(0, claude.Calls);
    }

    [Fact]
    public async Task A_throwing_usage_subscriber_is_reported_and_the_snapshot_survives()
    {
        using var dir = new TempDir();
        var claude = new StubProvider(AgentKind.Claude) { Next = () => Ok(AgentKind.Claude, 7, Now) };
        var errors = new List<Exception>();
        var service = new UsageService([claude], new UsageCache(Path.Combine(dir.Path, "c.json")), new FakeClock(Now)) { OnError = errors.Add };
        service.UsageUpdated += _ => throw new InvalidOperationException("cross-thread WPF access");

        await service.RefreshAsync(AgentKind.Claude);

        Assert.Equal(7, service.Current[AgentKind.Claude].Windows.Single().Percent);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
    }

    [Fact]
    public async Task Scheduler_survives_a_throwing_interval_selector_and_reports_it()
    {
        using var dir = new TempDir();
        var claude = new StubProvider(AgentKind.Claude) { Next = () => Ok(AgentKind.Claude, 1, Now) };
        var service = new UsageService([claude], new UsageCache(Path.Combine(dir.Path, "c.json")), new FakeClock(Now));
        var errors = new List<Exception>();
        var calls = 0;
        using var scheduler = new UsageScheduler(service, _ =>
            Interlocked.Increment(ref calls) == 2 ? throw new InvalidOperationException("settings not loaded") : TimeSpan.FromMilliseconds(30))
        {
            OnError = errors.Add
        };

        scheduler.Start([AgentKind.Claude]);
        for (var i = 0; i < 100 && errors.Count == 0; i++) await Task.Delay(20);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));

        // The loop is still alive after the failure.
        var before = claude.Calls;
        scheduler.RefreshNow(AgentKind.Claude);
        for (var i = 0; i < 100 && claude.Calls == before; i++) await Task.Delay(20);
        Assert.True(claude.Calls > before);
    }
}
