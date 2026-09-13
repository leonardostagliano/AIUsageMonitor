using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CodexSubagentScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
    private const string Parent = "01a0970c-b9e6-7c90-b787-f95e9166e315";
    private const string Child = "01a0970c-b9e6-7c90-b787-f95e9166e999";

    private static string Meta(string threadId, string? parentThreadId) =>
        $$$"""{"timestamp":"2026-09-13T11:58:00.000Z","type":"session_meta","payload":{"session_id":"{{{threadId}}}","parent_thread_id":{{{(parentThreadId is null ? "null" : $"\"{parentThreadId}\"")}}},"cwd":"C:\\demo\\proj","originator":"codex-tui","cli_version":"0.154.0"}}""";

    private const string TaskStarted = """{"timestamp":"2026-09-13T11:59:00.000Z","type":"event_msg","payload":{"type":"task_started","turn_id":"t1","started_at":1789241145}}""";
    private const string TaskComplete = """{"timestamp":"2026-09-13T11:59:30.000Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"t1","last_agent_message":"done"}}""";
    private const string TurnAborted = """{"timestamp":"2026-09-13T11:59:30.000Z","type":"event_msg","payload":{"type":"turn_aborted","turn_id":"t1","reason":"interrupted"}}""";

    /// <summary>An assistant message that mentions the turn event names: it must not be mistaken for one.</summary>
    private const string Decoy = """{"timestamp":"2026-09-13T11:59:20.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"about to emit task_complete and turn_aborted"}]}}""";

    private static string Rollout(TempDir dir, string fileName, string threadId, string? parentThreadId, string[] events, DateTimeOffset lastWrite)
    {
        var content = string.Join("\n", new[] { Meta(threadId, parentThreadId) }.Concat(events)) + "\n";
        var full = dir.File(Path.Combine("sessions", "2026", "09", "13", fileName), content);
        File.SetLastWriteTimeUtc(full, lastWrite.UtcDateTime);
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
            $$$"""{"timestamp":"2026-09-13T11:58:30.000Z","type":"session_meta","payload":{"cwd":"C:\\demo\\proj","parent_thread_id":"{{{Parent}}}"}}""" + "\n" + TaskStarted + "\n");
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
}
