using System.Text;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ReverseLineReaderTests
{
    [Fact]
    public void Reads_lines_from_last_to_first()
    {
        using var dir = new TempDir();
        var file = dir.File("a.txt", "one\ntwo\nthree\n");
        Assert.Equal(new[] { "three", "two", "one" }, ReverseLineReader.ReadLinesFromEnd(file).ToArray());
    }

    [Fact]
    public void Handles_missing_trailing_newline_and_crlf()
    {
        using var dir = new TempDir();
        var file = dir.File("a.txt", "one\r\ntwo\r\nthree");
        Assert.Equal(new[] { "three", "two", "one" }, ReverseLineReader.ReadLinesFromEnd(file).ToArray());
    }

    [Fact]
    public void Skips_empty_lines_and_handles_empty_file()
    {
        using var dir = new TempDir();
        Assert.Empty(ReverseLineReader.ReadLinesFromEnd(dir.File("empty.txt", "")));
        Assert.Equal(new[] { "b", "a" }, ReverseLineReader.ReadLinesFromEnd(dir.File("gaps.txt", "a\n\n\nb\n\n")).ToArray());
    }

    [Fact]
    public void Lines_spanning_chunk_boundaries_and_multibyte_chars_are_intact()
    {
        using var dir = new TempDir();
        var lines = Enumerable.Range(0, 500).Select(i => $"riga {i} è ünïcödé {new string('x', i % 37)}").ToArray();
        var file = dir.File("big.txt", string.Join("\n", lines) + "\n");
        var read = ReverseLineReader.ReadLinesFromEnd(file, chunkSize: 100).ToArray();
        Assert.Equal(lines.Reverse(), read);
    }

    [Fact]
    public void Can_be_read_while_another_handle_is_appending()
    {
        using var dir = new TempDir();
        var file = dir.File("live.txt", "first\n");
        using var writer = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer.Write(Encoding.UTF8.GetBytes("second\n"));
        writer.Flush();
        Assert.Equal("second", ReverseLineReader.ReadLinesFromEnd(file).First());
    }
}
