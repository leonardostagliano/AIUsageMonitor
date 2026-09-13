using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Tests;

public class ModelsTests
{
    [Theory]
    [InlineData(AgentKind.Claude, "Claude Code", "claude")]
    [InlineData(AgentKind.Codex, "Codex", "codex")]
    public void AgentKind_has_display_name_and_key(AgentKind kind, string display, string key)
    {
        Assert.Equal(display, kind.DisplayName());
        Assert.Equal(key, kind.Key());
        Assert.True(AgentKindExtensions.TryParseKey(key, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void TryParseKey_rejects_unknown()
    {
        Assert.False(AgentKindExtensions.TryParseKey("gemini", out _));
        Assert.False(AgentKindExtensions.TryParseKey(null, out _));
    }

    [Theory]
    [InlineData(0, Severity.Normal)]
    [InlineData(49.9, Severity.Normal)]
    [InlineData(50, Severity.Warning)]
    [InlineData(79.9, Severity.Warning)]
    [InlineData(80, Severity.Critical)]
    [InlineData(100, Severity.Critical)]
    public void Severity_thresholds(double percent, Severity expected) =>
        Assert.Equal(expected, SeverityRules.FromPercent(percent));

    [Fact]
    public void Severity_from_api_takes_the_worse_of_api_and_threshold()
    {
        Assert.Equal(Severity.Critical, SeverityRules.FromApi("critical", 10));
        Assert.Equal(Severity.Warning, SeverityRules.FromApi("warning", 10));
        Assert.Equal(Severity.Critical, SeverityRules.FromApi("normal", 95));
        Assert.Equal(Severity.Normal, SeverityRules.FromApi(null, 10));
    }

    [Fact]
    public void Session_phase_labels_are_italian()
    {
        var now = DateTimeOffset.UtcNow;
        SessionState Make(SessionPhase p, string? msg) => new(AgentKind.Claude, "s", "demo", null, p, msg, now, now);
        SessionState WithRunning(int n) => Make(SessionPhase.Working, null) with
        {
            Subagents = Enumerable.Range(0, n)
                .Select(i => new SubagentState("a" + i, null, SubagentPhase.Running, now, null, null, TokenUsage.Zero))
                .ToList()
        };
        Assert.Equal("al lavoro", Make(SessionPhase.Working, null).PhaseLabel);
        Assert.Equal("al lavoro · 1 agente", WithRunning(1).PhaseLabel);
        Assert.Equal("al lavoro · 3 agenti", WithRunning(3).PhaseLabel);
        Assert.Equal("attende input", Make(SessionPhase.NeedsInput, "x").PhaseLabel);
        Assert.Equal("pronto", Make(SessionPhase.Idle, null).PhaseLabel);
        Assert.Equal("finito", Make(SessionPhase.Idle, "Turno completato").PhaseLabel);
        Assert.Equal("errore", Make(SessionPhase.Error, "boom").PhaseLabel);
    }

    [Fact]
    public void TokenUsage_total_sums_all_buckets_and_operator_adds_componentwise()
    {
        var a = new TokenUsage(1, 2, 3, 4);
        var b = new TokenUsage(10, 20, 30, 40);
        Assert.Equal(10, a.Total);
        Assert.Equal(new TokenUsage(11, 22, 33, 44), a + b);
        Assert.Equal(TokenUsage.Zero, new TokenUsage(0, 0, 0, 0));
    }

    [Fact]
    public void ActiveSubagents_counts_only_running_and_SubagentTokens_sums_all()
    {
        var now = DateTimeOffset.UtcNow;
        var running = new SubagentState("a1", "general-purpose", SubagentPhase.Running, now, null, null, new TokenUsage(1, 1, 0, 0));
        var done = new SubagentState("a2", "workflow", SubagentPhase.Done, now, now, "path", new TokenUsage(2, 2, 0, 0));
        var session = new SessionState(AgentKind.Claude, "s", "demo", null, SessionPhase.Working, null, now, now,
            Subagents: [running, done]);

        Assert.Equal(1, session.ActiveSubagents);
        Assert.Equal(new TokenUsage(3, 3, 0, 0), session.SubagentTokens);
    }

    [Fact]
    public void ActiveSubagents_and_SubagentTokens_are_zero_when_no_subagents()
    {
        var now = DateTimeOffset.UtcNow;
        var session = new SessionState(AgentKind.Claude, "s", "demo", null, SessionPhase.Idle, null, now, now);
        Assert.Equal(0, session.ActiveSubagents);
        Assert.Equal(TokenUsage.Zero, session.SubagentTokens);
    }

    [Fact]
    public void AppPaths_are_rooted_on_the_given_dirs()
    {
        var p = new AppPaths(@"C:\home", @"C:\lad");
        Assert.Equal(@"C:\home\.claude\.credentials.json", p.ClaudeCredentialsFile);
        Assert.Equal(@"C:\home\.codex\sessions", p.CodexSessionsDir);
        Assert.Equal(@"C:\home\.aiusagemonitor\events.jsonl", p.EventsFile);
        Assert.Equal(@"C:\lad\settings.json", p.SettingsFile);
    }
}
