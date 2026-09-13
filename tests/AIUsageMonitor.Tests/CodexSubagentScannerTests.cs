using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CodexSubagentScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private const string Parent = "01a0970c-b9e6-7c90-b787-f95e9166e315";
    private const string Child = "01a0970c-b9e6-7c90-b787-f95e9166e999";

    private const string TaskStarted = "task_started";
    private const string TaskComplete = "task_complete";
    private const string TurnAborted = "turn_aborted";

    /// <summary>An assistant message that mentions the turn event names: it must not be mistaken for one.</summary>
    private const string Decoy = "decoy";

    private static string Stamp(DateTimeOffset ts) => ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    private static string Meta(string threadId, string? parentThreadId, DateTimeOffset ts) =>
        $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"session_meta","payload":{"session_id":"{{{threadId}}}","parent_thread_id":{{{(parentThreadId is null ? "null" : $"\"{parentThreadId}\"")}}},"cwd":"C:\\demo\\proj","originator":"codex-tui","cli_version":"0.154.0"}}""";

    private static string Event(string kind, DateTimeOffset ts) => kind == Decoy
        ? $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"about to emit task_complete and turn_aborted"}]}}"""
        : $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"event_msg","payload":{"type":"{{{kind}}}","turn_id":"t1","started_at":1789241145}}""";

    /// <summary>
    /// Writes a rollout whose newest line carries <paramref name="lastActivity"/> as its timestamp (that is what the
    /// scanner reads to decide freshness) and whose directory entry is stamped with <paramref name="mtime"/>
    /// (the same instant by default; a different value simulates the stale NTFS entry of a file held open).
    /// </summary>
    private static string Rollout(TempDir dir, string fileName, string threadId, string? parentThreadId, string[] events,
        DateTimeOffset lastActivity, DateTimeOffset? mtime = null)
    {
        var lines = new List<string> { Meta(threadId, parentThreadId, lastActivity.AddSeconds(-events.Length - 1)) };
        for (var i = 0; i < events.Length; i++)
            lines.Add(Event(events[i], lastActivity.AddSeconds(i - events.Length + 1)));
        var full = dir.File(Path.Combine("sessions", "2026", "09", "13", fileName), string.Join("\n", lines) + "\n");
        File.SetLastWriteTimeUtc(full, (mtime ?? lastActivity).UtcDateTime);
        return full;
    }

    private static CodexSubagentScanner Build(TempDir dir) =>
        new(Path.Combine(dir.Path, "sessions"), new FakeClock(Now));

    [Fact]
    public void ActiveChildren_returns_a_fresh_child_whose_last_turn_event_is_task_started()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-58-00-{Parent}.jsonl", Parent, null, [TaskStarted], Now.AddSeconds(-10));
        Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted, Decoy], Now.AddSeconds(-30));

        var scanner = Build(dir);

        Assert.Equal(new[] { Child }, scanner.ActiveChildren(Parent));
        Assert.Equal(1, scanner.CountActiveChildren(Parent));
    }

    [Fact]
    public void CountActiveChildren_ignores_a_child_that_completed_its_turn()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted, TaskComplete], Now.AddSeconds(-30));

        Assert.Equal(0, Build(dir).CountActiveChildren(Parent));
    }

    [Fact]
    public void CountActiveChildren_ignores_a_child_whose_turn_was_aborted()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted, TurnAborted], Now.AddSeconds(-30));

        Assert.Equal(0, Build(dir).CountActiveChildren(Parent));
    }

    [Fact]
    public void CountActiveChildren_counts_a_child_restarted_after_a_completed_turn()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted, TaskComplete, TaskStarted], Now.AddSeconds(-5));

        Assert.Equal(1, Build(dir).CountActiveChildren(Parent));
    }

    [Fact]
    public void CountActiveChildren_ignores_a_child_that_stopped_writing()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-50-00-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddMinutes(-5));

        Assert.Equal(0, Build(dir).CountActiveChildren(Parent));
    }

    /// <summary>
    /// Windows does not refresh the NTFS directory entry of a file a process keeps open, so the mtime the enumeration
    /// reports lags for the whole life of a Codex child thread. Freshness must therefore come from the newest line of
    /// the rollout itself, not from the cached directory entry.
    /// </summary>
    [Fact]
    public void CountActiveChildren_counts_a_child_whose_directory_entry_is_stale()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-40-00-{Child}.jsonl", Child, Parent, [TaskStarted],
            lastActivity: Now.AddSeconds(-20), mtime: Now.AddMinutes(-20));

        Assert.Equal(1, Build(dir).CountActiveChildren(Parent));
    }

    /// <summary>Freshness falls back to the file's own mtime when no line carries a parseable timestamp.</summary>
    [Fact]
    public void CountActiveChildren_falls_back_to_the_file_mtime_without_timestamps()
    {
        using var dir = new TempDir();
        var full = Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddSeconds(-30));
        File.WriteAllText(full,
            $$$"""{"type":"session_meta","payload":{"session_id":"{{{Child}}}","parent_thread_id":"{{{Parent}}}"}}""" + "\n" +
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}""" + "\n");
        File.SetLastWriteTimeUtc(full, Now.AddSeconds(-30).UtcDateTime);

        Assert.Equal(1, Build(dir).CountActiveChildren(Parent));

        File.SetLastWriteTimeUtc(full, Now.AddMinutes(-5).UtcDateTime);
        Assert.Equal(0, Build(dir).CountActiveChildren(Parent));
    }

    [Fact]
    public void CountActiveChildren_ignores_the_parent_and_the_children_of_other_threads()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-58-00-{Parent}.jsonl", Parent, null, [TaskStarted], Now.AddSeconds(-10));
        Rollout(dir, "rollout-2026-09-13T11-58-40-other-child.jsonl", "other-child", "another-thread", [TaskStarted], Now.AddSeconds(-10));

        Assert.Equal(0, Build(dir).CountActiveChildren(Parent));
    }

    [Fact]
    public void ActiveChildren_falls_back_to_the_thread_id_in_the_file_name()
    {
        using var dir = new TempDir();
        var full = Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddSeconds(-30));
        File.WriteAllText(full,
            $$$"""{"timestamp":"{{{Stamp(Now.AddSeconds(-35))}}}","type":"session_meta","payload":{"cwd":"C:\\demo\\proj","parent_thread_id":"{{{Parent}}}"}}""" + "\n" +
            Event(TaskStarted, Now.AddSeconds(-30)) + "\n");
        File.SetLastWriteTimeUtc(full, Now.AddSeconds(-30).UtcDateTime);

        Assert.Equal(new[] { Child }, Build(dir).ActiveChildren(Parent));
    }

    [Fact]
    public void ActiveChildren_survives_an_unreadable_or_malformed_rollout()
    {
        using var dir = new TempDir();
        var broken = dir.File(Path.Combine("sessions", "broken.jsonl"), "not json at all\n{\"type\":\n");
        File.SetLastWriteTimeUtc(broken, Now.AddSeconds(-10).UtcDateTime);
        Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddSeconds(-30));

        Assert.Equal(new[] { Child }, Build(dir).ActiveChildren(Parent));
    }

    [Fact]
    public void ActiveChildren_reads_a_rollout_held_open_by_a_live_session()
    {
        using var dir = new TempDir();
        var full = Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddSeconds(-30));
        using var held = new FileStream(full, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(new[] { Child }, Build(dir).ActiveChildren(Parent));
    }

    [Fact]
    public void CountActiveChildren_returns_zero_without_a_sessions_directory_or_a_parent_id()
    {
        using var dir = new TempDir();
        var scanner = new CodexSubagentScanner(Path.Combine(dir.Path, "missing"), new FakeClock(Now));

        Assert.Equal(0, scanner.CountActiveChildren(Parent));
        Assert.Empty(scanner.ActiveChildren(""));
    }

    /// <summary>A scan that could not enumerate the rollouts must be distinguishable from "this parent has no children".</summary>
    [Fact]
    public void TryGetActiveChildren_reports_whether_the_scan_succeeded()
    {
        using var dir = new TempDir();
        var missing = new CodexSubagentScanner(Path.Combine(dir.Path, "missing"), new FakeClock(Now));
        Assert.False(missing.TryGetActiveChildren(Parent, out var none));
        Assert.Empty(none);

        Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddSeconds(-30));
        Assert.True(Build(dir).TryGetActiveChildren(Parent, out var children));
        Assert.Equal(new[] { Child }, children);

        Assert.True(Build(dir).TryGetActiveChildren("", out var empty));
        Assert.Empty(empty);
    }

    /// <summary>
    /// A rollout enumerated between its creation and the first flush of its session_meta must not be remembered as
    /// "no parent" forever: that read is inconclusive, so the next scan reads the file again.
    /// </summary>
    [Fact]
    public void ActiveChildren_rereads_a_rollout_whose_session_meta_was_not_written_yet()
    {
        using var dir = new TempDir();
        var full = dir.File(Path.Combine("sessions", "2026", "09", "13", $"rollout-2026-09-13T11-58-30-{Child}.jsonl"), "");
        File.SetLastWriteTimeUtc(full, Now.AddSeconds(-40).UtcDateTime);
        var scanner = Build(dir);

        Assert.Empty(scanner.ActiveChildren(Parent));

        File.WriteAllText(full, Meta(Child, Parent, Now.AddSeconds(-35)) + "\n" + Event(TaskStarted, Now.AddSeconds(-30)) + "\n");
        File.SetLastWriteTimeUtc(full, Now.AddSeconds(-30).UtcDateTime);

        Assert.Equal(new[] { Child }, scanner.ActiveChildren(Parent));
    }

    /// <summary>A conclusive read (session_meta found) is cached: the immutable first line is not parsed again.</summary>
    [Fact]
    public void ActiveChildren_caches_a_conclusive_session_meta()
    {
        using var dir = new TempDir();
        var full = Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddSeconds(-30));
        var scanner = Build(dir);
        Assert.Equal(new[] { Child }, scanner.ActiveChildren(Parent));

        // The meta line is rewritten with a different parent: the cached (conclusive) meta still wins.
        File.WriteAllText(full, Meta(Child, "another-parent", Now.AddSeconds(-35)) + "\n" + Event(TaskStarted, Now.AddSeconds(-30)) + "\n");
        File.SetLastWriteTimeUtc(full, Now.AddSeconds(-30).UtcDateTime);

        Assert.Equal(new[] { Child }, scanner.ActiveChildren(Parent));
    }
}
