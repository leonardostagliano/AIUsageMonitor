namespace AIUsageMonitor.Core.Models;

public enum AgentKind
{
    Claude,
    Codex
}

public static class AgentKindExtensions
{
    public static string DisplayName(this AgentKind kind) => kind switch
    {
        AgentKind.Claude => "Claude Code",
        AgentKind.Codex => "Codex",
        _ => kind.ToString()
    };

    /// <summary>Lower-case key used in hook commands and event lines ("claude", "codex").</summary>
    public static string Key(this AgentKind kind) => kind.ToString().ToLowerInvariant();

    public static bool TryParseKey(string? key, out AgentKind kind)
    {
        switch (key?.Trim().ToLowerInvariant())
        {
            case "claude": kind = AgentKind.Claude; return true;
            case "codex": kind = AgentKind.Codex; return true;
            default: kind = default; return false;
        }
    }
}
