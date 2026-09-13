using System.Text;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class HookEventReaderTests
{
    private const string Line1 = """{"ts":"2026-09-13T10:15:02.123Z","agent":"claude","event":"UserPromptSubmit","session_id":"s1","cwd":"C:\\Users\\demo\\proj","notification_type":null,"message":null,"source":null}""";
    private const string Line2 = """{"ts":"2026-09-13T10:15:09.000Z","agent":"claude","event":"Notification","session_id":"s1","cwd":"C:\\Users\\demo\\proj","notification_type":"permission_prompt","message":"Bash needs approval","source":null}""";

    [Fact]
    public void Parse_maps_all_fields()
    {
        var ev = HookEventParser.Parse(Line2)!;
        Assert.Equal(new DateTimeOffset(2026, 9, 13, 10, 15, 9, TimeSpan.Zero), ev.Ts);
        Assert.Equal(AgentKind.Claude, ev.Agent);
        Assert.Equal("Notification", ev.Event);
        Assert.Equal("s1", ev.SessionId);
        Assert.Equal(@"C:\Users\demo\proj", ev.Cwd);
        Assert.Equal("permission_prompt", ev.NotificationType);
        Assert.Equal("Bash needs approval", ev.Message);
        Assert.Null(ev.Source);
    }

    [Fact]
    public void Parse_reads_subagent_fields()
    {
        var ev = HookEventParser.Parse("""{"ts":"2026-09-13T10:15:02Z","agent":"claude","event":"SubagentStart","session_id":"s1","cwd":null,"notification_type":null,"message":null,"source":null,"agent_id":"a1","agent_type":"Explore"}""")!;
        Assert.Equal("SubagentStart", ev.Event);
        Assert.Equal("a1", ev.AgentId);
        Assert.Equal("Explore", ev.AgentType);
        Assert.Null(HookEventParser.Parse(Line1)!.AgentId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"ts":"2026-09-13T10:15:02Z","agent":"gemini","event":"Stop","session_id":"s"}""")]
    [InlineData("""{"ts":"nope","agent":"claude","event":"Stop","session_id":"s"}""")]
    [InlineData("""{"ts":"2026-09-13T10:15:02Z","agent":"claude","session_id":"s"}""")]
    public void Parse_rejects_invalid_lines(string line) => Assert.Null(HookEventParser.Parse(line));

    [Fact]
    public void ReadNew_returns_only_complete_lines_appended_since_last_call()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "events.jsonl");
        var reader = new HookEventReader(path, Path.Combine(dir.Path, "events.1.jsonl"));

        Assert.Empty(reader.ReadNew()); // file does not exist yet

        File.WriteAllText(path, Line1 + "\n" + Line2[..20]); // second line still being written
        var first = reader.ReadNew();
        Assert.Single(first);
        Assert.Equal("UserPromptSubmit", first[0].Event);

        File.WriteAllText(path, Line1 + "\n" + Line2 + "\n");
        var second = reader.ReadNew();
        Assert.Single(second);
        Assert.Equal("Notification", second[0].Event);

        Assert.Empty(reader.ReadNew());
        Assert.Equal(Encoding.UTF8.GetByteCount(Line1 + "\n" + Line2 + "\n"), reader.Offset);
    }

    [Fact]
    public void ReadNew_restarts_when_the_file_shrinks()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "events.jsonl");
        var reader = new HookEventReader(path, Path.Combine(dir.Path, "events.1.jsonl"));
        File.WriteAllText(path, Line1 + "\n" + Line2 + "\n");
        Assert.Equal(2, reader.ReadNew().Count);
        File.WriteAllText(path, Line2 + "\n");
        Assert.Single(reader.ReadNew());
    }

    [Fact]
    public void ReadNew_skips_malformed_lines_and_handles_unicode()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "events.jsonl");
        var reader = new HookEventReader(path, Path.Combine(dir.Path, "events.1.jsonl"));
        var unicode = Line1.Replace("proj", "progetto-è");
        File.WriteAllText(path, "garbage\n" + unicode + "\n" + Line2 + "\n");
        var events = reader.ReadNew();
        Assert.Equal(2, events.Count);
        Assert.EndsWith("progetto-è", events[0].Cwd);
        Assert.Empty(reader.ReadNew());
    }

    [Fact]
    public void ReadAll_reads_rotated_then_current_filtered_by_time_and_fast_forwards()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "events.jsonl");
        var rotated = Path.Combine(dir.Path, "events.1.jsonl");
        File.WriteAllText(rotated, Line1.Replace("10:15:02.123Z", "08:00:00.000Z") + "\n");
        File.WriteAllText(path, Line1 + "\n" + Line2 + "\n");
        var reader = new HookEventReader(path, rotated);

        var all = reader.ReadAll(since: new DateTimeOffset(2026, 9, 13, 9, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, all.Count);
        Assert.Empty(reader.ReadNew());
    }

    [Fact]
    public void RotateIfNeeded_moves_large_files_and_resets_offset()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "events.jsonl");
        var rotated = Path.Combine(dir.Path, "events.1.jsonl");
        var reader = new HookEventReader(path, rotated);
        File.WriteAllText(path, Line1 + "\n");
        reader.ReadNew();
        Assert.False(reader.RotateIfNeeded());

        using (var fs = new FileStream(path, FileMode.Append)) fs.Write(new byte[HookEventReader.RotateThresholdBytes]);
        Assert.True(reader.RotateIfNeeded());
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(rotated));
        Assert.Equal(0, reader.Offset);
    }
}
