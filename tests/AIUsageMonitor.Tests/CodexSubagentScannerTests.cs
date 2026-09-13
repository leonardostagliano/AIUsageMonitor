using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CodexSubagentScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private const string Parent = "01a0970c-b9e6-7c90-b787-f95e9166e315";
    private const string Child = "01a099e8-f531-7640-9c0c-8a20fe1a7aa9";
    private const string Sibling = "01a099e9-1b74-7ab3-84d1-0a4f0c9d7c11";

    private const string TaskStarted = "task_started";
    private const string TaskComplete = "task_complete";
    private const string TurnAborted = "turn_aborted";

    /// <summary>An assistant message that mentions the turn event names: it must not be mistaken for one.</summary>
    private const string Decoy = "decoy";

    private static string Stamp(DateTimeOffset ts) => ts.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    /// <summary>
    /// A session_meta in the shape Codex really writes: <c>payload.id</c> is the thread's own id (the same uuid the
    /// file name carries), while <c>payload.session_id</c> is the id of the root conversation, shared by every thread
    /// of the tree — so all the children of one parent carry the SAME session_id and differ only by their <c>id</c>.
    /// A root thread has <c>session_id == id</c> and no <c>parent_thread_id</c>.
    /// </summary>
    private static string Meta(string threadId, string? parentThreadId, DateTimeOffset ts, string? conversationId = null) =>
        $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"session_meta","payload":{"session_id":"{{{conversationId ?? parentThreadId ?? threadId}}}","id":"{{{threadId}}}","parent_thread_id":{{{(parentThreadId is null ? "null" : $"\"{parentThreadId}\"")}}},"cwd":"C:\\demo\\proj","originator":"codex-tui","cli_version":"0.154.0","thread_source":"{{{(parentThreadId is null ? "user" : "subagent")}}}"}}""";

    private static string Event(string kind, DateTimeOffset ts) => kind == Decoy
        ? $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"about to emit task_complete and turn_aborted"}]}}"""
        : $$$"""{"timestamp":"{{{Stamp(ts)}}}","type":"event_msg","payload":{"type":"{{{kind}}}","turn_id":"t1","started_at":1789241145}}""";

    /// <summary>
    /// Writes a rollout whose newest line carries <paramref name="lastActivity"/> as its timestamp (that is what the
    /// scanner reads to decide freshness) and whose directory entry is stamped with <paramref name="mtime"/>
    /// (the same instant by default; a different value simulates the stale NTFS entry of a file held open).
    /// </summary>
    private static string Rollout(TempDir dir, string fileName, string threadId, string? parentThreadId, string[] events,
        DateTimeOffset lastActivity, DateTimeOffset? mtime = null, string? conversationId = null)
    {
        var lines = new List<string> { Meta(threadId, parentThreadId, lastActivity.AddSeconds(-events.Length - 1), conversationId) };
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

    /// <summary>
    /// The headline case of the whole task: two children of the same parent running at the same time must be counted
    /// as two. They share <c>session_id</c> (the root conversation) and differ only by <c>payload.id</c>, so reading
    /// the thread id from <c>session_id</c> would collapse them onto one and "al lavoro · 2 agenti" could never render.
    /// </summary>
    [Fact]
    public void ActiveChildren_counts_two_concurrent_children_of_the_same_parent()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-58-00-{Parent}.jsonl", Parent, null, [TaskStarted], Now.AddSeconds(-10));
        Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddSeconds(-30));
        Rollout(dir, $"rollout-2026-09-13T11-58-40-{Sibling}.jsonl", Sibling, Parent, [TaskStarted, Decoy], Now.AddSeconds(-20));

        var scanner = Build(dir);

        Assert.Equal([Child, Sibling], scanner.ActiveChildren(Parent).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(2, scanner.CountActiveChildren(Parent));
    }

    /// <summary>
    /// A grandchild carries the same root <c>session_id</c> as its parent's siblings but a different
    /// <c>parent_thread_id</c>: it belongs to its own parent, not to the root of the conversation.
    /// </summary>
    [Fact]
    public void ActiveChildren_attributes_a_grandchild_to_its_own_parent()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-58-30-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddSeconds(-30));
        Rollout(dir, $"rollout-2026-09-13T11-58-45-{Sibling}.jsonl", Sibling, Child, [TaskStarted], Now.AddSeconds(-20),
            conversationId: Parent);

        var scanner = Build(dir);

        Assert.Equal(new[] { Child }, scanner.ActiveChildren(Parent));
        Assert.Equal(new[] { Sibling }, scanner.ActiveChildren(Child));
    }

    /// <summary>A rollout that names itself as its own parent is not a child of anything.</summary>
    [Fact]
    public void ActiveChildren_ignores_a_rollout_whose_parent_is_itself()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-58-00-{Parent}.jsonl", Parent, Parent, [TaskStarted], Now.AddSeconds(-10));

        Assert.Empty(Build(dir).ActiveChildren(Parent));
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

    /// <summary>
    /// A running child goes quiet for minutes at a time — a long exec or tool call appends nothing to its rollout
    /// while it runs (measured on this machine: gaps of several minutes inside turns that then completed normally).
    /// Reporting it as finished would take the session to Idle and toast "Turno completato" mid-turn, then toast a
    /// second time at the real end; the 30-minute subagent timeout is the backstop for a child that really died.
    /// </summary>
    [Fact]
    public void CountActiveChildren_counts_a_child_that_went_quiet_inside_an_open_turn()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-53-00-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddMinutes(-5));

        Assert.Equal(1, Build(dir).CountActiveChildren(Parent));
    }

    [Fact]
    public void CountActiveChildren_ignores_a_child_that_stopped_writing()
    {
        using var dir = new TempDir();
        Rollout(dir, $"rollout-2026-09-13T11-40-00-{Child}.jsonl", Child, Parent, [TaskStarted], Now.AddMinutes(-20));

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
            $$$"""{"type":"session_meta","payload":{"session_id":"{{{Parent}}}","id":"{{{Child}}}","parent_thread_id":"{{{Parent}}}"}}""" + "\n" +
            """{"type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}""" + "\n");
        File.SetLastWriteTimeUtc(full, Now.AddSeconds(-30).UtcDateTime);

        Assert.Equal(1, Build(dir).CountActiveChildren(Parent));

        File.SetLastWriteTimeUtc(full, Now.AddMinutes(-20).UtcDateTime);
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
