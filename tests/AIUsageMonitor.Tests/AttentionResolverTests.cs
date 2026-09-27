using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Tests.Helpers;
using static AIUsageMonitor.Tests.Helpers.TranscriptLines;

namespace AIUsageMonitor.Tests;

public class AttentionResolverTests
{
    private const string SessionId = "3a9e5c10-7b2f-4d8e-9c61-5e0f2a7b4c18";
    private const string Cwd = @"C:\Users\demo\Progetti\Demo";

    /// <summary>Temp layout of Claude Code: projects/proj/&lt;session&gt;.jsonl and its agents under projects/proj/&lt;session&gt;/subagents.</summary>
    private sealed class Layout : IDisposable
    {
        private readonly TempDir _dir = new();

        public string Session(params string[] lines) => _dir.File($"projects/proj/{SessionId}.jsonl", Jsonl(lines));

        public string Agent(string relative, params string[] lines) => _dir.File($"projects/proj/{SessionId}/subagents/{relative}", Jsonl(lines));

        public string Directory => _dir.Path;

        public void Dispose() => _dir.Dispose();
    }

    private static readonly string[] Answered =
    [
        Prompt("lancia il workflow", 0),
        ToolUse("m1", "t1", "Workflow", new { description = "Revisione" }, 2),
        Result("t1", 3),
        Text("m2", "Workflow avviato.", 4)
    ];

    private static HookEvent Notification(string type, string? transcriptPath, string? source = null, string? message = null,
        AgentKind agent = AgentKind.Claude) =>
        new(At(60), agent, "Notification", SessionId, Cwd, type, message ?? "Claude needs your permission", source,
            TranscriptPath: transcriptPath);

    private static SubagentState Running(string agentId, string? transcriptPath = null) =>
        new(agentId, "general-purpose", SubagentPhase.Running, At(1), null, transcriptPath, TokenUsage.Zero);

    private static SessionState Session(string? transcriptPath, params SubagentState[] subagents) =>
        new(AgentKind.Claude, SessionId, "Demo", Cwd, SessionPhase.Working, null, At(0), At(0),
            TranscriptPath: transcriptPath, Subagents: subagents.Length == 0 ? null : subagents);

    [Fact]
    public void Only_live_attention_notifications_of_claude_are_resolved()
    {
        using var layout = new Layout();
        var transcript = layout.Session(ToolUse("m1", "t1", "Bash", new { command = "make" }, 1));
        var resolver = new AttentionResolver();

        Assert.Null(resolver.Resolve(Notification("idle_prompt", transcript), null));
        Assert.Null(resolver.Resolve(Notification("auth_success", transcript), null));
        Assert.Null(resolver.Resolve(Notification("permission_prompt", transcript, agent: AgentKind.Codex), null));
        Assert.Null(resolver.Resolve(Notification("permission_prompt", transcript) with { Event = "Stop" }, null));
        Assert.Null(resolver.Resolve(Notification("permission_prompt", transcript) with { NotificationType = null }, null));
        Assert.Contains("PERMISSION_PROMPT", AttentionResolver.AttentionTypes);
        Assert.DoesNotContain("idle_prompt", AttentionResolver.AttentionTypes);
    }

    [Fact]
    public void A_permission_names_the_request_pending_in_the_session_transcript()
    {
        using var layout = new Layout();
        var transcript = layout.Session(Prompt("lancia i test", 0), ToolUse("m1", "t1", "Bash", new { command = "dotnet test" }, 2));

        var detail = new AttentionResolver().Resolve(Notification("permission_prompt", transcript), Session(transcript));

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash", "dotnet test"), detail);
    }

    [Fact]
    public void The_transcript_and_cwd_known_to_the_session_are_used_when_the_event_has_none()
    {
        using var layout = new Layout();
        var transcript = layout.Session(ToolUse("m1", "t1", "Edit", new { file_path = Cwd + @"\src\Program.cs" }, 2));
        var bare = Notification("permission_prompt", null) with { Cwd = null };

        var detail = new AttentionResolver().Resolve(bare, Session(transcript));

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Edit", @"src\Program.cs"), detail);
    }

    [Fact]
    public void A_question_or_a_plan_of_the_main_session_wins_for_every_type()
    {
        using var layout = new Layout();
        var transcript = layout.Session(ToolUse("m1", "t1", "AskUserQuestion", new { questions = new[] { new { question = "Rilascio?" } } }, 2));
        var agent = layout.Agent("agent-a1.jsonl", ToolUse("x1", "u1", "Bash", new { command = "npm test" }, 50));
        var session = Session(transcript, Running("a1", agent));
        var resolver = new AttentionResolver();

        var question = new AttentionDetail(AttentionKind.Question, null, "Rilascio?");
        Assert.Equal(question, resolver.Resolve(Notification("permission_prompt", transcript), session));
        Assert.Equal(question, resolver.Resolve(Notification("worker_permission_prompt", transcript), session));

        var plan = layout.Session(ToolUse("m2", "t2", "ExitPlanMode", new { plan = "# Piano\nPasso 1" }, 3));
        Assert.Equal(new AttentionDetail(AttentionKind.Plan, null, "Piano"), resolver.Resolve(Notification("permission_prompt", plan), session));
    }

