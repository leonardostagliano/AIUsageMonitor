using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>A tool_use still waiting for its tool_result, and the timestamp of the transcript line that carried it.</summary>
public sealed record PendingToolUse(AttentionDetail Detail, DateTimeOffset? At);

/// <summary>
/// Finds the tool_use a Claude Code session is waiting on, at the end of its transcript: Claude Code sends the
/// <c>permission_prompt</c> notification a few seconds after writing the tool_use line, so the request the user is
/// asked about is already on disk when the notification arrives.
/// </summary>
/// <remarks>
/// Transcript shape (verified on Claude Code 2.1): one JSON object per line with <c>type</c> ("user", "assistant",
/// "attachment", "system", ...), <c>timestamp</c> and <c>message.content</c>. One assistant message is often split over
/// several lines sharing <c>message.id</c> (thinking, text, each tool_use), and the tool_result lines of tools that
/// already ran can sit between them. Only the last <see cref="MaxTailBytes"/> of the file are read, directly and at
/// most once per call: a tool_use further back is not found and the caller falls back to a generic detail.
/// </remarks>
public static class TranscriptAttentionReader
{
    public const int MaxTailBytes = 256 * 1024;

    /// <summary>
    /// Latest assistant message with tool_use blocks, read from the end within MaxTailBytes; returns its last tool_use
    /// without a later tool_result, or null (none pending, a real user prompt reached first, file missing/locked/unreadable).
    /// </summary>
    /// <remarks>
    /// Never throws; malformed lines are skipped. <paramref name="cwd"/> makes file paths relative (see <see cref="ToolSummary"/>).
    /// <paramref name="onError"/> receives the exception of a file that could not be read, so the caller can log its
    /// type: the reader logs nothing, and the exception message may hold the path, whose folder names the session.
    /// </remarks>
    public static PendingToolUse? FindPending(string? transcriptPath, string? cwd, Action<Exception>? onError = null)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath)) return null;
        List<string> lines;
        try
        {
            lines = ReadTail(transcriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Missing, locked by another process, a directory, an invalid path: nothing can be said about the wait.
            Report(onError, ex);
            return null;
        }
        return Scan(lines, cwd);
    }

    private static void Report(Action<Exception>? onError, Exception ex)
    {
        try { onError?.Invoke(ex); } catch { /* a broken logger must not make the reader throw */ }
    }

    /// <summary>
    /// The complete lines that lie in the last <see cref="MaxTailBytes"/> bytes of the file, oldest first. The file is
    /// opened like <see cref="Infrastructure.ReverseLineReader"/> does (Claude Code keeps appending to it), and the
    /// line cut by the start of the window is dropped whole.
    /// </summary>
    private static List<string> ReadTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        // One byte before the window as well: it tells whether the window starts exactly at the start of a line.
        var from = length > MaxTailBytes ? length - MaxTailBytes - 1 : 0;
        var buffer = new byte[length - from];
        stream.Seek(from, SeekOrigin.Begin);
        stream.ReadExactly(buffer);

        var start = 0;
        if (from > 0)
        {
            var newline = Array.IndexOf(buffer, (byte)'\n');
            if (newline < 0) return [];
            start = newline + 1;
        }

        var lines = new List<string>();
        foreach (var line in Encoding.UTF8.GetString(buffer, start, buffer.Length - start).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0) lines.Add(trimmed);
        }
        return lines;
    }

    /// <summary>Walks the lines from the newest, collecting the answered tool_use ids, until the target message is behind.</summary>
    private static PendingToolUse? Scan(List<string> lines, string? cwd)
    {
        var answered = new HashSet<string>(StringComparer.Ordinal);
        var found = false;
        string? targetId = null;

        for (var i = lines.Count - 1; i >= 0; i--)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(lines[i]);
            }
            catch (JsonException)
            {
                // A corrupt line, or the last one while Claude Code is still writing it.
                continue;
            }

            using (document)
            {
                try
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    var type = Text(root, "type");

                    if (type == "user")
                    {
                        // Meta lines (skill bodies, reminders, command caveats) are written by Claude Code, not typed.
                        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) continue;
                        var content = Content(root);
                        if (CollectResults(content, answered)) continue;
                        // A prompt newer than every tool_use still unanswered: the turn they belonged to is over.
                        if (IsPrompt(content)) return null;
                        continue;
                    }

                    if (type != "assistant") continue;
                    var messageId = MessageId(root);
                    // Past the lines of the target message: every one of its tool_use blocks has been seen.
                    if (found && (messageId is null || messageId != targetId)) return null;

                    var uses = ToolUses(root);
                    if (!found)
                    {
                        if (uses.Count == 0) continue;
                        found = true;
                        targetId = messageId;
                    }

                    for (var k = uses.Count - 1; k >= 0; k--)
                    {
                        if (answered.Contains(uses[k].Id)) continue;
                        return new PendingToolUse(ToolSummary.Describe(uses[k].Name, uses[k].Input, cwd), Timestamp(root));
                    }
                }
                catch (InvalidOperationException)
                {
                    // An escaped lone surrogate ("\ud83d") is valid JSON, but System.Text.Json throws when it has to
                    // decode it, in a string value or in a property name a lookup compares: the line is skipped.
                    continue;
                }
            }
        }
        return null;
    }

    /// <summary>Adds the tool_use ids answered by a user line; true when the line carries at least one tool_result.</summary>
    private static bool CollectResults(JsonElement content, HashSet<string> answered)
    {
        if (content.ValueKind != JsonValueKind.Array) return false;
        var any = false;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || Text(block, "type") != "tool_result") continue;
            any = true;
            if (Text(block, "tool_use_id") is { Length: > 0 } id) answered.Add(id);
        }
        return any;
    }

    /// <summary>A user line with a string content, or with text blocks (and, the caller checked, no tool_result).</summary>
    private static bool IsPrompt(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => true,
        JsonValueKind.Array => content.EnumerateArray().Any(b => b.ValueKind == JsonValueKind.Object && Text(b, "type") == "text"),
        _ => false
    };

    /// <summary>The tool_use blocks of an assistant line, in the order they were written.</summary>
    private static List<(string Id, string Name, JsonElement Input)> ToolUses(JsonElement root)
    {
        var uses = new List<(string, string, JsonElement)>();
        var content = Content(root);
        if (content.ValueKind != JsonValueKind.Array) return uses;
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object || Text(block, "type") != "tool_use") continue;
            if (Text(block, "id") is not { Length: > 0 } id || Text(block, "name") is not { Length: > 0 } name) continue;
            uses.Add((id, name, block.TryGetProperty("input", out var input) ? input : default));
        }
        return uses;
    }

    private static JsonElement Content(JsonElement root) =>
        root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object
        && message.TryGetProperty("content", out var content)
            ? content
            : default;

    private static string? MessageId(JsonElement root) =>
        root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object ? Text(message, "id") : null;

    private static DateTimeOffset? Timestamp(JsonElement root)
    {
        if (!root.TryGetProperty("timestamp", out var value) || value.ValueKind != JsonValueKind.String) return null;
        try
        {
            return value.TryGetDateTimeOffset(out var at) ? at : null;
        }
        catch (InvalidOperationException)
        {
            return null;                                // an escaped lone surrogate ("\ud83d") cannot be decoded
        }
    }

    /// <remarks>Throws InvalidOperationException on an escaped lone surrogate ("\ud83d"): <see cref="Scan"/> skips the line.</remarks>
    private static string? Text(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
