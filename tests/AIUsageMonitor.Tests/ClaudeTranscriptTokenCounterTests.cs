using System.Text.Json;
using System.Text;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ClaudeTranscriptTokenCounterTests
{
    /// <summary>One `type: "assistant"` transcript line as Claude Code writes it (usage nested under `message`).</summary>
    private static string Assistant(string? requestId, long input, long output, long cacheRead, long cacheWrite, string uuid = "u")
    {
        var line = new Dictionary<string, object?>
        {
            ["type"] = "assistant",
            ["uuid"] = uuid,
            ["message"] = new Dictionary<string, object?>
            {
                ["role"] = "assistant",
                ["usage"] = new Dictionary<string, object?>
                {
                    ["input_tokens"] = input,
                    ["output_tokens"] = output,
                    ["cache_read_input_tokens"] = cacheRead,
                    ["cache_creation_input_tokens"] = cacheWrite
                }
            }
        };
        if (requestId is not null) line["requestId"] = requestId;
        return JsonSerializer.Serialize(line);
    }

    /// <summary>
    /// Six lines: one request repeated three times with identical usage (Claude writes one line per content block),
    /// a second request, an assistant line without requestId (counts once on its own) and a non-assistant line.
    /// </summary>
    private static string Fixture() => Join(
        Assistant("req_a", 10, 20, 30, 40, "a1"),
        Assistant("req_a", 10, 20, 30, 40, "a2"),
        Assistant("req_a", 10, 20, 30, 40, "a3"),
        Assistant("req_b", 1, 2, 3, 4, "b1"),
        Assistant(null, 100, 200, 300, 400, "c1"),
        """{"type":"user","message":{"role":"user","usage":{"input_tokens":999,"output_tokens":999,"cache_read_input_tokens":999,"cache_creation_input_tokens":999}}}""");

    /// <summary>The lines joined as a JSONL chunk (trailing newline included: only complete lines are consumed).</summary>
    private static string Join(params string[] lines) => string.Join("\n", lines) + "\n";

    private static readonly TokenUsage FixtureTotal = new(111, 222, 333, 444);

    [Fact]
    public void Counts_one_usage_per_requestId_and_ignores_non_assistant_lines()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Fixture());

        var counter = new ClaudeTranscriptTokenCounter();

        Assert.Equal(FixtureTotal, counter.Read(file));
    }

    [Fact]
    public void Second_call_reads_only_the_appended_bytes()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Fixture());
        var counter = new ClaudeTranscriptTokenCounter();

        Assert.Equal(FixtureTotal, counter.Read(file));
        Assert.Equal(new FileInfo(file).Length, counter.OffsetOf(file));

        File.AppendAllText(file, Join(Assistant("req_a", 10, 20, 30, 40, "a4"), Assistant("req_c", 5, 6, 7, 8, "d1")));

        // req_a was already counted; only req_c is added.
        Assert.Equal(new TokenUsage(116, 228, 340, 452), counter.Read(file));
        Assert.Equal(new FileInfo(file).Length, counter.OffsetOf(file));
    }

    [Fact]
    public void Incomplete_trailing_line_is_counted_only_once_complete()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Fixture());
        var counter = new ClaudeTranscriptTokenCounter();
        Assert.Equal(FixtureTotal, counter.Read(file));

        var partial = Assistant("req_c", 5, 6, 7, 8, "d1");
        File.AppendAllText(file, partial[..20]);
        Assert.Equal(FixtureTotal, counter.Read(file));

        File.AppendAllText(file, partial[20..] + "\n");
        Assert.Equal(new TokenUsage(116, 228, 340, 452), counter.Read(file));
    }

    [Fact]
    public void Truncated_file_restarts_from_zero()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Fixture());
        var counter = new ClaudeTranscriptTokenCounter();
        Assert.Equal(FixtureTotal, counter.Read(file));

        File.WriteAllText(file, Join(Assistant("req_z", 1, 1, 1, 1, "z1")));

        Assert.Equal(new TokenUsage(1, 1, 1, 1), counter.Read(file));
        Assert.Equal(new FileInfo(file).Length, counter.OffsetOf(file));
    }

    [Fact]
    public void Missing_file_is_zero_and_is_counted_once_it_appears()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "later.jsonl");
        var counter = new ClaudeTranscriptTokenCounter();

        Assert.Equal(TokenUsage.Zero, counter.Read(file));

        File.WriteAllText(file, Fixture());
        Assert.Equal(FixtureTotal, counter.Read(file));
    }

    [Fact]
    public void Malformed_lines_and_missing_usage_never_throw()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Join(
            "not json at all",
            "[1,2,3]",
            """{"type":"assistant","requestId":"req_x"}""",
            """{"type":"assistant","requestId":"req_y","message":{"usage":"nope"}}""",
            Assistant("req_a", 10, 20, 30, 40)));

        Assert.Equal(new TokenUsage(10, 20, 30, 40), new ClaudeTranscriptTokenCounter().Read(file));
    }

    [Fact]
    public void A_request_whose_first_line_has_no_usage_is_still_counted_later()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Join("""{"type":"assistant","requestId":"req_a"}"""));
        var counter = new ClaudeTranscriptTokenCounter();
        Assert.Equal(TokenUsage.Zero, counter.Read(file));

        File.AppendAllText(file, Join(Assistant("req_a", 10, 20, 30, 40)));
        Assert.Equal(new TokenUsage(10, 20, 30, 40), counter.Read(file));
    }

    /// <summary>
    /// The streamed shape of a real subagent transcript: Claude writes the intermediate content-block lines with a
    /// partial <c>message.usage</c> (<c>output_tokens</c> still growing) and the final line with the real one, the
    /// other three components identical. Keeping the first line would undercount almost every response.
    /// </summary>
    [Fact]
    public void Growing_usage_of_one_request_is_counted_at_its_largest()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Join(
            Assistant("req_a", 4, 1, 25_000, 120, "a1"),
            Assistant("req_a", 4, 339, 25_000, 120, "a2")));

        Assert.Equal(new TokenUsage(4, 339, 25_000, 120), new ClaudeTranscriptTokenCounter().Read(file));
    }

    /// <summary>The growth lands on whichever read sees the later line, so an incremental read totals the same.</summary>
    [Fact]
    public void Growing_usage_split_across_two_reads_adds_only_the_delta()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Join(Assistant("req_a", 4, 1, 25_000, 120, "a1")));
        var counter = new ClaudeTranscriptTokenCounter();

        Assert.Equal(new TokenUsage(4, 1, 25_000, 120), counter.Read(file));

        File.AppendAllText(file, Join(Assistant("req_a", 4, 339, 25_000, 120, "a2")));

        Assert.Equal(new TokenUsage(4, 339, 25_000, 120), counter.Read(file));
    }

    /// <summary>A later line is never smaller in practice; if one ever is, it must not subtract from the total.</summary>
    [Fact]
    public void A_smaller_later_usage_never_lowers_the_total()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Join(
            Assistant("req_a", 10, 20, 30, 40, "a1"),
            Assistant("req_a", 1, 2, 3, 4, "a2")));

        Assert.Equal(new TokenUsage(10, 20, 30, 40), new ClaudeTranscriptTokenCounter().Read(file));
    }

    [Fact]
    public void Forget_drops_the_state_so_the_next_read_starts_over()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Fixture());
        var counter = new ClaudeTranscriptTokenCounter();
        Assert.Equal(FixtureTotal, counter.Read(file));

        counter.Forget(file);
        Assert.Equal(0, counter.OffsetOf(file));

        // Re-read from the beginning: the same file yields the same total, not twice it.
        Assert.Equal(FixtureTotal, counter.Read(file));
    }

    [Fact]
    public void Seen_request_ids_are_bounded_to_the_most_recent_ones()
    {
        using var dir = new TempDir();
        var counter = new ClaudeTranscriptTokenCounter { MaxSeenRequestIds = 4 };
        var file = dir.File("session.jsonl", Join(
            Enumerable.Range(0, 6).Select(i => Assistant($"req_{i}", 1, 0, 0, 0, $"u{i}")).ToArray()));

        Assert.Equal(new TokenUsage(6, 0, 0, 0), counter.Read(file));

        // The four newest ids are still remembered, so their duplicates add nothing.
        File.AppendAllText(file, Join(Assistant("req_5", 1, 0, 0, 0, "v5"), Assistant("req_4", 1, 0, 0, 0, "v4")));
        Assert.Equal(new TokenUsage(6, 0, 0, 0), counter.Read(file));

        // req_0 fell out of the bounded set: its usage is counted a second time. That is the price of the bound.
        File.AppendAllText(file, Join(Assistant("req_0", 1, 0, 0, 0, "v0")));
        Assert.Equal(new TokenUsage(7, 0, 0, 0), counter.Read(file));
    }

    [Fact]
    public void Reads_a_transcript_the_writer_keeps_open()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Fixture());
        using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Seek(0, SeekOrigin.End);
        var appended = Encoding.UTF8.GetBytes(Join(Assistant("req_c", 5, 6, 7, 8, "d1")));
        writer.Write(appended, 0, appended.Length);
        writer.Flush();

        Assert.Equal(new TokenUsage(116, 228, 340, 452), new ClaudeTranscriptTokenCounter().Read(file));
    }

    [Fact]
    public void Multibyte_content_keeps_the_offset_aligned()
    {
        using var dir = new TempDir();
        var file = dir.File("session.jsonl", Join(
            """{"type":"user","message":{"role":"user","content":"perché è così, però — ünïcödé"}}""",
            Assistant("req_a", 10, 20, 30, 40)));
        var counter = new ClaudeTranscriptTokenCounter();

        Assert.Equal(new TokenUsage(10, 20, 30, 40), counter.Read(file));
        Assert.Equal(new FileInfo(file).Length, counter.OffsetOf(file));

        File.AppendAllText(file, Join(Assistant("req_b", 1, 2, 3, 4)));
        Assert.Equal(new TokenUsage(11, 22, 33, 44), counter.Read(file));
    }
}
