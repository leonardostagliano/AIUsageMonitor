using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CodexTokenCounterTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private const string Parent = "01a0970c-b9e6-7c90-b787-f95e9166e315";
    private const string Child = "01a099e8-f531-7640-9c0c-8a20fe1a7aa9";
    private const string Sibling = "01a099e9-1b74-7ab3-84d1-0a4f0c9d7c11";
    private const string Stranger = "01a099ea-2c85-7bc4-95e2-1b5b1d0e8d22";

    private static string Stamp(DateTimeOffset ts) => ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// A session_meta as Codex writes it: <c>payload.id</c> is the thread's own id (the uuid the file name carries)
    /// and <c>payload.session_id</c> is the root conversation, shared by every thread of the tree.
    /// </summary>
    private static string Meta(string threadId, string? parentThreadId, DateTimeOffset ts, string? conversationId = null) =>
        $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"session_meta","payload":{"session_id":"{{{conversationId ?? parentThreadId ?? threadId}}}","id":"{{{threadId}}}","parent_thread_id":{{{(parentThreadId is null ? "null" : $"\"{parentThreadId}\"")}}},"cwd":"C:\\demo\\proj","originator":"codex-tui","cli_version":"0.154.0"}}""";

    /// <summary>A token_count event carrying the cumulative usage of the thread (the shape read from a real rollout).</summary>
    private static string TokenCount(DateTimeOffset ts, long input, long cached, long cacheWrite, long output) =>
        $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":{{{input}}},"cached_input_tokens":{{{cached}}},"cache_write_input_tokens":{{{cacheWrite}}},"output_tokens":{{{output}}},"reasoning_output_tokens":7,"total_tokens":{{{input + output + cacheWrite}}}}},"rate_limits":null}}""";

    /// <summary>Codex emits token_count without info while a turn is being aborted: it carries no totals.</summary>
    private static string TokenCountWithoutInfo(DateTimeOffset ts) =>
        $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":null}}""";

    /// <summary>An assistant message that quotes the event name: it must not be mistaken for a token_count.</summary>
    private static string Decoy(DateTimeOffset ts) =>
        $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"about to emit token_count with total_token_usage"}]}}""";

    private static string Rollout(TempDir dir, string fileName, string threadId, string? parentThreadId, string[] lines,
        DateTimeOffset? mtime = null, string? conversationId = null)
    {
        var all = new List<string> { Meta(threadId, parentThreadId, Now.AddMinutes(-10), conversationId) };
        all.AddRange(lines);
        var full = dir.File(Path.Combine("sessions", "2026", "09", "13", fileName), string.Join("\n", all) + "\n");
        File.SetLastWriteTimeUtc(full, (mtime ?? Now).UtcDateTime);
        return full;
    }

    private static string FileNameFor(string threadId) => $"rollout-2026-09-13T11-58-00-{threadId}.jsonl";

    private static CodexTokenCounter Build(TempDir dir, FakeClock? clock = null) =>
        new(Path.Combine(dir.Path, "sessions"), clock ?? new FakeClock(Now));

    [Fact]
    public void ReadThread_maps_the_newest_total_token_usage_of_the_rollout()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Parent), Parent, null,
        [
            TokenCount(Now.AddMinutes(-5), 100, 60, 5, 20),
            Decoy(Now.AddMinutes(-4)),
            TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)
        ]);

        var tokens = Build(dir).ReadThread(Parent);

        // input_tokens includes the cached ones: the cached share moves to CacheRead so the total stays total_tokens.
        Assert.Equal(new TokenUsage(400, 200, 600, 50), tokens);
        Assert.Equal(1250, tokens.Total);
    }

    [Fact]
    public void ReadThread_skips_a_token_count_without_info()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Parent), Parent, null,
        [
            TokenCount(Now.AddMinutes(-5), 1000, 600, 50, 200),
            TokenCountWithoutInfo(Now.AddMinutes(-1))
        ]);

        Assert.Equal(new TokenUsage(400, 200, 600, 50), Build(dir).ReadThread(Parent));
    }

    [Fact]
    public void ReadThread_finds_the_rollout_by_session_meta_when_the_file_name_does_not_carry_the_id()
    {
        using var dir = new TempDir();
        Rollout(dir, "rollout-2026-09-13T11-58-00-renamed.jsonl", Parent, null,
            [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);

        Assert.Equal(new TokenUsage(400, 200, 600, 50), Build(dir).ReadThread(Parent));
    }

    [Fact]
    public void ReadThread_falls_back_to_the_conversation_id_of_the_session_meta()
    {
        using var dir = new TempDir();
        // A resumed thread: its own id differs from the session_id the hook reports.
        Rollout(dir, "rollout-2026-09-13T11-58-00-resumed.jsonl", Child, null,
            [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)], conversationId: Parent);

        Assert.Equal(new TokenUsage(400, 200, 600, 50), Build(dir).ReadThread(Parent));
    }

    [Fact]
    public void ReadThread_returns_zero_for_an_unknown_thread_a_missing_directory_and_a_rollout_without_token_count()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Parent), Parent, null, [Decoy(Now.AddMinutes(-3))]);

        var counter = Build(dir);

        Assert.Equal(TokenUsage.Zero, counter.ReadThread(Parent));
        Assert.Equal(TokenUsage.Zero, counter.ReadThread(Stranger));
        Assert.Equal(TokenUsage.Zero, counter.ReadThread(string.Empty));
        Assert.Equal(TokenUsage.Zero, new CodexTokenCounter(Path.Combine(dir.Path, "nope"), new FakeClock(Now)).ReadThread(Parent));
    }

    [Fact]
    public void ReadThread_caches_a_miss_for_the_cache_ttl()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Now);
        var counter = Build(dir, clock);

        Assert.Equal(TokenUsage.Zero, counter.ReadThread(Parent));

        Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);

        // Still inside the TTL: the directory is not scanned again.
        clock.Advance(CodexTokenCounter.CacheTtl - TimeSpan.FromSeconds(1));
        Assert.Equal(TokenUsage.Zero, counter.ReadThread(Parent));

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(new TokenUsage(400, 200, 600, 50), counter.ReadThread(Parent));
    }

    [Fact]
    public void ReadThread_sees_the_tokens_appended_after_the_first_call()
    {
        using var dir = new TempDir();
        var counter = Build(dir);
        var file = Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-5), 100, 60, 5, 20)]);

        Assert.Equal(new TokenUsage(40, 20, 60, 5), counter.ReadThread(Parent));

        File.AppendAllText(file, TokenCount(Now.AddMinutes(-1), 1000, 600, 50, 200) + "\n");

        Assert.Equal(new TokenUsage(400, 200, 600, 50), counter.ReadThread(Parent));
    }

    [Fact]
    public void ReadChildren_returns_the_threads_whose_parent_is_the_session_with_their_own_totals()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-3), 9000, 0, 0, 900)]);
        var childFile = Rollout(dir, FileNameFor(Child), Child, Parent,
            [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)], mtime: Now.AddMinutes(-2));
        Rollout(dir, FileNameFor(Sibling), Sibling, Parent,
            [TokenCount(Now.AddMinutes(-3), 10, 0, 0, 5)], mtime: Now.AddMinutes(-4));
        Rollout(dir, FileNameFor(Stranger), Stranger, Sibling, [TokenCount(Now.AddMinutes(-3), 77, 0, 0, 7)]);

        var children = Build(dir).ReadChildren(Parent);

        Assert.Equal(2, children.Count);
        var child = Assert.Single(children, c => c.ThreadId == Child);
        Assert.Equal(new TokenUsage(400, 200, 600, 50), child.Tokens);
        Assert.Equal(File.GetLastWriteTimeUtc(childFile), child.LastWriteUtc);
        Assert.Equal(new TokenUsage(10, 5, 0, 0), Assert.Single(children, c => c.ThreadId == Sibling).Tokens);
    }

    [Fact]
    public void ReadChildren_ignores_a_thread_that_names_itself_as_its_parent()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Parent), Parent, Parent, [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);

        Assert.Empty(Build(dir).ReadChildren(Parent));
    }

    [Fact]
    public void ReadChildren_ignores_rollouts_older_than_the_child_window()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Child), Child, Parent,
            [TokenCount(Now.AddDays(-8), 1000, 600, 50, 200)], mtime: Now.AddDays(-8));

        Assert.Empty(Build(dir).ReadChildren(Parent));
    }

    [Fact]
    public void ReadChildren_returns_empty_for_a_missing_directory_and_an_empty_parent()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Child), Child, Parent, [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);

        Assert.Empty(Build(dir).ReadChildren(string.Empty));
        Assert.Empty(new CodexTokenCounter(Path.Combine(dir.Path, "nope"), new FakeClock(Now)).ReadChildren(Parent));
    }

    [Fact]
    public void ReadChildren_sees_a_child_that_appears_after_the_first_call()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Now);
        var counter = Build(dir, clock);
        Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-3), 9000, 0, 0, 900)]);

        Assert.Empty(counter.ReadChildren(Parent));

        Rollout(dir, FileNameFor(Child), Child, Parent, [TokenCount(Now.AddMinutes(-1), 1000, 600, 50, 200)]);
        clock.Advance(TimeSpan.FromSeconds(5));

        // The list of children is not cached: only the per-file metadata is, and a new file has none.
        var child = Assert.Single(counter.ReadChildren(Parent));
        Assert.Equal(Child, child.ThreadId);
        Assert.Equal(new TokenUsage(400, 200, 600, 50), child.Tokens);
    }

    /// <summary>Holds a rollout the way an antivirus scan or a backup does: every read of it then fails.</summary>
    private static FileStream Exclusive(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.None);

    [Fact]
    public void ReadThread_keeps_the_last_known_total_when_the_rollout_stops_being_readable()
    {
        using var dir = new TempDir();
        var counter = Build(dir);
        var file = Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);

        Assert.True(counter.TryReadThread(Parent, out var first));
        Assert.Equal(new TokenUsage(400, 200, 600, 50), first);

        using (Exclusive(file))
        {
            // The count must not fall to zero because one sweep could not open the file.
            Assert.False(counter.TryReadThread(Parent, out var locked));
            Assert.Equal(new TokenUsage(400, 200, 600, 50), locked);
            Assert.Equal(new TokenUsage(400, 200, 600, 50), counter.ReadThread(Parent));
        }

        Assert.Equal(new TokenUsage(400, 200, 600, 50), counter.ReadThread(Parent));
    }

    [Fact]
    public void TryReadThread_reports_a_thread_without_a_rollout_as_a_conclusive_zero()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);
        var counter = Build(dir);

        Assert.True(counter.TryReadThread(Stranger, out var tokens));
        Assert.Equal(TokenUsage.Zero, tokens);
        Assert.True(counter.TryReadThread(string.Empty, out var none));
        Assert.Equal(TokenUsage.Zero, none);
    }

    [Fact]
    public void ReadThread_does_not_cache_a_miss_caused_by_an_unreadable_rollout()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(Now);
        var counter = Build(dir, clock);
        // The file name does not carry the id: the thread can only be found through its session_meta.
        var file = Rollout(dir, "rollout-2026-09-13T11-58-00-renamed.jsonl", Parent, null,
            [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);

        using (Exclusive(file))
        {
            Assert.False(counter.TryReadThread(Parent, out var tokens));
            Assert.Equal(TokenUsage.Zero, tokens);
        }

        // Well inside the TTL: the failed scan must not be remembered as "this thread has no rollout".
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new TokenUsage(400, 200, 600, 50), counter.ReadThread(Parent));
    }

    [Fact]
    public void ReadChildren_keeps_the_last_known_total_of_a_child_that_stops_being_readable()
    {
        using var dir = new TempDir();
        var counter = Build(dir);
        Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-3), 9000, 0, 0, 900)]);
        var childFile = Rollout(dir, FileNameFor(Child), Child, Parent,
            [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);

        Assert.Equal(new TokenUsage(400, 200, 600, 50), Assert.Single(counter.ReadChildren(Parent)).Tokens);

        using (Exclusive(childFile))
        {
            // Its session_meta is still cached, so the child is still known: only its totals could not be refreshed.
            Assert.True(counter.TryReadChildren(Parent, out var children));
            var child = Assert.Single(children);
            Assert.Equal(Child, child.ThreadId);
            Assert.Equal(new TokenUsage(400, 200, 600, 50), child.Tokens);
        }
    }

    [Fact]
    public void TryReadChildren_reports_a_sweep_that_could_not_read_every_candidate_as_incomplete()
    {
        using var dir = new TempDir();
        var counter = Build(dir);
        Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-3), 9000, 0, 0, 900)]);
        Rollout(dir, FileNameFor(Sibling), Sibling, Parent, [TokenCount(Now.AddMinutes(-3), 10, 0, 0, 5)]);
        var childFile = Rollout(dir, FileNameFor(Child), Child, Parent,
            [TokenCount(Now.AddMinutes(-3), 1000, 600, 50, 200)]);

        using (Exclusive(childFile))
        {
            Assert.False(counter.TryReadChildren(Parent, out var children));
            // What could be established is still returned: the caller merges it with the children it knows.
            Assert.Equal(Sibling, Assert.Single(children).ThreadId);
        }

        Assert.True(counter.TryReadChildren(Parent, out var complete));
        Assert.Equal(2, complete.Count);
    }

    [Fact]
    public void TryReadChildren_reports_a_normal_sweep_as_complete()
    {
        using var dir = new TempDir();
        Rollout(dir, FileNameFor(Parent), Parent, null, [TokenCount(Now.AddMinutes(-3), 9000, 0, 0, 900)]);

        Assert.True(Build(dir).TryReadChildren(Parent, out var children));
        Assert.Empty(children);
        Assert.True(Build(dir).TryReadChildren(string.Empty, out var none));
        Assert.Empty(none);
    }
}
