using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>
/// Says what a live attention notification of Claude Code waits for (spec 2026-09-27 §4.2): the tool_use pending in the
/// session's transcript, or in the transcript of one of its running agents, and a generic detail when neither says.
/// </summary>
/// <remarks>
/// The pump calls it for the live events only, before the tracker applies them: the replay of the last 24 h must not
/// read transcripts whose state has long moved on. Not thread-safe, like the <see cref="ClaudeAgentTranscriptLocator"/>
/// it owns: the pump calls it from its own single thread.
/// </remarks>
public sealed class AttentionResolver
{
    /// <summary>The notification types that put a session in NeedsInput (idle_prompt is not one of them).</summary>
    public static readonly IReadOnlySet<string> AttentionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "permission_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input", "worker_permission_prompt"
    };

    private readonly ClaudeAgentTranscriptLocator _locator;

    public AttentionResolver(ClaudeAgentTranscriptLocator? locator = null) => _locator = locator ?? new ClaudeAgentTranscriptLocator();

    /// <summary>
    /// Where a failure is reported (the App wires its log): one line naming the exception type only. The message of
    /// an IO exception holds the transcript path, whose folder names the session, and what a tool is about to do (a
    /// command, a question) is never logged.
    /// </summary>
    public Action<string>? LogError { get; init; }

    /// <summary>null unless e is a Claude "Notification" whose NotificationType is in AttentionTypes. Never throws.</summary>
    /// <param name="session">The session as tracked before this event (null for a session the tracker does not know yet).</param>
    public AttentionDetail? Resolve(HookEvent e, SessionState? session)
    {
        if (e.Agent != AgentKind.Claude || e.Event != "Notification" || e.NotificationType is not { } type || !AttentionTypes.Contains(type))
            return null;

        // A cloud session has no local transcript: its pending_action message is all there is.
        if (e.Source == "cloud") return new AttentionDetail(AttentionKind.Input, Summary: Shorten(e.Message));

        try
        {
            return ResolveCore(e, type, session);
        }
        catch (Exception ex)
        {
            // Nothing about a notification may stop the monitoring: the card falls back to the generic detail, worked
            // out without the session in case the session is what failed.
            Log($"Dettaglio dell'attesa non risolto ({ex.GetType().Name})");
            return Generic(type, e, null);
        }
    }

    private AttentionDetail ResolveCore(HookEvent e, string type, SessionState? session)
    {
        var transcript = e.TranscriptPath ?? session?.TranscriptPath;
        var cwd = e.Cwd ?? session?.Cwd;
        var main = TranscriptAttentionReader.FindPending(transcript, cwd, ReadFailed);

        // A question or a plan of the main session wins whatever the notification says: AskUserQuestion sends
        // permission_prompt too, and only the main session asks questions or presents plans.
        if (main?.Detail.Kind is AttentionKind.Question or AttentionKind.Plan) return main.Detail;

        if (IsType(type, "elicitation_dialog") || IsType(type, "elicitation_url_dialog")
            || IsType(type, "agent_needs_input"))
            return Generic(type, e, session);

        if (IsType(type, "worker_permission_prompt"))
            return FromAgents(session, transcript, cwd) ?? Generic(type, e, session);

        // permission_prompt: the main session's own request first. A pending Agent/Task call is the agent at work,
        // not a request, while agents run: what they wait on is more precise than the agent's description.
        if (main is not null && !(main.Detail.Tool is "Agent" or "Task" && (session?.ActiveSubagents ?? 0) > 0))
            return main.Detail;
        return FromAgents(session, transcript, cwd) ?? main?.Detail ?? Generic(type, e, session);
    }

    /// <summary>The most recent tool_use pending in the transcript of a running agent of the session, as a background request.</summary>
    private AttentionDetail? FromAgents(SessionState? session, string? sessionTranscript, string? cwd)
    {
        if (session is null) return null;
        PendingToolUse? latest = null;
        foreach (var agent in session.RunningSubagents)
        {
            var path = agent.TranscriptPath ?? _locator.Locate(sessionTranscript, session.SessionId, agent.AgentId);
            if (TranscriptAttentionReader.FindPending(path, cwd, ReadFailed) is not { } pending) continue;
            if (latest is null || (pending.At ?? DateTimeOffset.MinValue) > (latest.At ?? DateTimeOffset.MinValue)) latest = pending;
        }
        return latest is null ? null : latest.Detail with { Background = true };
    }

    /// <summary>Step 3 of the spec: what the notification alone says.</summary>
    private static AttentionDetail Generic(string type, HookEvent e, SessionState? session)
    {
        if (IsType(type, "elicitation_dialog") || IsType(type, "elicitation_url_dialog"))
            return new AttentionDetail(AttentionKind.Question, Summary: Shorten(e.Message));
        if (IsType(type, "agent_needs_input"))
            return new AttentionDetail(AttentionKind.Input, Background: true);
        var background = IsType(type, "worker_permission_prompt")
                         || session is not null && (session.ActiveSubagents > 0 || session.PendingWorkflows > 0);
        return new AttentionDetail(AttentionKind.Permission, Background: background);
    }

    private static bool IsType(string type, string expected) => string.Equals(type, expected, StringComparison.OrdinalIgnoreCase);

    /// <summary>The reader's report of a transcript it could not open or read: that transcript says nothing, the rest goes on.</summary>
    private void ReadFailed(Exception ex) => Log($"Dettaglio dell'attesa: transcript non leggibile ({ex.GetType().Name})");

    private void Log(string message)
    {
        try { LogError?.Invoke(message); } catch { /* a broken logger must not take the pump down */ }
    }

    private static string? Shorten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var shortened = ToolSummary.Shorten(text);
        return shortened.Length == 0 ? null : shortened;
    }
}
