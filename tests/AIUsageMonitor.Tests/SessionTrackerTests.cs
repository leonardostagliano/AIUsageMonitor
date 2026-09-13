using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class SessionTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private static HookEvent Ev(string evt, string sid = "s1", AgentKind agent = AgentKind.Claude, string? cwd = @"C:\Users\demo\AIUsageMonitor",
        string? notificationType = null, string? message = null, string? source = null, int plusSeconds = 0) =>
        new(T0.AddSeconds(plusSeconds), agent, evt, sid, cwd, notificationType, message, source);

    [Fact]
    public void SessionStart_creates_an_idle_session_named_after_cwd()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var change = tracker.Apply(Ev("SessionStart", source: "startup"))!;
        Assert.Equal(SessionChangeKind.Added, change.Kind);
        var s = Assert.Single(tracker.Sessions);
        Assert.Equal("AIUsageMonitor", s.DisplayName);
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("pronto", s.PhaseLabel);
        Assert.Equal(T0, s.StartedAt);
    }

    [Fact]
    public void Full_turn_lifecycle()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        tracker.Apply(Ev("UserPromptSubmit", plusSeconds: 1));
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase);

        tracker.Apply(Ev("Notification", notificationType: "permission_prompt", message: "Bash needs approval", plusSeconds: 2));
        Assert.Equal(SessionPhase.NeedsInput, tracker.Sessions.Single().Phase);
        Assert.Equal("Bash needs approval", tracker.Sessions.Single().Message);

        tracker.Apply(Ev("Notification", notificationType: "auth_success", plusSeconds: 3));
        Assert.Equal(SessionPhase.NeedsInput, tracker.Sessions.Single().Phase); // ignored type: no transition

        tracker.Apply(Ev("Stop", message: "Ho finito la modifica.", plusSeconds: 4));
        var done = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, done.Phase);
        Assert.Equal("finito", done.PhaseLabel);
        Assert.Equal("Ho finito la modifica.", done.Message);
        Assert.Equal(T0.AddSeconds(4), done.LastEventAt);
        Assert.Equal(T0.AddSeconds(1), done.StartedAt);

        var stop = changes.Last();
        Assert.Equal(SessionChangeKind.Updated, stop.Kind);
        Assert.Equal(SessionPhase.NeedsInput, stop.PreviousPhase);

        tracker.Apply(Ev("SessionEnd", plusSeconds: 5));
        Assert.Empty(tracker.Sessions);
        Assert.Equal(SessionChangeKind.Removed, changes.Last().Kind);
    }

    [Fact]
    public void Stop_without_message_and_StopFailure()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("Stop"));
        Assert.Equal("Turno completato", tracker.Sessions.Single().Message);
        tracker.Apply(Ev("StopFailure", message: "rate limited", plusSeconds: 1));
        Assert.Equal(SessionPhase.Error, tracker.Sessions.Single().Phase);
        Assert.Equal("rate limited", tracker.Sessions.Single().Message);
        tracker.Apply(Ev("StopFailure", sid: "s2", plusSeconds: 2));
        Assert.Equal("Errore API", tracker.Sessions.Single(s => s.SessionId == "s2").Message);
    }

    [Theory]
    [InlineData("permission_prompt")]
    [InlineData("idle_prompt")]
    [InlineData("agent_needs_input")]
    [InlineData("elicitation_dialog")]
    [InlineData("elicitation_url_dialog")]
    public void NeedsInput_notification_types(string type)
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("Notification", notificationType: type));
        Assert.Equal(SessionPhase.NeedsInput, tracker.Sessions.Single().Phase);
    }

    [Fact]
    public void PostToolUse_returns_to_working_and_unknown_events_are_ignored()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("Notification", notificationType: "agent_needs_input"));
        Assert.Null(tracker.Apply(Ev("PreCompact", plusSeconds: 1)));
        tracker.Apply(Ev("PostToolUse", plusSeconds: 2));
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase);
        Assert.Null(tracker.Apply(Ev("SessionEnd", sid: "never-seen")));
    }

    [Fact]
    public void Messages_are_truncated_to_120_chars()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("Stop", message: new string('a', 200)));
        Assert.Equal(120, tracker.Sessions.Single().Message!.Length);
    }

    [Fact]
    public void Cwd_resolver_is_used_when_the_event_has_no_cwd()
    {
        var tracker = new SessionTracker(new FakeClock(T0), (agent, id) => agent == AgentKind.Codex && id == "c1" ? @"D:\work\liferay-ws" : null);
        tracker.Apply(Ev("UserPromptSubmit", sid: "c1", agent: AgentKind.Codex, cwd: null));
        Assert.Equal("liferay-ws", tracker.Sessions.Single().DisplayName);
        tracker.Apply(Ev("UserPromptSubmit", sid: "c2", agent: AgentKind.Codex, cwd: null));
        Assert.Equal("c2", tracker.Sessions.Single(s => s.SessionId == "c2").DisplayName);
    }

    [Fact]
    public void Resume_keeps_started_at_and_message()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("Stop", message: "done"));
        tracker.Apply(Ev("SessionStart", source: "resume", plusSeconds: 60));
        var s = tracker.Sessions.Single();
        Assert.Equal(T0, s.StartedAt);
        Assert.Equal("done", s.Message);
        Assert.Equal("finito", s.PhaseLabel);
    }

    [Fact]
    public void RemoveStale_drops_sessions_without_recent_events()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("Stop", sid: "old"));
        tracker.Apply(Ev("Stop", sid: "fresh", plusSeconds: 3600 * 11));
        clock.Advance(TimeSpan.FromHours(12.5));
        var removed = tracker.RemoveStale(TimeSpan.FromHours(12));
        Assert.Equal("old", Assert.Single(removed).Session.SessionId);
        Assert.Equal("fresh", tracker.Sessions.Single().SessionId);
    }

    [Fact]
    public void AggregatePhase_uses_priority_error_needsinput_working_idle()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        Assert.Null(tracker.AggregatePhase(AgentKind.Claude));
        tracker.Apply(Ev("Stop", sid: "a"));
        Assert.Equal(SessionPhase.Idle, tracker.AggregatePhase(AgentKind.Claude));
        tracker.Apply(Ev("UserPromptSubmit", sid: "b"));
        Assert.Equal(SessionPhase.Working, tracker.AggregatePhase(AgentKind.Claude));
        tracker.Apply(Ev("Notification", sid: "c", notificationType: "idle_prompt"));
        Assert.Equal(SessionPhase.NeedsInput, tracker.AggregatePhase(AgentKind.Claude));
        tracker.Apply(Ev("StopFailure", sid: "d"));
        Assert.Equal(SessionPhase.Error, tracker.AggregatePhase(AgentKind.Claude));
        Assert.Null(tracker.AggregatePhase(AgentKind.Codex));
    }

    [Theory]
    [InlineData(@"C:\Users\demo\Progetti\Vivisol Azure\", "abcdefghijkl", "Vivisol Azure")]
    [InlineData("/home/demo/repo", "abcdefghijkl", "repo")]
    [InlineData(null, "abcdefghijkl", "abcdefgh")]
    [InlineData("", "short", "short")]
    public void DisplayNameFor(string? cwd, string id, string expected) => Assert.Equal(expected, SessionTracker.DisplayNameFor(cwd, id));
}
