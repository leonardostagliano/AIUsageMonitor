using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Startup;

/// <summary>
/// The <see cref="ITokenSource"/> the pump asks for totals: Claude Code counts the transcript of the session and of
/// every subagent it spawned, Codex reads the cumulative total of the thread and of its child threads. The ledgers
/// come from the same Claude counter state (no IO) and, for Codex, from an incremental forward read of the rollout.
/// </summary>
/// <remarks>
/// Every member runs on the pump thread — that is where <see cref="HookEventPump"/> does its IO — and so does
/// <see cref="Forget"/>, wired to the tracker's Removed change, which is raised by the pump's stale sweep. Neither
/// counter — nor the agent-transcript locator — is thread-safe, and that single thread is what keeps them safe.
/// <para>
/// A total of zero is reported as "nothing to say" (null): both counters return zero for a transcript they have not
/// been able to read yet, and turning that into an update would spend a Changed event — and a whole panel refresh —
/// on every session at startup for no visible difference.
/// </para>
/// </remarks>
public sealed class AppTokenSource : ITokenSource
{
    private readonly ClaudeTranscriptTokenCounter _claude = new();
    private readonly ClaudeAgentTranscriptLocator _claudeAgents = new();
    private readonly CodexTokenCounter _codex;
    private readonly CodexUsageLedgerReader _codexLedgers = new();

    /// <summary>Rollout read for each Codex thread id, so <see cref="Forget"/> can drop its ledger state.</summary>
    private readonly Dictionary<string, string> _codexRollouts = new(StringComparer.OrdinalIgnoreCase);

    public AppTokenSource(AppPaths paths, IClock clock) => _codex = new CodexTokenCounter(paths.CodexSessionsDir, clock);

    public TokenUsage? SessionTokens(SessionState session)
    {
        if (session.Agent == AgentKind.Codex)
            return _codex.TryReadThread(session.SessionId, out var thread) ? NullIfEmpty(thread) : null;

        // The transcript path arrives with the hook payload: until the first event carrying one, there is nothing to read.
        if (string.IsNullOrWhiteSpace(session.TranscriptPath)) return null;
        return NullIfEmpty(_claude.Read(session.TranscriptPath));
    }

    public IReadOnlyDictionary<string, TokenUsage>? SubagentTokens(SessionState session)
    {
        if (session.Subagents is not { Count: > 0 }) return null;

        var totals = new Dictionary<string, TokenUsage>(StringComparer.Ordinal);
        if (session.Agent == AgentKind.Codex)
        {
            // A child thread is its own rollout, keyed by the thread id — the same id the scanner uses as agent id.
            if (!_codex.TryReadChildren(session.SessionId, out var children) && children.Count == 0) return null;
            foreach (var child in children)
                if (child.Tokens.Total > 0) totals[child.ThreadId] = child.Tokens;
        }
        else
        {
            foreach (var subagent in session.Subagents)
            {
                // agent_transcript_path arrives only with SubagentStop: while the agent runs — exactly when its
                // count is worth watching — the path has to be found under the session's own directory.
                var path = subagent.TranscriptPath
                           ?? _claudeAgents.Locate(session.TranscriptPath, session.SessionId, subagent.AgentId);
                if (string.IsNullOrWhiteSpace(path)) continue;
                var usage = _claude.Read(path);
                if (usage.Total > 0) totals[subagent.AgentId] = usage;
            }
        }

        return totals.Count == 0 ? null : totals;
    }

    public IReadOnlyDictionary<string, string>? SubagentModels(SessionState session)
    {
        var models = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var subagent in session.RunningSubagents)
        {
            string? model;
            if (session.Agent == AgentKind.Codex)
                model = _codex.ReadModel(subagent.AgentId);
            else
            {
                var path = subagent.TranscriptPath
                           ?? _claudeAgents.Locate(session.TranscriptPath, session.SessionId, subagent.AgentId);
                model = path is null ? null : _claude.ReadModel(path);
            }
            if (!string.IsNullOrWhiteSpace(model)) models[subagent.AgentId] = model;
        }
        return models.Count == 0 ? null : models;
    }

    public UsageLedger? SessionLedger(SessionState session)
    {
        if (session.Agent == AgentKind.Codex) return CodexLedger(session.SessionId);
        if (string.IsNullOrWhiteSpace(session.TranscriptPath)) return null;
        return NullIfEmpty(_claude.LedgerOf(session.TranscriptPath));
    }

    public IReadOnlyDictionary<string, UsageLedger>? SubagentLedgers(SessionState session)
    {
        if (session.Subagents is not { Count: > 0 }) return null;

        var ledgers = new Dictionary<string, UsageLedger>(StringComparer.Ordinal);
        foreach (var subagent in session.Subagents)
        {
            UsageLedger? ledger;
            if (session.Agent == AgentKind.Codex) ledger = CodexLedger(subagent.AgentId);
            else
            {
                // Same path SubagentTokens has just read, so the counter state behind LedgerOf is current.
                var path = subagent.TranscriptPath
                           ?? _claudeAgents.Locate(session.TranscriptPath, session.SessionId, subagent.AgentId);
                ledger = string.IsNullOrWhiteSpace(path) ? null : NullIfEmpty(_claude.LedgerOf(path));
            }
            if (ledger is not null) ledgers[subagent.AgentId] = ledger;
        }
        return ledgers.Count == 0 ? null : ledgers;
    }

    private UsageLedger? CodexLedger(string threadId)
    {
        if (!_codex.TryResolveRollout(threadId, out var path) || path is null) return null;
        _codexRollouts[threadId] = path;
        return NullIfEmpty(_codexLedgers.Read(path));
    }

    /// <summary>Drops the per-transcript state of a session that is gone, with that of its subagents.</summary>
    public void Forget(SessionState session)
    {
        if (session.Agent == AgentKind.Codex)
        {
            ForgetCodex(session.SessionId);
            foreach (var subagent in session.Subagents ?? []) ForgetCodex(subagent.AgentId);
            return;
        }
        if (!string.IsNullOrWhiteSpace(session.TranscriptPath)) _claude.Forget(session.TranscriptPath);
        foreach (var subagent in session.Subagents ?? [])
        {
            if (!string.IsNullOrWhiteSpace(subagent.TranscriptPath)) _claude.Forget(subagent.TranscriptPath);
            // The path found while the agent was running is its own cache entry and its own counter state, and
            // survives the one SubagentStop later put on the subagent: both go with the session.
            if (_claudeAgents.Forget(subagent.AgentId) is { } located) _claude.Forget(located);
        }
    }

    private void ForgetCodex(string threadId)
    {
        if (_codexRollouts.Remove(threadId, out var path)) _codexLedgers.Forget(path);
    }

    private static TokenUsage? NullIfEmpty(TokenUsage usage) => usage.Total > 0 ? usage : null;

    private static UsageLedger? NullIfEmpty(UsageLedger ledger) => ledger.IsEmpty ? null : ledger;
}
