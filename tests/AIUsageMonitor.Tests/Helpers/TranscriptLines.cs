using System.Globalization;
using System.Text.Json;

namespace AIUsageMonitor.Tests.Helpers;

/// <summary>
/// Synthetic lines of a Claude Code transcript (<c>~/.claude/projects/&lt;proj&gt;/&lt;session&gt;.jsonl</c>), reduced to
/// the fields the attention reader looks at. Every value is invented: no real transcript is ever copied here.
/// </summary>
public static class TranscriptLines
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 27, 9, 0, 0, TimeSpan.Zero);

    /// <summary>A prompt typed by the user (string content), or a line Claude Code writes itself when <paramref name="meta"/>.</summary>
    public static string Prompt(string text, int plusSeconds = 0, bool meta = false)
    {
        var line = Line("user", plusSeconds, new Dictionary<string, object?> { ["role"] = "user", ["content"] = text });
        if (meta) line["isMeta"] = true;
        return JsonSerializer.Serialize(line);
    }

    /// <summary>A prompt whose content is a list of text blocks (what Claude Code writes when an image is pasted too).</summary>
    public static string PromptBlocks(string text, int plusSeconds = 0) =>
        JsonSerializer.Serialize(Line("user", plusSeconds, new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } }
        }));

    /// <summary>One assistant line of message <paramref name="messageId"/> carrying one tool_use block.</summary>
    public static string ToolUse(string messageId, string toolUseId, string name, object input, int plusSeconds = 0) =>
        JsonSerializer.Serialize(Line("assistant", plusSeconds, new Dictionary<string, object?>
        {
            ["id"] = messageId,
            ["role"] = "assistant",
            ["content"] = new object[]
            {
                new Dictionary<string, object?> { ["type"] = "tool_use", ["id"] = toolUseId, ["name"] = name, ["input"] = input }
            }
        }));

    /// <summary>One assistant line of message <paramref name="messageId"/> carrying several tool_use blocks, in order.</summary>
    public static string ToolUses(string messageId, int plusSeconds, params (string Id, string Name, object Input)[] uses) =>
        JsonSerializer.Serialize(Line("assistant", plusSeconds, new Dictionary<string, object?>
        {
            ["id"] = messageId,
            ["role"] = "assistant",
            ["content"] = uses
                .Select(u => (object)new Dictionary<string, object?> { ["type"] = "tool_use", ["id"] = u.Id, ["name"] = u.Name, ["input"] = u.Input })
                .ToArray()
        }));

    /// <summary>One assistant line of message <paramref name="messageId"/> carrying only text.</summary>
    public static string Text(string messageId, string text, int plusSeconds = 0) =>
        JsonSerializer.Serialize(Line("assistant", plusSeconds, new Dictionary<string, object?>
        {
            ["id"] = messageId,
            ["role"] = "assistant",
            ["content"] = new object[] { new Dictionary<string, object?> { ["type"] = "text", ["text"] = text } }
        }));

    /// <summary>The user line with the tool_result of <paramref name="toolUseId"/>.</summary>
    public static string Result(string toolUseId, int plusSeconds = 0, bool isError = false) =>
        JsonSerializer.Serialize(Line("user", plusSeconds, new Dictionary<string, object?>
        {
            ["role"] = "user",
            ["content"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["type"] = "tool_result", ["tool_use_id"] = toolUseId, ["content"] = "ok", ["is_error"] = isError
                }
            }
        }));

    /// <summary>A line that is neither user nor assistant (system, attachment...), padded to about <paramref name="padding"/> characters.</summary>
    public static string Other(string type = "system", int padding = 0, int plusSeconds = 0)
    {
        var line = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["timestamp"] = Stamp(plusSeconds),
            ["content"] = new string('x', padding)
        };
        return JsonSerializer.Serialize(line);
    }

    /// <summary>The lines as a JSONL file body, trailing newline included.</summary>
    public static string Jsonl(params string[] lines) => string.Join("\n", lines) + "\n";

    public static DateTimeOffset At(int plusSeconds) => T0.AddSeconds(plusSeconds);

    private static Dictionary<string, object?> Line(string type, int plusSeconds, Dictionary<string, object?> message) => new()
    {
        ["type"] = type,
        ["timestamp"] = Stamp(plusSeconds),
        ["message"] = message
    };

    private static string Stamp(int plusSeconds) =>
        At(plusSeconds).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
