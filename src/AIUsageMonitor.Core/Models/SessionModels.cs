using System.Linq;

namespace AIUsageMonitor.Core.Models;

public enum SessionPhase { Working, NeedsInput, Idle, Error }

/// <summary>Token usage for a session or a subagent (Claude Code fields; Codex maps its totals onto Input/Output/CacheRead/CacheWrite).</summary>
public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite)
{
    public long Total => Input + Output + CacheRead + CacheWrite;

    public static readonly TokenUsage Zero = new(0, 0, 0, 0);

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite);
}

public enum SubagentPhase { Running, Done }

/// <summary>One background subagent or workflow agent spawned by a session (Agent tool or workflow).</summary>
public sealed record SubagentState(
    string AgentId,
    string? AgentType,
    SubagentPhase Phase,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string? TranscriptPath,
    TokenUsage Tokens);

public sealed record SessionState(
    AgentKind Agent,
    string SessionId,
    string DisplayName,
    string? Cwd,
    SessionPhase Phase,
    string? Message,
    DateTimeOffset LastEventAt,
    DateTimeOffset StartedAt,
    string? TranscriptPath = null,
    TokenUsage? Tokens = null,
    IReadOnlyList<SubagentState>? Subagents = null)
{
    /// <summary>Italian label shown in the UI. Idle is "pronto" before the first completed turn, "finito" after.</summary>
    public string PhaseLabel => Phase switch
    {
        SessionPhase.Working => "al lavoro",
        SessionPhase.NeedsInput => "attende input",
        SessionPhase.Idle => Message is null ? "pronto" : "finito",
        SessionPhase.Error => "errore",
        _ => Phase.ToString()
    };

    /// <summary>Number of subagents still running (Agent tool or workflow agents).</summary>
    public int ActiveSubagents => Subagents?.Count(s => s.Phase == SubagentPhase.Running) ?? 0;

    /// <summary>Sum of token usage across every known subagent (running and done).</summary>
    public TokenUsage SubagentTokens => Subagents is null
        ? TokenUsage.Zero
        : Subagents.Aggregate(TokenUsage.Zero, (acc, s) => acc + s.Tokens);
}

/// <summary>One line of ~/.aiusagemonitor/events.jsonl as written by hook.cjs.</summary>
public sealed record HookEvent(
    DateTimeOffset Ts,
    AgentKind Agent,
    string Event,
    string SessionId,
    string? Cwd,
    string? NotificationType,
    string? Message,
    string? Source,
    string? AgentId = null,
    string? AgentType = null);
