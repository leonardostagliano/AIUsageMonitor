namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Finds the transcript of a Claude Code subagent from the parent session's transcript, for the whole time the agent
/// is still running.
/// </summary>
/// <remarks>
/// Only <c>SubagentStop</c> carries <c>agent_transcript_path</c> (verified with the probe of the plan's Task 1), so a
/// Running subagent has no path of its own and would show no token total at all until it finished. The layout is
/// documented and stable: the agents of the session <c>&lt;proj&gt;/&lt;session&gt;.jsonl</c> write under
/// <c>&lt;proj&gt;/&lt;session&gt;/subagents/</c> — directly for an Agent-tool agent, under
/// <c>subagents/workflows/&lt;wf&gt;/</c> for a workflow agent — as <c>agent-&lt;agent_id&gt;.jsonl</c>, the same id the
/// hook event carries. Searching that subtree for that one file name gives the Running agent its transcript straight
/// away; <c>agent_transcript_path</c> stays authoritative once <c>SubagentStop</c> delivers it.
/// <para>
/// A hit is cached per agent id (a session's subtree holds hundreds of files and the pump re-reads the totals every
/// 30 s), a miss is not: the agent may simply not have written its first line yet.
/// </para>
/// </remarks>
/// <remarks>Not thread-safe: like the counters, the pump calls it from its own single thread.</remarks>
public sealed class ClaudeAgentTranscriptLocator
{
    private readonly Dictionary<string, string> _resolved = new(StringComparer.Ordinal);

    /// <summary>Directory searches performed (a cached hit performs none). Exposed for the tests.</summary>
    public int Searches { get; private set; }

    /// <summary>
    /// Transcript of the subagent <paramref name="agentId"/> of the session whose own transcript is
    /// <paramref name="sessionTranscriptPath"/>, or null when it cannot be found (yet).
    /// </summary>
    /// <remarks>Never throws: an unreadable or missing directory is just a miss.</remarks>
    public string? Locate(string? sessionTranscriptPath, string sessionId, string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        if (_resolved.TryGetValue(agentId, out var cached)) return cached;
        if (string.IsNullOrWhiteSpace(sessionTranscriptPath) || string.IsNullOrWhiteSpace(sessionId)) return null;
        // The id becomes part of a search pattern and of a path: anything but a plain file-name token (a wildcard, a
        // separator, the synthetic "anon:<n>" of a subagent event without agent_id) would either throw or match the
        // transcript of a different agent.
        if (!IsPlainToken(agentId) || !IsPlainToken(sessionId)) return null;

        try
        {
            var projectDir = Path.GetDirectoryName(sessionTranscriptPath);
            if (string.IsNullOrEmpty(projectDir)) return null;
            var root = Path.Combine(projectDir, sessionId, "subagents");
            if (!Directory.Exists(root)) return null;

            var fileName = $"agent-{agentId}.jsonl";
            Searches++;
            foreach (var candidate in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
            {
                // Windows can match a search pattern against the 8.3 name of a file as well: only an exact name is
                // the transcript we are after (the sidecar "agent-<id>.meta.json" is never it).
                if (!string.Equals(Path.GetFileName(candidate), fileName, StringComparison.OrdinalIgnoreCase)) continue;
                _resolved[agentId] = candidate;
                return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Treated as a miss: the next refresh tries again.
        }

        return null;
    }

    /// <summary>
    /// Drops the cached path of <paramref name="agentId"/> and returns it, so the caller can release the state its
    /// token counter keeps for that file.
    /// </summary>
    public string? Forget(string agentId) => _resolved.Remove(agentId, out var path) ? path : null;

    /// <summary>True when the string can be used verbatim inside a file name: no wildcard, no separator, no colon.</summary>
    private static bool IsPlainToken(string value)
    {
        foreach (var c in value)
        {
            if (c is '*' or '?' or ':' or '"' or '<' or '>' or '|' or '/' or '\\') return false;
            if (char.IsControl(c)) return false;
        }
        return value != "." && value != "..";
    }
}