    [Fact]
    public void Without_a_main_request_the_latest_one_of_a_running_agent_is_taken_as_background()
    {
        using var layout = new Layout();
        var transcript = layout.Session(Answered);
        // a1 has no transcript path yet (only SubagentStop brings it): the locator finds it under subagents/.
        layout.Agent("workflows/wf_01/agent-a1.jsonl", ToolUse("x1", "u1", "Bash", new { command = "npm test" }, 50));
        var a2 = layout.Agent("agent-a2.jsonl", ToolUse("x2", "u2", "Edit", new { file_path = Cwd + @"\README.md" }, 55));
        // A finished agent is not asked, even with a newer tool_use left open.
        var a3 = layout.Agent("agent-a3.jsonl", ToolUse("x3", "u3", "Bash", new { command = "rm -rf dist" }, 58));
        var done = new SubagentState("a3", "general-purpose", SubagentPhase.Done, At(1), At(59), a3, TokenUsage.Zero);
        var locator = new ClaudeAgentTranscriptLocator();
        var resolver = new AttentionResolver(locator);

        var detail = resolver.Resolve(Notification("permission_prompt", transcript), Session(transcript, Running("a1"), Running("a2", a2), done));

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Edit", "README.md", Background: true), detail);
        Assert.Equal(1, locator.Searches); // the injected locator did the search for a1
    }

    [Fact]
    public void A_running_agent_found_through_the_locator_is_read()
    {
        using var layout = new Layout();
        var transcript = layout.Session(Answered);
        layout.Agent("workflows/wf_01/agent-a1.jsonl", ToolUse("x1", "u1", "Bash", new { command = "npm test" }, 50));

        var detail = new AttentionResolver().Resolve(Notification("permission_prompt", transcript), Session(transcript, Running("a1")));

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash", "npm test", Background: true), detail);
    }

