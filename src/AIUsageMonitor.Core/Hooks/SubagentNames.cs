using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// The name a subagent was started with, as its agent wrote it down: the <c>description</c> of the
/// <c>agent-&lt;id&gt;.meta.json</c> Claude Code writes next to the agent's transcript ("write:B (tasks 3,6)"), or the
/// nickname and the agent path Codex gives a child thread ("Harvey · oasis_brand"). The hooks only carry the agent's
/// type ("workflow-subagent", "general-purpose", "default"), which says nothing about what the agent does.
/// </summary>
/// <remarks>A name is shown in the notch only: it is never logged.</remarks>
public static class SubagentNames
{
    /// <summary>Longest name kept, "…" included.</summary>
    public const int MaxLength = 60;

    /// <summary>A meta.json larger than this is not an agent's sidecar: it is not read.</summary>
    private const int MaxMetaBytes = 64 * 1024;

    /// <summary>
    /// Reads the name of a Claude Code agent from the <c>agent-&lt;id&gt;.meta.json</c> next to its transcript. False
    /// when that file cannot tell yet: no transcript path, no meta.json, a file locked, being written (not valid JSON
    /// yet) or oversized; the caller asks again later. True once the file was read: <paramref name="name"/> is then its
    /// <c>description</c>, shortened, or null when it has none. Never throws.
    /// </summary>
    public static bool TryReadClaude(string? agentTranscriptPath, out string? name)
    {
        name = null;
        if (string.IsNullOrWhiteSpace(agentTranscriptPath)) return false;
        try
        {
            var meta = Path.ChangeExtension(agentTranscriptPath, ".meta.json");
            if (!File.Exists(meta)) return false;
            using var stream = new FileStream(meta, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxMetaBytes) return false;
            using var document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return true;
            if (document.RootElement.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                name = Shorten(description.GetString());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException or JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// "Harvey · oasis_brand": the nickname of a Codex child and the last segment of its agent path
    /// ("/root/oasis_brand"); one of the two when the other is missing; null when neither is there.
    /// </summary>
    public static string? FromCodex(string? nickname, string? agentPath)
    {
        var nick = string.IsNullOrWhiteSpace(nickname) ? null : nickname.Trim();
        var leaf = agentPath?.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrEmpty(leaf)) leaf = null;
        return Shorten(nick is not null && leaf is not null ? $"{nick} · {leaf}" : nick ?? leaf);
    }

    /// <summary>
    /// The text on one line (every run of whitespace becomes one space), trimmed, and cut to <see cref="MaxLength"/>
    /// characters with "…" as the last one when it is longer, never between the two halves of a surrogate pair; null
    /// when nothing is left.
    /// </summary>
    public static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = new StringBuilder(text.Length);
        foreach (var c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (line.Length > 0 && line[^1] != ' ') line.Append(' ');
            }
            else line.Append(c);
        }
        var collapsed = line.ToString();
        if (collapsed.Length <= MaxLength) return collapsed;
        var keep = MaxLength - 1;
        // Half an emoji would render as a replacement box.
        if (char.IsHighSurrogate(collapsed[keep - 1])) keep--;
        return collapsed[..keep].TrimEnd() + "…";
    }
}
