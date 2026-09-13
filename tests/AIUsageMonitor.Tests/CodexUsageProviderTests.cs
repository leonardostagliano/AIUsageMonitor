using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Usage;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CodexUsageProviderTests
{
    // 1789806273 = 2026-09-19T00:24:33Z (the reset in the fixture); "now" is one day earlier.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1789806273).AddDays(-1);
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static CodexUsageProvider Build(TempDir dir, params (string RelativePath, string Content, DateTime LastWriteUtc)[] files)
    {
        foreach (var (rel, content, lastWrite) in files)
        {
            var full = dir.File(Path.Combine(".codex", "sessions", rel), content);
            File.SetLastWriteTimeUtc(full, lastWrite);
        }
        return new CodexUsageProvider(new AppPaths(dir.Path, dir.Sub("lad")), new FakeClock(Now));
    }

    [Fact]
    public void FindLatestRateLimits_returns_the_newest_token_count_and_ignores_decoys()
    {
        var lines = Fixture("codex-session.jsonl").Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse();
        var rl = CodexUsageProvider.FindLatestRateLimits(lines);
        Assert.NotNull(rl);
        Assert.Equal("codex", rl!.LimitId);
        Assert.Equal(92, rl.Primary!.UsedPercent);
        Assert.Null(rl.Secondary);
        Assert.Equal("prolite", rl.PlanType);
    }

    [Theory]
    [InlineData(300, "5h")]
    [InlineData(60, "1h")]
    [InlineData(1440, "1g")]
    [InlineData(10080, "7g")]
    [InlineData(0, "?")]
    public void LabelFor_window_minutes(int minutes, string expected) => Assert.Equal(expected, CodexUsageProvider.LabelFor(minutes));

    [Fact]
    public void ResolveWindow_keeps_future_reset_and_percent()
    {
        var w = new CodexRateWindow { UsedPercent = 92, WindowMinutes = 10080, ResetsAt = 1789806273 };
        var window = CodexUsageProvider.ResolveWindow(w, "", Now);
        Assert.Equal("7g", window.Label);
        Assert.Equal(92, window.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789806273), window.ResetsAt);
        Assert.Equal(Severity.Critical, window.Severity);
    }

    [Fact]
    public void ResolveWindow_advances_a_stale_reset_and_zeroes_the_percent()
    {
        var stale = Now.AddMinutes(-700).ToUnixTimeSeconds();
        var w = new CodexRateWindow { UsedPercent = 60, WindowMinutes = 300, ResetsAt = stale };
        var window = CodexUsageProvider.ResolveWindow(w, "", Now);
        Assert.Equal(0, window.Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(stale).AddMinutes(900), window.ResetsAt);
        Assert.True(window.ResetsAt > Now);
    }

    [Fact]
    public async Task Fetch_uses_the_newest_file_and_reports_plan()
    {
        using var dir = new TempDir();
        var older = Fixture("codex-session.jsonl").Replace("\"used_percent\":92", "\"used_percent\":50");
        var provider = Build(dir,
            (@"2026\09\11\rollout-old.jsonl", older, Now.UtcDateTime.AddDays(-2)),
            (@"2026\09\12\rollout-new.jsonl", Fixture("codex-session.jsonl"), Now.UtcDateTime.AddHours(-1)));

        var snap = await provider.FetchAsync();

        Assert.Equal(UsageStatus.Ok, snap.Status);
        Assert.Equal("Prolite", snap.PlanLabel);
        var w = Assert.Single(snap.Windows);
        Assert.Equal("7g", w.Label);
        Assert.Equal(92, w.Percent);
    }

    [Fact]
    public async Task Fetch_ignores_files_older_than_the_lookback()
    {
        using var dir = new TempDir();
        var provider = Build(dir, (@"2026\08\01\rollout-ancient.jsonl", Fixture("codex-session.jsonl"), Now.UtcDateTime.AddDays(-30)));
        var snap = await provider.FetchAsync();
        Assert.Equal(UsageStatus.NoData, snap.Status);
        Assert.Equal("Nessuna sessione Codex recente", snap.StatusMessage);
    }

    [Fact]
    public async Task Fetch_reports_missing_codex_dir()
    {
        using var dir = new TempDir();
        var provider = new CodexUsageProvider(new AppPaths(dir.Path, dir.Sub("lad")), new FakeClock(Now));
        Assert.Equal(UsageStatus.NoData, (await provider.FetchAsync()).Status);
    }

    [Fact]
    public async Task Fetch_prefixes_labels_when_several_limit_ids_exist()
    {
        using var dir = new TempDir();
        var other = Fixture("codex-session.jsonl").Replace("\"limit_id\":\"codex\"", "\"limit_id\":\"codex-plus\"");
        var provider = Build(dir,
            (@"2026\09\12\rollout-a.jsonl", Fixture("codex-session.jsonl"), Now.UtcDateTime.AddHours(-1)),
            (@"2026\09\12\rollout-b.jsonl", other, Now.UtcDateTime.AddHours(-2)));
        var snap = await provider.FetchAsync();
        Assert.Equal(new[] { "codex 7g", "codex-plus 7g" }, snap.Windows.Select(w => w.Label).ToArray());
    }

    [Theory]
    [InlineData(1789806273000L)]   // milliseconds mistaken for seconds
    [InlineData(-62135596801L)]    // below DateTimeOffset.MinValue
    [InlineData(253402300800L)]    // above DateTimeOffset.MaxValue
    public void ResolveWindow_ignores_an_out_of_range_reset(long resetsAt)
    {
        var w = new CodexRateWindow { UsedPercent = 92, WindowMinutes = 10080, ResetsAt = resetsAt };
        var window = CodexUsageProvider.ResolveWindow(w, "", Now);
        Assert.Null(window.ResetsAt);
        Assert.Equal(92, window.Percent);
        Assert.Equal("7g", window.Label);
    }
}
