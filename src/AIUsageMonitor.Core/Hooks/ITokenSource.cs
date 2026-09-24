using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Where <see cref="HookEventPump"/> gets the token totals it pushes into the tracker. The App implements it on top
/// of <see cref="ClaudeTranscriptTokenCounter"/> (transcript paths) and <see cref="CodexTokenCounter"/> (rollouts).
/// Both members run on the pump thread and may do IO; returning <c>null</c> means "nothing to say about this
/// session right now" and leaves the totals already known untouched (a locked or missing transcript must never
/// reset a row to zero).
/// </summary>
public interface ITokenSource
{
    /// <summary>Total usage of the session itself, or null when it cannot be determined.</summary>
    TokenUsage? SessionTokens(SessionState session);

    /// <summary>Usage per subagent id (only the ids it knows about), or null when there is nothing to report.</summary>
    IReadOnlyDictionary<string, TokenUsage>? SubagentTokens(SessionState session);

    /// <summary>Model name per subagent id, when the source can determine it.</summary>
    IReadOnlyDictionary<string, string>? SubagentModels(SessionState session) => null;

    /// <summary>
    /// Usage of the session itself split by model and price variant (what the cost is computed from), or null when it
    /// cannot be determined. Called right after <see cref="SessionTokens"/>, on the pump thread; it may do IO.
    /// </summary>
    UsageLedger? SessionLedger(SessionState session) => null;

    /// <summary>Ledger per subagent id (only the ids it knows about), or null. Called right after <see cref="SubagentTokens"/>.</summary>
    IReadOnlyDictionary<string, UsageLedger>? SubagentLedgers(SessionState session) => null;

    /// <summary>
    /// When a running subagent last showed signs of life (for Claude, the last write to its transcript), or null when
    /// the source cannot tell. The subagent timeout sweep keeps an agent that is still active: one busy on a single
    /// long task sends no hook event for as long as that task lasts.
    /// </summary>
    DateTimeOffset? SubagentLastActivity(SessionState session, SubagentState subagent) => null;
}
