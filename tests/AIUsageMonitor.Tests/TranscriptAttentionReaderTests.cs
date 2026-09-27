using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Tests.Helpers;
using static AIUsageMonitor.Tests.Helpers.TranscriptLines;

namespace AIUsageMonitor.Tests;

public class TranscriptAttentionReaderTests
{
    private const string Cwd = @"C:\Users\demo\Progetti\Demo";

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Transcripts", name);

    private static PendingToolUse? Find(params string[] lines)
    {
        using var dir = new TempDir();
        return TranscriptAttentionReader.FindPending(dir.File("session.jsonl", Jsonl(lines)), Cwd);
    }

    [Fact]
    public void The_tool_use_left_without_a_result_is_the_pending_request()
    {
        var pending = Find(
            Prompt("lancia i test", 0),
            Text("m1", "Lancio i test.", 2),
            ToolUse("m1", "t1", "Bash", new { command = "dotnet test", description = "Run" }, 3));

        Assert.NotNull(pending);
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash", "dotnet test"), pending.Detail);
        Assert.Equal(At(3), pending.At);
    }

    [Fact]
    public void A_tool_use_with_its_result_is_not_pending()
    {
        Assert.Null(Find(
            Prompt("lancia i test", 0),
            ToolUse("m1", "t1", "Bash", new { command = "dotnet test" }, 3),
            Result("t1", 9)));
        // A refused permission is answered too: Claude Code writes an error result.
        Assert.Null(Find(
            ToolUse("m1", "t1", "Bash", new { command = "rm -rf build" }, 3),
            Result("t1", 9, isError: true)));
    }

    [Fact]
    public void A_question_waits_as_a_question()
    {
        var pending = Find(
            Prompt("prepara il rilascio", 0),
            ToolUse("m1", "t1", "AskUserQuestion", new
            {
                questions = new[] { new { question = "Pubblico anche la beta?" }, new { question = "Chi avviso?" } }
            }, 4));

        Assert.Equal(new AttentionDetail(AttentionKind.Question, null, "Pubblico anche la beta? (+1)"), pending!.Detail);
    }

    [Fact]
    public void Of_parallel_tool_uses_the_last_one_still_without_a_result_is_returned()
    {
        // One message split over three lines; the result of the first tool arrives between them.
        var onlyMiddle = Find(
            Prompt("controlla tutto", 0),
            ToolUse("m1", "t1", "Read", new { file_path = Cwd + @"\a.txt" }, 2),
            Result("t1", 3),
            ToolUse("m1", "t2", "Bash", new { command = "git status" }, 4),
            ToolUse("m1", "t3", "Edit", new { file_path = Cwd + @"\b.txt" }, 5),
            Result("t3", 6));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash", "git status"), onlyMiddle!.Detail);
        Assert.Equal(At(4), onlyMiddle.At);

        var twoOpen = Find(
            ToolUse("m1", "t1", "Bash", new { command = "git status" }, 4),
            ToolUse("m1", "t2", "Edit", new { file_path = Cwd + @"\b.txt" }, 5));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Edit", "b.txt"), twoOpen!.Detail);
    }

    [Fact]
    public void Only_the_latest_message_with_tool_uses_counts()
    {
        // m1 was abandoned without a result long ago; the latest message (m2) is fully answered.
        Assert.Null(Find(
            ToolUse("m1", "t1", "Bash", new { command = "vecchio comando" }, 1),
            Text("m2", "Riprendo.", 5),
            ToolUse("m2", "t2", "Read", new { file_path = Cwd + @"\a.txt" }, 6),
            Result("t2", 7),
            Text("m3", "Fatto.", 8)));
    }

    [Fact]
    public void A_prompt_after_the_tool_use_means_the_turn_is_over()
    {
        Assert.Null(Find(
            ToolUse("m1", "t1", "Bash", new { command = "npm run build" }, 1),
            Prompt("lascia stare, fai altro", 20)));
        Assert.Null(Find(
            ToolUse("m1", "t1", "Bash", new { command = "npm run build" }, 1),
            PromptBlocks("lascia stare, fai altro", 20)));
    }