    [Fact]
    public void With_nothing_pending_a_permission_is_generic_and_in_the_background_only_while_work_is_in_flight()
    {
        using var layout = new Layout();
        var transcript = layout.Session(Answered);
        var resolver = new AttentionResolver();
        var evt = Notification("permission_prompt", transcript);

        Assert.Equal(new AttentionDetail(AttentionKind.Permission), resolver.Resolve(evt, Session(transcript)));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission), resolver.Resolve(evt, null));
        // A running agent whose transcript cannot be found yet.
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, Background: true), resolver.Resolve(evt, Session(transcript, Running("a9"))));
        // A background workflow between two of its phases.
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, Background: true),
            resolver.Resolve(evt, Session(transcript) with { PendingWorkflows = 1 }));
        // No transcript at all, or one that cannot be read.
        Assert.Equal(new AttentionDetail(AttentionKind.Permission), resolver.Resolve(Notification("permission_prompt", null), null));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission), resolver.Resolve(Notification("permission_prompt", layout.Directory), null));
    }

    [Fact]
    public void A_worker_permission_looks_only_at_the_agents()
    {
        using var layout = new Layout();
        var transcript = layout.Session(ToolUse("m1", "t1", "Bash", new { command = "git push" }, 2));
        var agent = layout.Agent("agent-a1.jsonl", ToolUse("x1", "u1", "Bash", new { command = "npm test" }, 50));
        var resolver = new AttentionResolver();

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash", "npm test", Background: true),
            resolver.Resolve(Notification("worker_permission_prompt", transcript), Session(transcript, Running("a1", agent))));
        // The main session's own pending command is not the worker's request.
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, Background: true),
            resolver.Resolve(Notification("worker_permission_prompt", transcript), Session(transcript)));
    }

    [Fact]
    public void A_pending_agent_call_gives_way_to_what_its_running_agent_waits_on()
    {
        using var layout = new Layout();
        var transcript = layout.Session(ToolUse("m1", "t1", "Agent", new { description = "Esplora il login", prompt = "..." }, 2));
        var agent = layout.Agent("agent-a1.jsonl", ToolUse("x1", "u1", "Bash", new { command = "grep -r login src" }, 30));
        var resolver = new AttentionResolver();

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash", "grep -r login src", Background: true),
            resolver.Resolve(Notification("permission_prompt", transcript), Session(transcript, Running("a1", agent))));
        // Spec §4.2 step 2: while agents run the Agent call counts as nothing pending. When they have nothing pending
        // either (or their transcript is not found yet), the permission is generic and in the background: the agent is
        // already running, so the card must not read as a request to launch it.
        var idle = layout.Agent("agent-a2.jsonl", ToolUse("x2", "u2", "Bash", new { command = "ls" }, 30), Result("u2", 31));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, Background: true),
            resolver.Resolve(Notification("permission_prompt", transcript), Session(transcript, Running("a2", idle))));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, Background: true),
            resolver.Resolve(Notification("permission_prompt", transcript), Session(transcript, Running("a3"))));
        // No agent at work: the Agent call itself is what waits.
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Agent", "Esplora il login"),
            resolver.Resolve(Notification("permission_prompt", transcript), Session(transcript)));
    }

    [Theory]
    [InlineData("elicitation_dialog")]
    [InlineData("elicitation_url_dialog")]
    public void An_elicitation_is_a_question_with_the_message_of_the_event(string type)
    {
        using var layout = new Layout();
        // The MCP call that asked is pending, but it is a question from the server, not a permission.
        var transcript = layout.Session(ToolUse("m1", "t1", "mcp__demo_server__login", new { user = "demo" }, 2));

        var detail = new AttentionResolver().Resolve(Notification(type, transcript, message: "Il server   chiede un codice"), Session(transcript));

        Assert.Equal(new AttentionDetail(AttentionKind.Question, null, "Il server chiede un codice"), detail);
    }

    [Fact]
    public void An_agent_waiting_for_input_is_input_in_the_background()
    {
        using var layout = new Layout();
        var transcript = layout.Session(Answered);

        Assert.Equal(new AttentionDetail(AttentionKind.Input, Background: true),
            new AttentionResolver().Resolve(Notification("agent_needs_input", transcript), Session(transcript)));
    }

    [Fact]
    public void A_cloud_session_waits_for_input_with_its_pending_action()
    {
        var resolver = new AttentionResolver();

        Assert.Equal(new AttentionDetail(AttentionKind.Input, null, "Permesso richiesto: Bash"),
            resolver.Resolve(Notification("permission_prompt", null, source: "cloud", message: "Permesso richiesto: Bash"), null));
        Assert.Equal(new AttentionDetail(AttentionKind.Input),
            resolver.Resolve(Notification("permission_prompt", null, source: "cloud") with { Message = null }, null));
    }

    [Fact]
    public void A_wait_reported_by_the_session_registry_reads_the_transcript_like_a_hook()
    {
        using var layout = new Layout();
        var transcript = layout.Session(ToolUse("m1", "t1", "WebFetch", new { url = "https://example.com/a?b=c" }, 2));

        var detail = new AttentionResolver().Resolve(
            Notification("permission_prompt", transcript, source: "registry", message: "Permesso richiesto"), null);

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "WebFetch", "https://example.com/a"), detail);
    }

    [Fact]
    public void A_transcript_that_cannot_be_read_is_logged_by_the_error_type_alone()
    {
        using var layout = new Layout();
        var transcript = layout.Session(Prompt("pubblica", 0), ToolUse("m1", "t1", "Bash", new { command = "git push --force" }, 2));
        var logged = new List<string>();
        var resolver = new AttentionResolver { LogError = logged.Add };

        // No transcript to read, and one read fine: nothing to log.
        Assert.Equal(new AttentionDetail(AttentionKind.Permission), resolver.Resolve(Notification("permission_prompt", null), null));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash", "git push --force"),
            resolver.Resolve(Notification("permission_prompt", transcript), Session(transcript)));
        Assert.Empty(logged);

        using (new FileStream(transcript, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(new AttentionDetail(AttentionKind.Permission), resolver.Resolve(Notification("permission_prompt", transcript), Session(transcript)));

        // Neither the path (its folder names the session) nor the command.
        Assert.Equal("Dettaglio dell'attesa: transcript non leggibile (IOException)", Assert.Single(logged));
    }

    [Fact]
    public void An_unexpected_failure_gives_the_generic_detail_and_logs_only_its_type()
    {
        using var layout = new Layout();
        var transcript = layout.Session(Answered);
        var broken = Session(transcript) with { Subagents = new BrokenList() };
        var logged = new List<string>();

        var detail = new AttentionResolver { LogError = logged.Add }.Resolve(Notification("permission_prompt", transcript), broken);

        Assert.Equal(new AttentionDetail(AttentionKind.Permission), detail);
        Assert.Equal("Dettaglio dell'attesa non risolto (InvalidOperationException)", Assert.Single(logged));
        // A logger that throws does not make the resolver throw either.
        var noisy = new AttentionResolver { LogError = _ => throw new InvalidOperationException("log rotto") };
        Assert.Equal(new AttentionDetail(AttentionKind.Permission), noisy.Resolve(Notification("permission_prompt", transcript), broken));
    }

    /// <summary>A subagent list that fails when read: it stands for any unexpected failure inside the resolution.</summary>
    private sealed class BrokenList : IReadOnlyList<SubagentState>
    {
        public SubagentState this[int index] => throw new InvalidOperationException("elenco rotto");

        public int Count => throw new InvalidOperationException("elenco rotto");

        public IEnumerator<SubagentState> GetEnumerator() => throw new InvalidOperationException("elenco rotto");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
