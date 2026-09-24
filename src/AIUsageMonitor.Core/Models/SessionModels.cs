using System.Linq;

namespace AIUsageMonitor.Core.Models;

public enum SessionPhase { Working, NeedsInput, Idle, Error }

/// <summary>Where a session runs, and so where the app learns about it.</summary>
public enum SessionOrigin
{
    /// <summary>A session in a terminal or an IDE, reported by the agent's hooks (Claude Code CLI, Codex).</summary>
    Terminal,

    /// <summary>
    /// A local session started by an app (the Claude desktop app, an SDK host): from the hooks when they fire there,
    /// otherwise from the session registry Claude Code keeps in <c>~/.claude/sessions</c>.
    /// </summary>
    App,

    /// <summary>A Claude Code session running in Anthropic's cloud (claude.ai/code, the apps), read from the sessions API.</summary>
    Cloud,

    /// <summary>A run of a scheduled Claude routine, in the cloud like <see cref="Cloud"/>.</summary>
    Routine
}

/// <summary>
/// One entry of the <c>background_tasks</c> Claude Code puts in its <c>Stop</c> and <c>SubagentStop</c> payloads:
/// backgrounded work still in flight. hook.cjs keeps only agents (<c>subagent</c>, whose id is the agent id of
/// <c>SubagentStart</c>) and workflows.
/// </summary>
public sealed record BackgroundTask(string Id, string Type, string? AgentType = null, string? Name = null)
{
    public const string SubagentType = "subagent";
    public const string WorkflowType = "workflow";

    /// <summary>
    /// The backgrounded main session (<c>agent_type</c> "main-session") is registered as an agent task too, but it is
    /// the session itself, not one of its agents, and fires no SubagentStart.
    /// </summary>
    public bool IsAgent => Type == SubagentType && AgentType != "main-session";

    public bool IsWorkflow => Type == WorkflowType;
}

/// <summary>Token usage for a session or a subagent (Claude Code fields; Codex maps its totals onto Input/Output/CacheRead/CacheWrite).</summary>
public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite)
{
    /// <summary>All input processed, including cache reads and writes; Input itself is the uncached bucket.</summary>
    public long TotalInput => Input + CacheRead + CacheWrite;

    public long Total => TotalInput + Output;

    public static readonly TokenUsage Zero = new(0, 0, 0, 0);

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheRead + b.CacheRead, a.CacheWrite + b.CacheWrite);
}

/// <summary>
/// Terminal host of a session, as read by the hook from its own environment and parent pid. <see cref="WmuxPty"/>
/// is the <c>WMUX_PTY_ID</c> of the wmux pane: wmux runs its shells under a daemon without a window, so it is the
/// only handle that still leads to the pane once the hook's short-lived parent has exited.
/// </summary>
/// <remarks><see cref="Entrypoint"/> is Claude Code's <c>CLAUDE_CODE_ENTRYPOINT</c> ("cli", "claude-desktop", "sdk-ts"...).</remarks>
public sealed record HostInfo(int? Ppid, string? HerdrPane, string? WtSession, string? TermProgram, int? VscodePid, string? WmuxPty = null,
    string? Entrypoint = null)
{
    /// <summary>
    /// The origin an entrypoint stands for: the Claude desktop app (Code and Cowork) and the SDK hosts are
    /// <see cref="SessionOrigin.App"/>; the CLI, the IDE extensions and an unknown value are left to the caller (null).
    /// </summary>
    public static SessionOrigin? OriginOf(string? entrypoint) => entrypoint switch
    {
        "claude-desktop" or "claude-desktop-3p" or "local-agent" or "local_agent" or "sdk-ts" or "sdk-py" or "sdk-cli" => SessionOrigin.App,
        _ => null
    };
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
    TokenUsage Tokens,
    string? Model = null,
    UsageLedger? Ledger = null);

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
    IReadOnlyList<SubagentState>? Subagents = null,
    // A Stop that arrived while subagents were still running: the Idle transition waits for them.
    bool AwaitingSubagents = false,
    // Timestamp of the last SubagentStart/SubagentStop, used by the timeout sweep.
    DateTimeOffset? LastSubagentEventAt = null,
    // Terminal host of the session, from the latest SessionStart/UserPromptSubmit that carried one.
    HostInfo? Host = null,
    // Usage split by model and price variant, what the cost is computed from (null until the first read).
    UsageLedger? Ledger = null,
    // Where the session runs (terminal, app, cloud, routine).
    SessionOrigin Origin = SessionOrigin.Terminal,
    // Name given by its source (a cloud session's title, a routine's name): shown instead of the cwd folder.
    string? Title = null,
    // Background workflows still in flight according to the latest Stop/SubagentStop: while the session waits on
    // them, the gap between two phases of a workflow (no agent running) is not the end of the turn.
    int PendingWorkflows = 0)
{
    /// <summary>Italian label shown in the UI. Idle is "pronto" before the first completed turn, "finito" after.</summary>
    public string PhaseLabel => Phase switch
    {
        SessionPhase.Working when ActiveSubagents == 1 => "al lavoro · 1 agente",
        SessionPhase.Working when ActiveSubagents > 1 => $"al lavoro · {ActiveSubagents} agenti",
        SessionPhase.Working => "al lavoro",
        SessionPhase.NeedsInput => "attende input",
        SessionPhase.Idle => Message is null ? "pronto" : "finito",
        SessionPhase.Error => "errore",
        _ => Phase.ToString()
    };

    /// <summary>Number of subagents still running (Agent tool or workflow agents).</summary>
    public int ActiveSubagents => Subagents?.Count(s => s.Phase == SubagentPhase.Running) ?? 0;

    /// <summary>The live workflow rows; completed agents remain in history but are no longer displayed.</summary>
    public IEnumerable<SubagentState> RunningSubagents =>
        Subagents?.Where(s => s.Phase == SubagentPhase.Running) ?? [];

    public TokenUsage ActiveSubagentTokens => RunningSubagents.Aggregate(TokenUsage.Zero, (acc, s) => acc + s.Tokens);

    /// <summary>Sum of token usage across every known subagent (running and done).</summary>
    public TokenUsage SubagentTokens => Subagents is null
        ? TokenUsage.Zero
        : Subagents.Aggregate(TokenUsage.Zero, (acc, s) => acc + s.Tokens);

    /// <summary>Ledger of every known subagent (running and done): the cost of the whole conversation includes them.</summary>
    public UsageLedger SubagentLedger => Subagents is null
        ? UsageLedger.Empty
        : Subagents.Aggregate(UsageLedger.Empty, (acc, s) => s.Ledger is null ? acc : acc + s.Ledger);

    /// <summary>Ledger of the running subagents only, like <see cref="ActiveSubagentTokens"/>.</summary>
    public UsageLedger ActiveSubagentLedger =>
        RunningSubagents.Aggregate(UsageLedger.Empty, (acc, s) => s.Ledger is null ? acc : acc + s.Ledger);
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
    string? AgentType = null,
    string? TranscriptPath = null,
    string? AgentTranscriptPath = null,
    HostInfo? Host = null,
    // Stop/SubagentStop of Claude Code 2.1+: backgrounded agents and workflows still in flight; null when the payload
    // carried no list (Codex, older versions), which says nothing about what is running.
    IReadOnlyList<BackgroundTask>? BackgroundTasks = null,
    // Set by the app's own sources (session registry, cloud API); null for the lines written by hook.cjs.
    SessionOrigin? Origin = null,
    string? Title = null);
