using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class AppTokenSourceTests
{
    [Fact]
    public void Running_workflow_gets_tokens_and_model_before_stop_supplies_its_transcript_path()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.UtcNow;
        var transcript = dir.File("projects/session.jsonl", "");
        dir.File("projects/session/subagents/workflows/workflow/agent-live.jsonl",
            """{"type":"assistant","message":{"id":"msg-live","model":"claude-sonnet-test","usage":{"input_tokens":12,"output_tokens":8,"cache_read_input_tokens":20,"cache_creation_input_tokens":4}}}""" + "\n");
        var session = new SessionState(AgentKind.Claude, "session", "demo", null, SessionPhase.Working,
            null, now, now, TranscriptPath: transcript, Subagents:
            [new("live", "workflow", SubagentPhase.Running, now, null, null, TokenUsage.Zero)]);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(now));

        var tokens = source.SubagentTokens(session)!["live"];
        Assert.Equal(36, tokens.TotalInput);
        Assert.Equal(8, tokens.Output);
        Assert.Equal("claude-sonnet-test", source.SubagentModels(session)!["live"]);
        Assert.Null(source.SubagentModels(session with
        {
            Subagents = [session.Subagents![0] with { Phase = SubagentPhase.Done }]
        }));
    }

    [Fact]
    public void Missing_model_is_unknown_without_losing_available_tokens()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.UtcNow;
        var path = dir.File("agent.jsonl",
            """{"type":"assistant","requestId":"request","message":{"usage":{"input_tokens":12,"output_tokens":8}}}""" + "\n");
        var session = new SessionState(AgentKind.Claude, "s", "demo", null, SessionPhase.Working,
            null, now, now, Subagents:
            [new("a", "workflow", SubagentPhase.Running, now, null, path, TokenUsage.Zero)]);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(now));

        Assert.Equal(new TokenUsage(12, 8, 0, 0), source.SubagentTokens(session)!["a"]);
        Assert.Null(source.SubagentModels(session));
    }

    [Fact]
    public void Subagent_activity_is_the_last_write_to_its_transcript_found_while_it_runs()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.UtcNow;
        var transcript = dir.File("projects/session.jsonl", "");
        var agentFile = dir.File("projects/session/subagents/agent-live.jsonl", "{}\n");
        var written = new DateTime(2026, 9, 24, 9, 30, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(agentFile, written);
        var running = new SubagentState("live", "Explore", SubagentPhase.Running, now, null, null, TokenUsage.Zero);
        var session = new SessionState(AgentKind.Claude, "session", "demo", null, SessionPhase.Working,
            null, now, now, TranscriptPath: transcript, Subagents: [running]);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(now));

        Assert.Equal(new DateTimeOffset(written), source.SubagentLastActivity(session, running));
        Assert.Null(source.SubagentLastActivity(session, running with { AgentId = "missing" }));
        Assert.Null(source.SubagentLastActivity(session with { Agent = AgentKind.Codex }, running));
    }
}
