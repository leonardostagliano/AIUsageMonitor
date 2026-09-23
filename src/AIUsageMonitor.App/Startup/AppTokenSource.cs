using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Startup;

/// <summary>
/// The <see cref="ITokenSource"/> the pump asks for totals: Claude Code counts the transcript of the session and of
/// every subagent it spawned, Codex reads the rollout of the thread and of each child thread forward and incrementally
/// (<see cref="CodexUsageLedgerReader"/>). The ledgers come from the state those reads have just updated:
/// <see cref="SessionLedger"/> and <see cref="SubagentLedgers"/> read no file of their own.
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
/// <para>
/// A Codex total is the total of its ledger, not the newest cumulative total of the rollout (what
/// <see cref="CodexTokenCounter"/> reads): Codex restarts that total when it wakes a thread for a new task, and a
/// forked child's starts with the totals it copied from its parent. The ledger counts every restart and leaves the
/// copy out, so the tokens on a row are exactly the ones its cost prices.
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

    /// <summary>
    /// Child threads read for each Codex session. The tracker keeps only the newest
    /// <see cref="SessionTracker.MaxDoneSubagents"/> finished subagents: the state of a child it drops is released at
    /// the session's next read instead of staying until the app exits.
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _codexChildren = new(StringComparer.OrdinalIgnoreCase);

    public AppTokenSource(AppPaths paths, IClock clock) => _codex = new CodexTokenCounter(paths.CodexSessionsDir, clock);

    public TokenUsage? SessionTokens(SessionState session)
    {
        if (session.Agent == AgentKind.Codex)
            return ReadCodexLedger(session.SessionId) is { } ledger ? NullIfEmpty(ledger.ToTokenUsage()) : null;

        // The transcript path arrives with the hook payload: until the first event carrying one, there is nothing to read.
        if (string.IsNullOrWhiteSpace(session.TranscriptPath)) return null;
        return NullIfEmpty(_claude.Read(session.TranscriptPath));
    }

    public IReadOnlyDictionary<string, TokenUsage>? SubagentTokens(SessionState session)
    {
        if (session.Agent == AgentKind.Codex) ForgetDroppedChildren(session);
        if (session.Subagents is not { Count: > 0 }) return null;

        var totals = new Dictionary<string, TokenUsage>(StringComparer.Ordinal);
        if (session.Agent == AgentKind.Codex)
        {
            if (!_codexChildren.TryGetValue(session.SessionId, out var read))
                _codexChildren[session.SessionId] = read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var subagent in session.Subagents)
            {
                // A child thread is its own rollout, keyed by the thread id — the same id the scanner uses as agent id.
                read.Add(subagent.AgentId);
                if (ReadCodexLedger(subagent.AgentId, reuseKnownRollout: subagent.Phase == SubagentPhase.Done) is not { } ledger) continue;
                var usage = ledger.ToTokenUsage();
                if (usage.Total > 0) totals[subagent.AgentId] = usage;
            }
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
        // Same rollout SessionTokens has just read.
        if (session.Agent == AgentKind.Codex) return KnownCodexLedger(session.SessionId);
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
            if (session.Agent == AgentKind.Codex)
                // Same rollout SubagentTokens has just read.
                ledger = KnownCodexLedger(subagent.AgentId);
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

    /// <summary>
    /// The ledger of a Codex thread with everything appended to its rollout since the last read (possibly empty), or
    /// null when its rollout cannot be found right now.
    /// </summary>
    /// <param name="reuseKnownRollout">
    /// True for a finished child: the rollout it was last read from is read again as is. Resolving it anew would cost
    /// a recursive scan of the sessions directory per finished child every <see cref="CodexTokenCounter.CacheTtl"/>,
    /// while the incremental read of a known rollout only opens it and parses what was appended since.
    /// </param>
    private UsageLedger? ReadCodexLedger(string threadId, bool reuseKnownRollout = false)
    {
        string? path;
        if (!reuseKnownRollout || !_codexRollouts.TryGetValue(threadId, out path))
        {
            if (!_codex.TryResolveRollout(threadId, out path) || path is null) return null;
            // The thread now resolves to another rollout: the state read from the previous one would otherwise stay
            // until the app exits (Forget is always safe, a later Read rebuilds the state from the start of the file).
            if (_codexRollouts.TryGetValue(threadId, out var previous) && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
                _codexLedgers.Forget(previous);
            _codexRollouts[threadId] = path;
        }
        return _codexLedgers.Read(path);
    }

    /// <summary>The ledger of a Codex thread as last read, without any IO; null when it has none yet.</summary>
    private UsageLedger? KnownCodexLedger(string threadId) =>
        _codexRollouts.TryGetValue(threadId, out var path) ? NullIfEmpty(_codexLedgers.LedgerOf(path)) : null;

    /// <summary>Releases the ledger state of the child threads the tracker no longer lists for <paramref name="session"/>.</summary>
    private void ForgetDroppedChildren(SessionState session)
    {
        if (!_codexChildren.TryGetValue(session.SessionId, out var read)) return;
        var listed = new HashSet<string>(session.Subagents?.Select(s => s.AgentId) ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var dropped in read.Where(id => !listed.Contains(id)).ToList())
        {
            read.Remove(dropped);
            ForgetCodex(dropped);
        }
    }

    /// <summary>Drops the per-transcript state of a session that is gone, with that of its subagents.</summary>
    public void Forget(SessionState session)
    {
        if (session.Agent == AgentKind.Codex)
        {
            ForgetCodex(session.SessionId);
            foreach (var subagent in session.Subagents ?? []) ForgetCodex(subagent.AgentId);
            if (_codexChildren.Remove(session.SessionId, out var read))
                foreach (var child in read) ForgetCodex(child);
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
