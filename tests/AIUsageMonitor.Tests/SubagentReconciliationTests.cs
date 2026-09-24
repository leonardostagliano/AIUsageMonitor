using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

/// <summary>
/// The <c>background_tasks</c> list of Claude Code's Stop/SubagentStop, the pending workflows and the activity-aware
/// subagent timeout.
/// </summary>
public class SubagentReconciliationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    private static HookEvent Ev(string evt, int plusSeconds = 0, string? agentId = null, string? agentType = null,
        IReadOnlyList<BackgroundTask>? tasks = null, string? message = null, AgentKind agent = AgentKind.Claude) =>
        new(T0.AddSeconds(plusSeconds), agent, evt, "s1", @"C:\p\demo", null, message, null, agentId, agentType,
            BackgroundTasks: tasks);

    private static BackgroundTask Agent(string id, string? type = "general-purpose") => new(id, BackgroundTask.SubagentType, type);
    private static BackgroundTask Workflow(string id) => new(id, BackgroundTask.WorkflowType, Name: "review");

    [Fact]
    public void Stop_marks_the_agents_it_leaves_out_as_done_and_waits_for_the_others()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        tracker.Apply(Ev("SubagentStart", 2, "a2"));

        // a1 was killed without a SubagentStop: Claude Code no longer lists it.
        tracker.Apply(Ev("Stop", 3, tasks: [Agent("a2")], message: "Lanciati."));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.True(s.AwaitingSubagents);
        Assert.Equal("al lavoro · 1 agente", s.PhaseLabel);
        Assert.Equal(SubagentPhase.Done, s.Subagents!.Single(a => a.AgentId == "a1").Phase);
        Assert.Equal(T0.AddSeconds(3), s.Subagents!.Single(a => a.AgentId == "a1").EndedAt);

        tracker.Apply(Ev("SubagentStop", 4, "a2", tasks: []));
        s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("Lanciati.", s.Message);
    }

    [Fact]
    public void Stop_with_an_empty_list_ends_the_turn_even_when_a_SubagentStop_was_lost()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        tracker.Apply(Ev("Stop", 2, tasks: []));

        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("finito", s.PhaseLabel);
        Assert.Equal(0, s.ActiveSubagents);
        Assert.False(s.AwaitingSubagents);
        Assert.Equal(SessionPhase.Working, changes.Last().PreviousPhase);
    }

    [Fact]
    public void Stop_adds_a_running_agent_it_never_saw_start_but_not_the_backgrounded_main_session()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("Stop", 1, tasks: [Agent("x9", "Explore"), Agent("s1abcdefg", "main-session")]));

        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.True(s.AwaitingSubagents);
        var added = Assert.Single(s.Subagents!);
        Assert.Equal("x9", added.AgentId);
        Assert.Equal("Explore", added.AgentType);
        Assert.Equal(SubagentPhase.Running, added.Phase);
        Assert.Equal(T0.AddSeconds(1), s.LastSubagentEventAt);
    }

    [Fact]
    public void A_finished_agent_still_listed_by_a_racing_Stop_stays_finished()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        tracker.Apply(Ev("SubagentStop", 2, "a1", tasks: [Agent("a1")]));
        tracker.Apply(Ev("Stop", 3, tasks: [Agent("a1")]));

        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal(SubagentPhase.Done, s.Subagents!.Single().Phase);
    }

    [Fact]
    public void Stop_without_a_list_keeps_the_agents_as_the_events_left_them()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        tracker.Apply(Ev("Stop", 2));

        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.True(s.AwaitingSubagents);
        Assert.Equal(1, s.ActiveSubagents);
    }

    [Fact]
    public void A_SubagentStop_list_never_closes_the_foreground_agents_running_next_to_it()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        tracker.Apply(Ev("SubagentStart", 2, "a2"));
        // Foreground agents are not "background tasks": the list is empty while a2 still runs.
        tracker.Apply(Ev("SubagentStop", 3, "a1", tasks: []));

        var s = tracker.Sessions.Single();
        Assert.Equal(1, s.ActiveSubagents);
        Assert.Equal(SubagentPhase.Running, s.Subagents!.Single(a => a.AgentId == "a2").Phase);
    }

    [Fact]
    public void A_workflow_between_two_phases_keeps_the_session_working_until_its_last_stop()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "p1"));
        tracker.Apply(Ev("Stop", 2, tasks: [Agent("p1"), Workflow("wf1")], message: "Workflow avviato."));
        Assert.Equal(1, tracker.Sessions.Single().PendingWorkflows);

        // Phase 1 is over, phase 2 has not started: no agent runs, but the workflow does.
        tracker.Apply(Ev("SubagentStop", 3, "p1", tasks: [Workflow("wf1")]));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.True(s.AwaitingSubagents);
        Assert.DoesNotContain(changes, c => c.Session.Phase == SessionPhase.Idle);

        tracker.Apply(Ev("SubagentStart", 4, "p2"));
        tracker.Apply(Ev("SubagentStop", 5, "p2", tasks: [Workflow("wf1")]));
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase);

        // The workflow ends and wakes the session, which closes its turn with nothing in flight.
        tracker.Apply(Ev("Stop", 6, tasks: [], message: "Review completata."));
        s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("Review completata.", s.Message);
        Assert.Equal(0, s.PendingWorkflows);
        Assert.False(s.AwaitingSubagents);
        Assert.Single(changes, c => c.Session.Phase == SessionPhase.Idle && c.PreviousPhase == SessionPhase.Working);
    }

    [Fact]
    public void The_sweep_keeps_an_agent_whose_transcript_is_still_being_written()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        tracker.Apply(Ev("Stop", 2));
        clock.Advance(TimeSpan.FromMinutes(45));

        DateTimeOffset? activity = clock.UtcNow - TimeSpan.FromMinutes(2);
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => activity));
        Assert.Equal(1, tracker.Sessions.Single().ActiveSubagents);

        // The transcript goes quiet: now the timeout applies.
        activity = clock.UtcNow - TimeSpan.FromMinutes(31);
        var change = Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => activity));
        Assert.Equal(SessionPhase.Idle, change.Session.Phase);
        Assert.Equal(0, change.Session.ActiveSubagents);
    }

    [Fact]
    public void The_sweep_releases_one_quiet_agent_and_keeps_the_busy_one()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "busy"));
        tracker.Apply(Ev("SubagentStart", 2, "quiet"));
        tracker.Apply(Ev("Stop", 3));
        clock.Advance(TimeSpan.FromMinutes(40));

        var change = Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30),
            (_, sub) => sub.AgentId == "busy" ? clock.UtcNow : null));
        Assert.Equal(SessionPhase.Working, change.Session.Phase);
        Assert.True(change.Session.AwaitingSubagents);
        Assert.Equal("busy", change.Session.RunningSubagents.Single().AgentId);
    }

    [Fact]
    public void A_throwing_activity_probe_is_reported_and_counts_as_no_evidence()
    {
        var clock = new FakeClock(T0);
        var errors = new List<Exception>();
        var tracker = new SessionTracker(clock) { OnError = errors.Add };
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        clock.Advance(TimeSpan.FromMinutes(31));

        Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => throw new IOException("locked")));
        Assert.Single(errors);
        Assert.Equal(0, tracker.Sessions.Single().ActiveSubagents);
    }

    [Fact]
    public void A_session_waiting_only_on_a_silent_workflow_is_released_by_the_sweep()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("Stop", 1, tasks: [Workflow("wf1")], message: "Workflow avviato."));
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single().Phase);

        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30)));
        clock.Advance(TimeSpan.FromMinutes(11));
        var change = Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30)));
        Assert.Equal(SessionPhase.Idle, change.Session.Phase);
        Assert.Equal(0, change.Session.PendingWorkflows);
        Assert.False(change.Session.AwaitingSubagents);
    }

    [Fact]
    public void Origin_and_title_come_from_the_source_and_survive_later_events()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(new HookEvent(T0, AgentKind.Claude, "SessionStart", "session_01abc", null, null, null, null,
            Origin: SessionOrigin.Cloud, Title: "Raccolta subagenti"));
        tracker.Apply(new HookEvent(T0.AddSeconds(1), AgentKind.Claude, "UserPromptSubmit", "session_01abc", null, null, null, null));

        var s = tracker.Sessions.Single();
        Assert.Equal(SessionOrigin.Cloud, s.Origin);
        Assert.Equal("Raccolta subagenti", s.DisplayName);
        Assert.Equal("Raccolta subagenti", s.Title);
    }

    [Fact]
    public void A_hook_line_from_the_desktop_app_marks_the_session_as_an_app_session()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        var host = new HostInfo(1234, null, null, null, null, Entrypoint: "claude-desktop");
        tracker.Apply(new HookEvent(T0, AgentKind.Claude, "SessionStart", "s1", @"C:\p\demo", null, null, null, Host: host));
        tracker.Apply(Ev("Stop", 1));

        var s = tracker.Sessions.Single();
        Assert.Equal(SessionOrigin.App, s.Origin);
        Assert.Equal("demo", s.DisplayName);
    }

    [Fact]
    public void The_cli_entrypoint_stays_a_terminal_session()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(new HookEvent(T0, AgentKind.Claude, "SessionStart", "s1", @"C:\p\demo", null, null, null,
            Host: new HostInfo(1, null, null, null, null, Entrypoint: "cli")));
        Assert.Equal(SessionOrigin.Terminal, tracker.Sessions.Single().Origin);
    }

    [Fact]
    public void The_parser_reads_background_tasks_and_the_entrypoint()
    {
        var line = """{"ts":"2026-09-24T10:00:00.000Z","agent":"claude","event":"Stop","session_id":"s1","background_tasks":[{"id":"a1","type":"subagent","agent_type":"Explore","name":null},{"id":"w1","type":"workflow","agent_type":null,"name":"review"},{"type":"subagent"},42],"host":{"ppid":7,"entrypoint":"claude-desktop"}}""";
        var e = HookEventParser.Parse(line)!;
        Assert.Equal([new BackgroundTask("a1", "subagent", "Explore"), new BackgroundTask("w1", "workflow", Name: "review")], e.BackgroundTasks!);
        Assert.Equal("claude-desktop", e.Host!.Entrypoint);

        var none = HookEventParser.Parse("""{"ts":"2026-09-24T10:00:00.000Z","agent":"claude","event":"Stop","session_id":"s1","background_tasks":null}""")!;
        Assert.Null(none.BackgroundTasks);
    }
}
