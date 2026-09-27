namespace AIUsageMonitor.Core.Models;

/// <summary>What a session in <see cref="SessionPhase.NeedsInput"/> is waiting for.</summary>
public enum AttentionKind
{
    /// <summary>A tool waits for the user's permission (a command, a file edit, an MCP tool), or a request of unknown kind.</summary>
    Permission,

    /// <summary>Claude asks a question (AskUserQuestion) or an MCP server asks for an answer (elicitation).</summary>
    Question,

    /// <summary>Claude has a plan for the user to approve (ExitPlanMode).</summary>
    Plan,

    /// <summary>Input of another kind: an agent waiting for the user, a cloud session in <c>requires_action</c>.</summary>
    Input
}

/// <summary>
/// The detail of a wait, read from the tool_use still pending in the transcript when the notification arrived: the
/// tool (null for a question or a plan), one short line saying what it is about to do (a command, a file, the question;
/// at most 120 characters, shown on the card and never written to the log) and whether the request comes from an agent
/// working in the background rather than from the main session.
/// </summary>
public sealed record AttentionDetail(AttentionKind Kind, string? Tool = null, string? Summary = null, bool Background = false);
