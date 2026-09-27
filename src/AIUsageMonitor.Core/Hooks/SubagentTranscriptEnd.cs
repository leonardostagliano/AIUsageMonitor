using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Tells from its files that a Claude Code subagent is over although its SubagentStop never came: the user interrupted
/// it, the API refused to go on (the session limit), or the user stopped it from the task list.
/// </summary>
/// <remarks>
/// Shape verified on the agent transcripts of Claude Code 2.1 (<c>&lt;proj&gt;/&lt;session&gt;/subagents/**/agent-&lt;id&gt;.jsonl</c>):
/// an interrupted agent ends with a user line whose text (a string content or a <c>text</c> block) opens with
/// "[Request interrupted by user" (the "for tool use" variant included); an agent the API stopped ends with an assistant
/// line carrying <c>"isApiErrorMessage": true</c>; <c>attachment</c> and <c>system</c> lines may follow either. An agent
/// stopped from the task list can end with an ordinary line, but its <c>agent-&lt;id&gt;.meta.json</c> next to the
/// transcript then says <c>"stoppedByUser": true</c>. Only the last <see cref="MaxTailBytes"/> of the transcript are
/// read, once per call.
/// </remarks>
public static class SubagentTranscriptEnd
{
    public const int MaxTailBytes = 64 * 1024;

    private const string InterruptedMarker = "[Request interrupted by user";

    /// <summary>
    /// True when the last user/assistant line is "[Request interrupted by user…" or an assistant line with
    /// isApiErrorMessage, or when the agent's meta.json says stoppedByUser. Never throws: a missing, locked or
    /// unreadable file says nothing (false), and corrupt lines are skipped.
    /// </summary>
    public static bool IsTerminated(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath)) return false;
        if (StoppedByUser(transcriptPath)) return true;
        List<string> lines;
        try
        {
            lines = ReadTail(transcriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Missing, locked, a directory, an invalid path: nothing can be said about the agent.
            return false;
        }
        return EndsTerminated(lines);
    }

    /// <summary>The newest user or assistant line decides; every other line (attachment, system…) is passed over.</summary>
    private static bool EndsTerminated(List<string> lines)
    {
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
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                try
                {
                    switch (Text(root, "type"))
                    {
                        case "assistant":
                            return root.TryGetProperty("isApiErrorMessage", out var error) && error.ValueKind == JsonValueKind.True;
                        case "user":
                            return IsInterruption(root);
                    }
                }
                catch (InvalidOperationException)
                {
                    // An escaped lone surrogate ("\ud83d") is valid JSON, but System.Text.Json throws when it has to
                    // decode it, in a string value or in a property name a lookup compares: the line is skipped.
                }
            }
        }
        return false;
    }

    /// <summary>A user line whose string content, or one of whose text blocks, opens with the interruption marker.</summary>
    private static bool IsInterruption(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
            || !message.TryGetProperty("content", out var content))
            return false;
        return content.ValueKind switch
        {
            JsonValueKind.String => content.GetString()!.StartsWith(InterruptedMarker, StringComparison.Ordinal),
            JsonValueKind.Array => content.EnumerateArray().Any(block => block.ValueKind == JsonValueKind.Object
                && Text(block, "type") == "text" && Text(block, "text") is { } text
                && text.StartsWith(InterruptedMarker, StringComparison.Ordinal)),
            _ => false
        };
    }

    /// <summary>
    /// <c>"stoppedByUser": true</c> in the <c>agent-&lt;id&gt;.meta.json</c> next to the transcript. A missing, oversized,
    /// unreadable or corrupt file says nothing.
    /// </summary>
    private static bool StoppedByUser(string transcriptPath)
    {
        try
        {
            var meta = Path.ChangeExtension(transcriptPath, ".meta.json");
            if (!File.Exists(meta)) return false;
            using var stream = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxTailBytes) return false;
            using var document = JsonDocument.Parse(stream);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("stoppedByUser", out var stopped) && stopped.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The complete lines that lie in the last <see cref="MaxTailBytes"/> bytes of the file, oldest first, read like
    /// the attention reader does: Claude Code keeps appending to the file, and the line cut by the start of the
    /// window is dropped whole.
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

    private static string? Text(JsonElement obj, string property) =>
        obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
