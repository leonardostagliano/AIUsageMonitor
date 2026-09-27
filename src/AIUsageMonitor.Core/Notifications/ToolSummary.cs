using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>
/// Turns one pending tool_use (the tool's name and its input) into the detail a card shows: the kind of wait, the tool
/// and one short line saying what the tool is about to do (spec 2026-09-27 §4.2).
/// </summary>
public static class ToolSummary
{
    public const int MaxLength = 120;

    private const string McpPrefix = "mcp__";

    /// <summary>Maps one tool_use (name + input object) to the detail of spec §4.2 table. Never throws.</summary>
    /// <remarks>
    /// A field that is missing or of the wrong type leaves <see cref="AttentionDetail.Summary"/> null: the card then
    /// falls back to its generic text. <paramref name="input"/> may be any JSON value, <c>default</c> included.
    /// </remarks>
    public static AttentionDetail Describe(string toolName, JsonElement input, string? cwd)
    {
        try
        {
            return DescribeInput(toolName, input, cwd);
        }
        catch (InvalidOperationException)
        {
            // An escaped lone surrogate ("\ud83d") is valid JSON, but System.Text.Json throws when it has to decode it: in
            // a string value, or in a property name a lookup compares. The input is then read as missing.
            return DescribeInput(toolName, default, cwd);
        }
    }

    /// <summary>Cuts to MaxLength (with "…" as last char when cut), collapses whitespace runs to one space, trims.</summary>
    public static string Shorten(string text) => Cut(text, MaxLength);

    private static AttentionDetail DescribeInput(string toolName, JsonElement input, string? cwd)
    {
        var name = toolName?.Trim();
        if (string.IsNullOrEmpty(name)) return new AttentionDetail(AttentionKind.Permission);

        switch (name)
        {
            case "AskUserQuestion":
                return new AttentionDetail(AttentionKind.Question, Summary: FirstQuestion(input));
            case "ExitPlanMode":
                return new AttentionDetail(AttentionKind.Plan, Summary: OrNull(PlanTitle(Text(input, "plan"))));
            case "Bash" or "PowerShell":
                return Permission(name, FirstLine(Text(input, "command")));
            case "Edit" or "Write" or "MultiEdit" or "NotebookEdit" or "Read":
                // NotebookEdit names its file notebook_path, the others file_path.
                return Permission(name, RelativeTo(Text(input, "file_path") ?? Text(input, "notebook_path"), cwd));
            case "WebFetch":
                return Permission(name, WithoutQuery(Text(input, "url")));
            case "WebSearch":
                return Permission(name, Text(input, "query"));
            case "Agent" or "Task":
                return Permission(name, Text(input, "description"));
        }

        if (McpParts(name) is { } mcp) return Permission(mcp.Tool, mcp.Server);
        return Permission(name, $"Vuole usare {name}");
    }

    private static AttentionDetail Permission(string tool, string? summary) =>
        new(AttentionKind.Permission, tool, OrNull(summary));

    /// <summary>The shortened text, or null when nothing is left of it.</summary>
    private static string? OrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var shortened = Shorten(text);
        return shortened.Length == 0 ? null : shortened;
    }

    /// <summary>The first question, followed by " (+N)" when the tool asks N more; the whole text stays within MaxLength.</summary>
    private static string? FirstQuestion(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("questions", out var questions)
            || questions.ValueKind != JsonValueKind.Array)
            return null;
        var count = questions.GetArrayLength();
        if (count == 0) return null;
        var question = Text(questions[0], "question");
        if (string.IsNullOrWhiteSpace(question)) return null;
        var more = count > 1 ? $" (+{count - 1})" : "";
        var text = Cut(question, MaxLength - more.Length);
        return text.Length == 0 ? null : text + more;
    }

    /// <summary>The first line of the plan that still has text once its leading '#' are gone.</summary>
    private static string? PlanTitle(string? plan)
    {
        if (plan is null) return null;
        foreach (var line in plan.Split('\n'))
        {
            var title = line.Trim().TrimStart('#').Trim();
            if (title.Length > 0) return title;
        }
        return null;
    }

    /// <summary>The first line of a command that is not blank.</summary>
    private static string? FirstLine(string? command)
    {
        if (command is null) return null;
        foreach (var line in command.Split('\n'))
            if (!string.IsNullOrWhiteSpace(line)) return line;
        return null;
    }

    /// <summary>The path relative to <paramref name="cwd"/> when it lies under it, the path as given otherwise.</summary>
    private static string? RelativeTo(string? path, string? cwd)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim();
        if (string.IsNullOrWhiteSpace(cwd)) return path;
        var root = cwd.Trim().TrimEnd('\\', '/');
        if (root.Length == 0 || path.Length <= root.Length + 1) return path;
        // Claude Code often writes "C:/p/demo/a.cs" under a cwd "C:\p\demo", and the reverse: '\' and '/' match.
        if (!string.Equals(path[..root.Length].Replace('\\', '/'), root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return path;
        // "C:\p\demo2\x" starts with "C:\p\demo" too: only a separator right after the root makes it a child.
        return path[root.Length] is '\\' or '/' ? path[(root.Length + 1)..] : path;
    }

    /// <summary>The URL without its query string and fragment.</summary>
    private static string? WithoutQuery(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var cut = url.IndexOfAny(['?', '#']);
        return cut >= 0 ? url[..cut] : url;
    }

    /// <summary>"mcp__&lt;server&gt;__&lt;tool&gt;" split at the first "__" after the prefix; null for any other name.</summary>
    private static (string Server, string Tool)? McpParts(string name)
    {
        if (!name.StartsWith(McpPrefix, StringComparison.Ordinal)) return null;
        var rest = name[McpPrefix.Length..];
        var separator = rest.IndexOf("__", StringComparison.Ordinal);
        if (separator <= 0 || separator + 2 >= rest.Length) return null;
        return (rest[..separator], rest[(separator + 2)..]);
    }

    /// <remarks>Throws InvalidOperationException on an escaped lone surrogate ("\ud83d"): <see cref="Describe"/> catches it.</remarks>
    private static string? Text(JsonElement obj, string property) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Collapses the whitespace, then cuts to <paramref name="max"/> characters with "…" as the last one.</summary>
    private static string Cut(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var collapsed = Collapse(text);
        if (collapsed.Length <= max) return collapsed;
        var keep = max - 1;
        // Never split a surrogate pair: half an emoji would render as a replacement box.
        if (keep > 0 && char.IsHighSurrogate(collapsed[keep - 1])) keep--;
        return collapsed[..keep].TrimEnd() + "…";
    }

    /// <summary>Every run of whitespace (new lines and tabs included) becomes one space; none is left at either end.</summary>
    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace) builder.Append(' ');
            pendingSpace = false;
            builder.Append(c);
        }
        return builder.ToString();
    }
}