    [Fact]
    public void Meta_lines_and_lines_of_other_types_do_not_hide_the_pending_tool_use()
    {
        var pending = Find(
            Prompt("apri la pagina", 0),
            ToolUse("m1", "t1", "WebFetch", new { url = "https://example.com/stato?x=1" }, 2),
            Prompt("<system-reminder>promemoria</system-reminder>", 3, meta: true),
            Other("attachment", plusSeconds: 3),
            Other("system", plusSeconds: 4));

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "WebFetch", "https://example.com/stato"), pending!.Detail);
    }

    [Fact]
    public void Malformed_lines_are_skipped_including_a_last_line_still_being_written()
    {
        var pending = Find(
            Prompt("lancia", 0),
            "{ questo non e' json",
            ToolUse("m1", "t1", "PowerShell", new { command = "Get-ChildItem" }, 2),
            "[1,2,3]",
            "{}",
            "\"solo una stringa\"",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_res""");

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "PowerShell", "Get-ChildItem"), pending!.Detail);
    }

    [Fact]
    public void Windows_line_endings_are_read_like_plain_ones()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", string.Join("\r\n",
            Prompt("lancia", 0),
            ToolUse("m1", "t1", "Bash", new { command = "make" }, 2)) + "\r\n");

        Assert.Equal("make", TranscriptAttentionReader.FindPending(file, Cwd)!.Detail.Summary);
    }

    [Fact]
    public void A_line_without_a_timestamp_gives_no_time()
    {
        var line = ToolUse("m1", "t1", "Bash", new { command = "make" }).Replace("\"timestamp\"", "\"ts_missing\"");

        var pending = Find(line);

        Assert.Equal("make", pending!.Detail.Summary);
        Assert.Null(pending.At);
    }

    [Fact]
    public void Only_the_last_256_KB_are_read()
    {
        // About 30 KB per padding line: the tool_use sits ~210 KB from the end in the first file, ~330 KB in the second.
        var padding = Enumerable.Range(0, 11).Select(i => Other("system", 30_000, 10 + i)).ToArray();
        var near = new[] { Prompt("lancia", 0), ToolUse("m1", "t1", "Bash", new { command = "make" }, 2) }.Concat(padding[..7]).ToArray();
        var far = new[] { Prompt("lancia", 0), ToolUse("m1", "t1", "Bash", new { command = "make" }, 2) }.Concat(padding).ToArray();

        Assert.Equal("make", Find(near)!.Detail.Summary);
        Assert.Null(Find(far));
    }

    [Fact]
    public void Missing_blank_or_unreadable_paths_give_null_and_only_the_read_failures_are_reported()
    {
        using var dir = new TempDir();
        var errors = new List<Exception>();

        // Nothing to read is not a failure.
        Assert.Null(TranscriptAttentionReader.FindPending(null, Cwd, errors.Add));
        Assert.Null(TranscriptAttentionReader.FindPending("   ", Cwd, errors.Add));
        Assert.Null(TranscriptAttentionReader.FindPending(dir.File("empty.jsonl", ""), Cwd, errors.Add));
        Assert.Empty(errors);

        Assert.Null(TranscriptAttentionReader.FindPending(Path.Combine(dir.Path, "nope.jsonl"), Cwd, errors.Add));
        Assert.IsType<FileNotFoundException>(Assert.Single(errors));
        Assert.Null(TranscriptAttentionReader.FindPending(dir.Path, Cwd, errors.Add));          // a directory
        Assert.Equal(2, errors.Count);
        Assert.IsType<UnauthorizedAccessException>(errors[1]);

        // Without a callback, or with one that throws, the reader still never throws.
        Assert.Null(TranscriptAttentionReader.FindPending(dir.Path, Cwd));
        Assert.Null(TranscriptAttentionReader.FindPending(dir.Path, Cwd, _ => throw new InvalidOperationException("log rotto")));
    }

    [Fact]
    public void A_file_locked_by_another_process_gives_null_and_one_open_for_appending_is_read()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Jsonl(ToolUse("m1", "t1", "Bash", new { command = "make" }, 2)));
        var errors = new List<Exception>();

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(TranscriptAttentionReader.FindPending(file, Cwd, errors.Add));
        Assert.IsType<IOException>(Assert.Single(errors));                                      // the sharing violation

        // Claude Code keeps the transcript open while it appends to it.
        using (new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            Assert.Equal("make", TranscriptAttentionReader.FindPending(file, Cwd, errors.Add)!.Detail.Summary);
        Assert.Single(errors);
    }

    [Fact]
    public void A_realistic_transcript_with_a_pending_command_is_read()
    {
        var pending = TranscriptAttentionReader.FindPending(Fixture("pending-bash.jsonl"), Cwd);

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash", "dotnet test tests/Demo.Tests --filter Category=Fast"), pending!.Detail);
        Assert.Equal(new DateTimeOffset(2026, 9, 27, 9, 0, 4, 300, TimeSpan.Zero), pending.At);
    }

    [Fact]
    public void A_realistic_finished_turn_has_nothing_pending()
    {
        Assert.Null(TranscriptAttentionReader.FindPending(Fixture("answered-turn.jsonl"), Cwd));
    }
}
