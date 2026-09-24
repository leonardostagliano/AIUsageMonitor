using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class SessionTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    private static HookEvent Ev(string evt, string sid = "s1", AgentKind agent = AgentKind.Claude, string? cwd = @"C:\Users\demo\AIUsageMonitor",
        string? notificationType = null, string? message = null, string? source = null, int plusSeconds = 0,
        string? agentId = null, string? agentType = null, string? transcriptPath = null, string? agentTranscriptPath = null,
        HostInfo? host = null) =>
        new(T0.AddSeconds(plusSeconds), agent, evt, sid, cwd, notificationType, message, source, agentId, agentType,
            transcriptPath, agentTranscriptPath, host);

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
    public void Host_is_stored_on_SessionStart_kept_through_Stop_and_replaced_by_a_later_UserPromptSubmit()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var pane1 = new HostInfo(24716, "w15:p1", "wt-guid", null, null);
        tracker.Apply(Ev("SessionStart", host: pane1));
        Assert.Equal(pane1, tracker.Sessions.Single().Host);

        // Stop carries no host: the one already stored must survive.
        tracker.Apply(Ev("Stop", plusSeconds: 1));
        Assert.Equal(pane1, tracker.Sessions.Single().Host);

        var pane2 = new HostInfo(1234, "w2:p3", null, "vscode", 555);
        tracker.Apply(Ev("UserPromptSubmit", plusSeconds: 2, host: pane2));
        Assert.Equal(pane2, tracker.Sessions.Single().Host);
    }

    [Fact]
    public void Host_survives_a_SubagentStart_and_SubagentStop_which_carry_none()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var pane = new HostInfo(24716, "w15:p1", "wt-guid", null, null);
        tracker.Apply(Ev("SessionStart", host: pane));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        Assert.Equal(pane, tracker.Sessions.Single().Host);
        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 2));
        Assert.Equal(pane, tracker.Sessions.Single().Host);
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
        // Waiting only for the next prompt does not hide a session at work; a permission prompt does.
        tracker.Apply(Ev("Notification", sid: "c", notificationType: "idle_prompt"));
        Assert.Equal(SessionPhase.Working, tracker.AggregatePhase(AgentKind.Claude));
        tracker.Apply(Ev("Notification", sid: "e", notificationType: "permission_prompt"));
        Assert.Equal(SessionPhase.NeedsInput, tracker.AggregatePhase(AgentKind.Claude));
        tracker.Apply(Ev("StopFailure", sid: "d"));
        Assert.Equal(SessionPhase.Error, tracker.AggregatePhase(AgentKind.Claude));
        Assert.Null(tracker.AggregatePhase(AgentKind.Codex));
    }

    [Theory]
    [InlineData(@"C:\Users\demo\Progetti\Demo Azure\", "abcdefghijkl", "Demo Azure")]
    [InlineData("/home/demo/repo", "abcdefghijkl", "repo")]
    [InlineData(null, "abcdefghijkl", "abcdefgh")]
    [InlineData("", "short", "short")]
    public void DisplayNameFor(string? cwd, string id, string expected) => Assert.Equal(expected, SessionTracker.DisplayNameFor(cwd, id));

    [Fact]
    public void A_throwing_changed_subscriber_is_reported_and_does_not_break_apply()
    {
        var errors = new List<Exception>();
        var tracker = new SessionTracker(new FakeClock(T0)) { OnError = errors.Add };
        tracker.Changed += _ => throw new InvalidOperationException("cross-thread WPF access");

        var change = tracker.Apply(Ev("UserPromptSubmit"));

        Assert.NotNull(change);
        Assert.Single(tracker.Sessions);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));

        tracker.Apply(Ev("Stop", plusSeconds: 1));
        Assert.Equal(SessionPhase.Idle, tracker.Sessions.Single().Phase);
    }

    [Fact]
    public void A_throwing_cwd_resolver_degrades_to_the_session_id()
    {
        var errors = new List<Exception>();
        var tracker = new SessionTracker(new FakeClock(T0), (_, _) => throw new ArgumentException("NUL in the session id")) { OnError = errors.Add };

        tracker.Apply(Ev("UserPromptSubmit", sid: "abcdefghijkl", cwd: null));

        var session = Assert.Single(tracker.Sessions);
        Assert.Null(session.Cwd);
        Assert.Equal("abcdefgh", session.DisplayName);
        Assert.IsType<ArgumentException>(Assert.Single(errors));
    }

    [Fact]
    public void A_throwing_changed_subscriber_does_not_break_the_stale_sweep()
    {
        var clock = new FakeClock(T0);
        var errors = new List<Exception>();
        var tracker = new SessionTracker(clock) { OnError = errors.Add };
        tracker.Apply(Ev("UserPromptSubmit", sid: "a"));
        tracker.Apply(Ev("UserPromptSubmit", sid: "b"));
        tracker.Changed += _ => throw new InvalidOperationException("boom");
        clock.Advance(TimeSpan.FromHours(13));

        Assert.Equal(2, tracker.RemoveStale(TimeSpan.FromHours(12)).Count);
        Assert.Empty(tracker.Sessions);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void Subagents_keep_the_session_working_after_stop_until_they_finish()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        tracker.Apply(Ev("UserPromptSubmit", plusSeconds: 1));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", agentType: "general-purpose", plusSeconds: 2));
        tracker.Apply(Ev("SubagentStart", agentId: "a2", agentType: "general-purpose", plusSeconds: 3));
        Assert.Equal("al lavoro · 2 agenti", tracker.Sessions.Single().PhaseLabel);

        tracker.Apply(Ev("Stop", message: "Workflow lanciato.", plusSeconds: 4));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.True(s.AwaitingSubagents);
        Assert.Equal("al lavoro · 2 agenti", s.PhaseLabel);

        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 5));
        Assert.Equal("al lavoro · 1 agente", tracker.Sessions.Single().PhaseLabel);
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase);

        tracker.Apply(Ev("SubagentStop", agentId: "a2", plusSeconds: 6));
        s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("finito", s.PhaseLabel);
        Assert.Equal("Workflow lanciato.", s.Message);
        Assert.False(s.AwaitingSubagents);
        Assert.Equal(0, s.ActiveSubagents);
        Assert.Equal(2, s.Subagents!.Count);
        Assert.All(s.Subagents, sub => Assert.Equal(SubagentPhase.Done, sub.Phase));
        var last = changes.Last();
        Assert.Equal(SessionPhase.Working, last.PreviousPhase); // the toast "finito" fires here, not at Stop
    }

    [Fact]
    public void Subagents_finishing_before_stop_do_not_change_the_phase()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 2));
        Assert.Equal("al lavoro", tracker.Sessions.Single().PhaseLabel);
        tracker.Apply(Ev("Stop", plusSeconds: 3));
        Assert.Equal("finito", tracker.Sessions.Single().PhaseLabel);
    }

    [Fact]
    public void SubagentStop_never_goes_below_zero_and_SubagentStart_wakes_an_idle_session()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("Stop"));
        tracker.Apply(Ev("SubagentStop", agentId: "ghost", plusSeconds: 1));
        Assert.Equal(0, tracker.Sessions.Single().ActiveSubagents);
        Assert.Equal(SessionPhase.Idle, tracker.Sessions.Single().Phase);
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 2));
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase);
    }

    [Fact]
    public void A_subagent_that_wakes_an_idle_session_re_arms_the_deferred_idle()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("Stop", message: "Workflow lanciato.", plusSeconds: 1));
        Assert.Equal(SessionPhase.Idle, tracker.Sessions.Single().Phase);

        // A late workflow agent starts after the turn's Stop: the session goes back to work...
        tracker.Apply(Ev("SubagentStart", agentId: "late", plusSeconds: 2));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.True(s.AwaitingSubagents);
        Assert.Equal("al lavoro · 1 agente", s.PhaseLabel);

        // ...and its SubagentStop must bring it back to Idle instead of pinning it to "al lavoro".
        tracker.Apply(Ev("SubagentStop", agentId: "late", plusSeconds: 3));
        s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("finito", s.PhaseLabel);
        Assert.Equal("Workflow lanciato.", s.Message);
        Assert.False(s.AwaitingSubagents);
        Assert.Equal(0, s.ActiveSubagents);
    }

    [Fact]
    public void A_second_wave_of_subagents_after_the_stop_still_ends_in_idle()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("Stop", message: "Workflow lanciato.", plusSeconds: 2));
        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 3));
        Assert.Equal(SessionPhase.Idle, tracker.Sessions.Single().Phase);

        // Sequential workflow: the next agent starts only after the previous one finished.
        tracker.Apply(Ev("SubagentStart", agentId: "a2", plusSeconds: 4));
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase);
        tracker.Apply(Ev("SubagentStop", agentId: "a2", plusSeconds: 5));

        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("finito", s.PhaseLabel);
        Assert.Equal("Workflow lanciato.", s.Message);
        Assert.False(s.AwaitingSubagents);
        Assert.Equal(0, s.ActiveSubagents);
        Assert.Equal(SessionPhase.Working, changes.Last().PreviousPhase);
    }

    [Fact]
    public void The_last_subagent_does_not_clear_an_error_or_a_pending_input()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("Stop", message: "Workflow lanciato.", plusSeconds: 2));
        tracker.Apply(Ev("StopFailure", message: "Errore API", plusSeconds: 3));
        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 4));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Error, s.Phase);
        Assert.Equal("Errore API", s.Message);
        // The phase survives, but the deferred Stop is spent: keeping the flag with no running agent left
        // would latch it forever (the timeout sweep only looks at sessions with running subagents).
        Assert.Equal(0, s.ActiveSubagents);
        Assert.False(s.AwaitingSubagents);
    }

    [Fact]
    public void A_deferred_stop_spent_on_an_error_does_not_fake_a_later_finito()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("Stop", message: "Workflow lanciato.", plusSeconds: 2));
        tracker.Apply(Ev("StopFailure", message: "Errore API", plusSeconds: 3));
        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 4));
        Assert.False(tracker.Sessions.Single().AwaitingSubagents);

        // With the counter back to zero the sweep never visits this session, so a stale flag could not be
        // cleared by anything: the next agent of the same turn would end on a "finito" no Stop announced.
        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30)));

        tracker.Apply(Ev("PostToolUse", plusSeconds: 5));
        tracker.Apply(Ev("SubagentStart", agentId: "a2", plusSeconds: 6));
        tracker.Apply(Ev("SubagentStop", agentId: "a2", plusSeconds: 7));
        var after = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, after.Phase);
        Assert.Equal("al lavoro", after.PhaseLabel);
        Assert.False(after.AwaitingSubagents);
    }

    [Fact]
    public void Subagents_without_an_agent_id_are_not_folded_into_one_entry()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", plusSeconds: 1));
        tracker.Apply(Ev("SubagentStart", plusSeconds: 2));
        Assert.Equal(2, tracker.Sessions.Single().ActiveSubagents);
        Assert.Equal("al lavoro · 2 agenti", tracker.Sessions.Single().PhaseLabel);

        tracker.Apply(Ev("Stop", message: "Workflow lanciato.", plusSeconds: 3));
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase);

        // The first anonymous SubagentStop closes the oldest running anonymous agent, not both of them.
        tracker.Apply(Ev("SubagentStop", plusSeconds: 4));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.Equal(1, s.ActiveSubagents);
        var done = s.Subagents!.Single(sub => sub.Phase == SubagentPhase.Done);
        Assert.Equal(T0.AddSeconds(1), done.StartedAt);
        Assert.Equal(T0.AddSeconds(4), done.EndedAt);

        tracker.Apply(Ev("SubagentStop", plusSeconds: 5));
        s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal(0, s.ActiveSubagents);
        Assert.Equal(2, s.Subagents!.Count);
    }

    [Fact]
    public void New_prompt_clears_awaiting_flag_but_keeps_the_counter()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("Stop", plusSeconds: 2));
        tracker.Apply(Ev("UserPromptSubmit", plusSeconds: 3));
        var s = tracker.Sessions.Single();
        Assert.False(s.AwaitingSubagents);
        Assert.Equal(1, s.ActiveSubagents);
        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 4));
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase); // still inside the new turn
    }

    [Fact]
    public void Subagent_events_keep_the_state_the_tracker_does_not_own_and_record_the_agent()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", agentType: "Explore", plusSeconds: 1));
        var s = tracker.Sessions.Single();
        var sub = Assert.Single(s.Subagents!);
        Assert.Equal("a1", sub.AgentId);
        Assert.Equal("Explore", sub.AgentType);
        Assert.Equal(SubagentPhase.Running, sub.Phase);
        Assert.Equal(T0.AddSeconds(1), sub.StartedAt);
        Assert.Null(sub.EndedAt);
        Assert.Equal(TokenUsage.Zero, sub.Tokens);
        Assert.Equal(T0.AddSeconds(1), s.LastSubagentEventAt);

        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 2));
        sub = Assert.Single(tracker.Sessions.Single().Subagents!);
        Assert.Equal(SubagentPhase.Done, sub.Phase);
        Assert.Equal(T0.AddSeconds(2), sub.EndedAt);
        Assert.Equal("Explore", sub.AgentType);
    }

    [Fact]
    public void Transcript_paths_land_on_the_session_and_on_the_subagent()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit", transcriptPath: @"C:\p\s1.jsonl"));
        Assert.Equal(@"C:\p\s1.jsonl", tracker.Sessions.Single().TranscriptPath);

        // A SubagentStop arrives with the agent transcript only: it lands on the subagent, and the session keeps
        // the path it already knows (the parent transcript_path of a subagent event is not taken).
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        Assert.Null(Assert.Single(tracker.Sessions.Single().Subagents!).TranscriptPath);

        tracker.Apply(Ev("SubagentStop", agentId: "a1", plusSeconds: 2,
            transcriptPath: @"C:\p\other.jsonl", agentTranscriptPath: @"C:\p\s1\subagents\agent-a1.jsonl"));
        var s = tracker.Sessions.Single();
        Assert.Equal(@"C:\p\s1.jsonl", s.TranscriptPath);
        Assert.Equal(@"C:\p\s1\subagents\agent-a1.jsonl", Assert.Single(s.Subagents!).TranscriptPath);

        // An event without a transcript path never clears what is already known.
        tracker.Apply(Ev("Stop", plusSeconds: 3));
        Assert.Equal(@"C:\p\s1.jsonl", tracker.Sessions.Single().TranscriptPath);
        Assert.Equal(@"C:\p\s1\subagents\agent-a1.jsonl", Assert.Single(tracker.Sessions.Single().Subagents!).TranscriptPath);
    }

    [Fact]
    public void An_unmatched_subagent_stop_keeps_its_agent_transcript_path()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStop", agentId: "a9", plusSeconds: 1,
            agentTranscriptPath: @"C:\p\s1\subagents\agent-a9.jsonl"));
        var sub = Assert.Single(tracker.Sessions.Single().Subagents!);
        Assert.Equal(SubagentPhase.Done, sub.Phase);
        Assert.Equal(@"C:\p\s1\subagents\agent-a9.jsonl", sub.TranscriptPath);
    }

    [Fact]
    public void The_first_event_of_a_session_carries_its_transcript_path()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("SessionStart", source: "startup", transcriptPath: @"C:\p\s1.jsonl"));
        Assert.Equal(@"C:\p\s1.jsonl", tracker.Sessions.Single().TranscriptPath);
    }

    [Fact]
    public void Only_the_fifty_most_recent_finished_subagents_are_kept()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        for (var i = 0; i < 60; i++)
        {
            tracker.Apply(Ev("SubagentStart", agentId: "a" + i, plusSeconds: 1 + i * 2));
            tracker.Apply(Ev("SubagentStop", agentId: "a" + i, plusSeconds: 2 + i * 2));
        }
        var subs = tracker.Sessions.Single().Subagents!;
        Assert.Equal(50, subs.Count);
        Assert.Equal("a10", subs[0].AgentId);
        Assert.Equal("a59", subs[^1].AgentId);
    }

    [Fact]
    public void Subagent_timeout_releases_a_stuck_session()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("Stop", message: "done", plusSeconds: 2));
        clock.Advance(TimeSpan.FromMinutes(31));
        var changes = tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30));
        var s = Assert.Single(changes).Session;
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal(0, s.ActiveSubagents);
        Assert.Equal("done", s.Message);
        Assert.Equal(SubagentPhase.Done, Assert.Single(s.Subagents!).Phase);
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30)));
    }

    [Fact]
    public void Subagent_timeout_sweep_can_run_without_raising_changed()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("Stop", message: "done", plusSeconds: 2));
        tracker.Changed += changes.Add;
        clock.Advance(TimeSpan.FromMinutes(31));

        tracker.SweepSubagentTimeoutsSilently(TimeSpan.FromMinutes(30));

        Assert.Empty(changes);
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal(0, s.ActiveSubagents);
        Assert.False(s.AwaitingSubagents);
    }

    [Fact]
    public void Subagent_timeout_leaves_a_session_that_is_still_working_in_working()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        clock.Advance(TimeSpan.FromMinutes(31));
        var s = Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30))).Session;
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.Equal("al lavoro", s.PhaseLabel);
        Assert.Equal(0, s.ActiveSubagents);
    }

    [Fact]
    public void UpdateTokens_stores_the_session_and_subagent_totals_and_raises_one_update()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("SubagentStart", agentId: "a2", plusSeconds: 2));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        var change = tracker.UpdateTokens(AgentKind.Claude, "s1", new TokenUsage(10, 20, 30, 40),
            new Dictionary<string, TokenUsage> { ["a1"] = new(1, 2, 3, 4), ["a2"] = new(5, 6, 7, 8) });

        Assert.Equal(SessionChangeKind.Updated, change!.Kind);
        // The phase does not move on a token refresh, so the App never toasts "Turno completato" for one.
        Assert.Equal(SessionPhase.Working, change.PreviousPhase);
        Assert.Equal(SessionPhase.Working, change.Session.Phase);
        var s = Assert.Single(tracker.Sessions);
        Assert.Equal(new TokenUsage(10, 20, 30, 40), s.Tokens);
        Assert.Equal(new TokenUsage(6, 8, 10, 12), s.SubagentTokens);
        Assert.Equal(2, s.ActiveSubagents);
        Assert.Equal(T0.AddSeconds(2), s.LastEventAt); // a refresh is not an event: the stale sweep must not be pushed back
        Assert.Single(changes);
    }

    [Fact]
    public void UpdateTokens_updates_model_metadata_without_changing_phase_or_activity_time()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit", sid: "s1", plusSeconds: 1));
        tracker.Apply(Ev("SubagentStart", sid: "s1", agentId: "a1", plusSeconds: 2));
        var before = tracker.Sessions.Single();
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        var change = tracker.UpdateTokens(AgentKind.Claude, "s1", null, null,
            new Dictionary<string, string> { ["a1"] = "claude-sonnet-4" });

        Assert.NotNull(change);
        var after = change!.Session;
        Assert.Equal(before.Phase, after.Phase);
        Assert.Equal(before.LastEventAt, after.LastEventAt);
        Assert.Equal("claude-sonnet-4", Assert.Single(after.Subagents!).Model);
        Assert.Equal(before.Tokens, after.Tokens);
        Assert.Single(changes);
    }

    [Fact]
    public void UpdateTokens_ignores_null_or_blank_model_metadata()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit", sid: "s1", plusSeconds: 1));
        tracker.Apply(Ev("SubagentStart", sid: "s1", agentId: "a1", plusSeconds: 2));
        Assert.Null(tracker.UpdateTokens(AgentKind.Claude, "s1", null, null,
            new Dictionary<string, string> { ["a1"] = " " }));
        Assert.Null(Assert.Single(tracker.Sessions).Subagents!.Single().Model);
    }

    [Fact]
    public void UpdateTokens_raises_nothing_when_the_totals_did_not_change()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        var subagents = new Dictionary<string, TokenUsage> { ["a1"] = new(1, 2, 3, 4) };
        tracker.UpdateTokens(AgentKind.Claude, "s1", new TokenUsage(10, 20, 30, 40), subagents);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        Assert.Null(tracker.UpdateTokens(AgentKind.Claude, "s1", new TokenUsage(10, 20, 30, 40), subagents));

        Assert.Empty(changes);
    }

    [Fact]
    public void UpdateTokens_keeps_the_known_totals_when_a_counter_reports_nothing()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.UpdateTokens(AgentKind.Claude, "s1", new TokenUsage(10, 20, 30, 40),
            new Dictionary<string, TokenUsage> { ["a1"] = new(1, 2, 3, 4) });

        Assert.Null(tracker.UpdateTokens(AgentKind.Claude, "s1", null, null));

        var s = Assert.Single(tracker.Sessions);
        Assert.Equal(new TokenUsage(10, 20, 30, 40), s.Tokens);
        Assert.Equal(new TokenUsage(1, 2, 3, 4), s.SubagentTokens);
    }

    [Fact]
    public void UpdateTokens_updates_only_one_subagent_and_ignores_unknown_ids()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("SubagentStart", agentId: "a2", plusSeconds: 2));
        tracker.UpdateTokens(AgentKind.Claude, "s1", null,
            new Dictionary<string, TokenUsage> { ["a1"] = new(1, 0, 0, 0), ["a2"] = new(2, 0, 0, 0) });

        var change = tracker.UpdateTokens(AgentKind.Claude, "s1", null,
            new Dictionary<string, TokenUsage> { ["a2"] = new(5, 0, 0, 0), ["ghost"] = new(9, 0, 0, 0) });

        var s = change!.Session;
        Assert.Equal(new TokenUsage(1, 0, 0, 0), s.Subagents!.Single(x => x.AgentId == "a1").Tokens);
        Assert.Equal(new TokenUsage(5, 0, 0, 0), s.Subagents!.Single(x => x.AgentId == "a2").Tokens);
        Assert.Equal(2, s.Subagents!.Count);
    }

    [Fact]
    public void UpdateTokens_ignores_a_session_it_does_not_know()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        Assert.Null(tracker.UpdateTokens(AgentKind.Claude, "nope", new TokenUsage(1, 1, 1, 1), null));

        Assert.Empty(changes);
        Assert.Empty(tracker.Sessions);
    }

    [Fact]
    public void UpdateTokensSilently_stores_the_totals_without_raising_changed()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("Stop", message: "done", plusSeconds: 2));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        tracker.UpdateTokensSilently(AgentKind.Claude, "s1", new TokenUsage(10, 20, 30, 40),
            new Dictionary<string, TokenUsage> { ["a1"] = new(1, 2, 3, 4) });

        Assert.Empty(changes);
        var s = Assert.Single(tracker.Sessions);
        Assert.Equal(new TokenUsage(10, 20, 30, 40), s.Tokens);
        Assert.Equal(new TokenUsage(1, 2, 3, 4), s.SubagentTokens);
        Assert.Equal(T0.AddSeconds(2), s.LastEventAt);
    }
}
