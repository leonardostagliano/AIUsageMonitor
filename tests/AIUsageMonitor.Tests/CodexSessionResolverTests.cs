using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CodexSessionResolverTests
{
    private const string Meta = """{"timestamp":"2026-09-12T19:20:46.540Z","type":"session_meta","payload":{"session_id":"01a0970c-b9e6-7c90-b787-f95e9166e315","id":"01a09710-a719-7cf2-9e08-1d41fe3b1980","cwd":"C:\\Users\\demo\\Progetti\\Demo Azure","originator":"codex-tui"}}""";

    [Fact]
    public void Resolves_cwd_by_file_name_id()
    {
        using var dir = new TempDir();
        dir.File(@".codex\sessions\2026\09\12\rollout-2026-09-12T21-20-46-01a09710-a719-7cf2-9e08-1d41fe3b1980.jsonl", Meta + "\n{\"type\":\"other\"}\n");
        var resolver = new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions"));
        Assert.Equal(@"C:\Users\demo\Progetti\Demo Azure", resolver.ResolveCwd("01a09710-a719-7cf2-9e08-1d41fe3b1980"));
    }

    [Fact]
    public void Falls_back_to_session_meta_session_id_and_caches()
    {
        using var dir = new TempDir();
        var file = dir.File(@".codex\sessions\2026\09\12\rollout-x.jsonl", Meta + "\n");
        var resolver = new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions"));
        Assert.Equal(@"C:\Users\demo\Progetti\Demo Azure", resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315"));
        File.Delete(file);
        Assert.Equal(@"C:\Users\demo\Progetti\Demo Azure", resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315"));
    }

    [Fact]
    public void Returns_null_when_unknown_or_dir_missing()
    {
        using var dir = new TempDir();
        Assert.Null(new CodexSessionResolver(Path.Combine(dir.Path, "missing")).ResolveCwd("abc"));
        dir.File(@".codex\sessions\2026\09\12\rollout-y.jsonl", "not json\n");
        Assert.Null(new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions")).ResolveCwd("abc"));
    }

    [Fact]
    public void Reads_a_rollout_file_that_codex_keeps_open_for_append()
    {
        using var dir = new TempDir();
        var file = dir.File(@".codex\sessions\2026\09\12\rollout-2026-09-12T21-20-46-01a09710-a719-7cf2-9e08-1d41fe3b1980.jsonl", Meta + "\n");
        // Rust's std::fs default share mode: the live session file is open for append while Codex runs.
        using var live = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var resolver = new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions"));

        Assert.Equal(@"C:\Users\demo\Progetti\Demo Azure", resolver.ResolveCwd("01a09710-a719-7cf2-9e08-1d41fe3b1980"));
    }

    [Fact]
    public void An_unreadable_file_does_not_abort_the_scan()
    {
        using var dir = new TempDir();
        var locked = dir.File(@".codex\sessions\2026\09\12\rollout-locked.jsonl", "{}\n");
        dir.File(@".codex\sessions\2026\09\12\rollout-good.jsonl", Meta + "\n");
        File.SetLastWriteTimeUtc(locked, DateTime.UtcNow.AddMinutes(5)); // scanned first
        using var exclusive = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var resolver = new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions"));

        Assert.Equal(@"C:\Users\demo\Progetti\Demo Azure", resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315"));
    }

    [Fact]
    public void A_non_string_type_line_is_skipped_instead_of_throwing()
    {
        using var dir = new TempDir();
        dir.File(@".codex\sessions\2026\09\12\rollout-weird.jsonl", "{\"type\":42}\n" + Meta + "\n");
        var resolver = new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions"));

        Assert.Equal(@"C:\Users\demo\Progetti\Demo Azure", resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315"));
    }

    [Fact]
    public void An_invalid_session_id_degrades_to_null()
    {
        using var dir = new TempDir();
        dir.File(@".codex\sessions\2026\09\12\rollout-y.jsonl", Meta + "\n");
        var resolver = new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions"));

        Assert.Null(resolver.ResolveCwd("bad\0id"));
    }

    [Fact]
    public void Misses_are_cached_for_a_minute_and_then_retried()
    {
        using var dir = new TempDir();
        var sessions = dir.Sub(@".codex\sessions");
        var clock = new FakeClock(new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.Zero));
        var resolver = new CodexSessionResolver(sessions, clock);

        Assert.Null(resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315"));
        dir.File(@".codex\sessions\2026\09\12\rollout-late.jsonl", Meta + "\n");
        Assert.Null(resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315")); // still the cached miss

        clock.Advance(CodexSessionResolver.MissTtl + TimeSpan.FromSeconds(1));
        Assert.Equal(@"C:\Users\demo\Progetti\Demo Azure", resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315"));
    }
}
