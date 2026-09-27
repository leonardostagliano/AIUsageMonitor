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

    private const string WorkflowAgent = "workflow-subagent";

    [Fact]
    public void The_agents_of_a_workflow_survive_every_Stop_that_names_it_and_end_with_the_workflow()
    {
        // The shape of a real session: a workflow starts four agents, the main agent ends its turn listing the workflow
        // (never its agents), the user keeps talking, the agents stop one by one, and the workflow's end wakes the session.
        var tracker = new SessionTracker(new FakeClock(T0));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 10, "w1", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 13, "w2", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 13, "w3", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 13, "w4", WorkflowAgent));
        tracker.Apply(Ev("UserPromptSubmit", 31));

        tracker.Apply(Ev("Stop", 51, tasks: [Workflow("wf1")], message: "Workflow avviato."));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.True(s.AwaitingSubagents);
        Assert.Equal(1, s.PendingWorkflows);
        Assert.Equal(4, s.ActiveSubagents);
        Assert.Equal("al lavoro · 4 agenti", s.PhaseLabel);

        tracker.Apply(Ev("SubagentStop", 1972, "w2", WorkflowAgent, tasks: [Workflow("wf1")]));
        // Another turn of the main agent while the workflow runs: its Stop names the workflow again.
        tracker.Apply(Ev("UserPromptSubmit", 2063));
        tracker.Apply(Ev("Stop", 2548, tasks: [Workflow("wf1")]));
        Assert.Equal(["w1", "w3", "w4"], tracker.Sessions.Single().RunningSubagents.Select(a => a.AgentId));

        tracker.Apply(Ev("SubagentStop", 4175, "w1", WorkflowAgent, tasks: [Workflow("wf1")]));
        tracker.Apply(Ev("SubagentStop", 4202, "w3", WorkflowAgent, tasks: [Workflow("wf1")]));
        tracker.Apply(Ev("SubagentStop", 4404, "w4", WorkflowAgent, tasks: [Workflow("wf1")]));
        s = tracker.Sessions.Single();
        Assert.Equal(0, s.ActiveSubagents);
        Assert.Equal(SessionPhase.Working, s.Phase);                // the workflow itself is still in flight

        tracker.Apply(Ev("Stop", 4410, tasks: [], message: "Workflow completato."));
        s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal(0, s.ActiveSubagents);
        Assert.Equal(0, s.PendingWorkflows);
        Assert.False(s.AwaitingSubagents);
        Assert.All(s.Subagents!, a => Assert.Equal(SubagentPhase.Done, a.Phase));
        // Each agent ended with its own SubagentStop, never with a Stop that could not see it.
        Assert.Equal(T0.AddSeconds(1972), s.Subagents!.Single(a => a.AgentId == "w2").EndedAt);
        Assert.Equal(T0.AddSeconds(4404), s.Subagents!.Single(a => a.AgentId == "w4").EndedAt);
        // The turn ended once, when the workflow did.
        Assert.Single(changes, c => c.Session.Phase == SessionPhase.Idle);
    }

    [Fact]
    public void A_Stop_that_lists_no_workflow_ends_the_workflow_agents_it_does_not_name()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "w1", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 2, "w2", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 3, "bg", "general-purpose"));

        // Only a background agent is in flight: no workflow, so none of its agents can be alive.
        tracker.Apply(Ev("Stop", 4, tasks: [Agent("bg")]));
        var s = tracker.Sessions.Single();
        Assert.Equal("bg", s.RunningSubagents.Single().AgentId);
        Assert.Equal(T0.AddSeconds(4), s.Subagents!.Single(a => a.AgentId == "w1").EndedAt);
        Assert.Equal(SessionPhase.Working, s.Phase);

        tracker.Apply(Ev("SubagentStart", 5, "w3", WorkflowAgent));
        tracker.Apply(Ev("Stop", 6, tasks: []));
        s = tracker.Sessions.Single();
        Assert.Equal(0, s.ActiveSubagents);
        Assert.Equal(SessionPhase.Idle, s.Phase);
    }

    [Fact]
    public void A_SubagentStop_whose_list_names_no_workflow_ends_the_workflow_agents_left()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "w1", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 2, "w2", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 3, "g1", "general-purpose"));
        tracker.Apply(Ev("SubagentStart", 4, "g2", "general-purpose"));
        tracker.Apply(Ev("Stop", 5, tasks: [Workflow("wf1"), Agent("g1"), Agent("g2")]));

        // A list with a workflow in it changes nothing for the workflow's agents.
        tracker.Apply(Ev("SubagentStop", 6, "g1", "general-purpose", tasks: [Workflow("wf1"), Agent("g2")]));
        Assert.Equal(["w1", "w2", "g2"], tracker.Sessions.Single().RunningSubagents.Select(a => a.AgentId));
        // Neither does a SubagentStop without a list (Codex, an older Claude Code).
        tracker.Apply(Ev("SubagentStart", 7, "g3", "general-purpose"));
        tracker.Apply(Ev("SubagentStop", 8, "g3", "general-purpose"));
        Assert.Equal(["w1", "w2", "g2"], tracker.Sessions.Single().RunningSubagents.Select(a => a.AgentId));

        // The workflow is gone from the list: its agents that never sent their SubagentStop are over too. The agent
        // running next to the one that stopped is not a background task and is left alone, as ever.
        tracker.Apply(Ev("SubagentStop", 9, "g4", "general-purpose", tasks: [Agent("g2")]));
        var s = tracker.Sessions.Single();
        Assert.Equal("g2", s.RunningSubagents.Single().AgentId);
        Assert.Equal(T0.AddSeconds(9), s.Subagents!.Single(a => a.AgentId == "w1").EndedAt);
        Assert.Equal(T0.AddSeconds(9), s.Subagents!.Single(a => a.AgentId == "w2").EndedAt);
        Assert.Equal(0, s.PendingWorkflows);
        Assert.Equal(SessionPhase.Working, s.Phase);

        tracker.Apply(Ev("SubagentStop", 10, "g2", "general-purpose", tasks: []));
        Assert.Equal(SessionPhase.Idle, tracker.Sessions.Single().Phase);
    }

    [Fact]
    public void A_general_purpose_agent_the_Stop_does_not_name_is_ended_even_while_a_workflow_runs()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "w1", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 2, "g1", "general-purpose"));

        tracker.Apply(Ev("Stop", 3, tasks: [Workflow("wf1")]));

        var s = tracker.Sessions.Single();
        Assert.Equal("w1", s.RunningSubagents.Single().AgentId);
        var ended = s.Subagents!.Single(a => a.AgentId == "g1");
        Assert.Equal(SubagentPhase.Done, ended.Phase);
        Assert.Equal(T0.AddSeconds(3), ended.EndedAt);
    }

    [Fact]
    public void An_agent_is_known_as_a_workflow_agent_by_its_transcript_path_too()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        // No agent_type, but a transcript under subagents/workflows/, with either separator.
        tracker.Apply(Ev("SubagentStart", 1, "p1") with { AgentTranscriptPath = @"C:\p\s1\subagents\workflows\wf_1\agent-p1.jsonl" });
        tracker.Apply(Ev("SubagentStart", 2, "p2") with { AgentTranscriptPath = "/home/demo/p/s1/subagents/workflows/wf_1/agent-p2.jsonl" });
        // An Agent-tool agent writes directly under subagents/.
        tracker.Apply(Ev("SubagentStart", 3, "p3") with { AgentTranscriptPath = @"C:\p\s1\subagents\agent-p3.jsonl" });

        tracker.Apply(Ev("Stop", 4, tasks: [Workflow("wf1")]));
        Assert.Equal(["p1", "p2"], tracker.Sessions.Single().RunningSubagents.Select(a => a.AgentId));

        tracker.Apply(Ev("SubagentStop", 5, "p9", "general-purpose", tasks: []));
        Assert.Empty(tracker.Sessions.Single().RunningSubagents);
    }

    [Fact]
    public void The_sweep_ends_an_agent_silent_for_the_timeout_while_the_others_keep_the_session_busy()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 60, "dead", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 61, "busy", WorkflowAgent));
        tracker.Apply(Ev("Stop", 62, tasks: [Workflow("wf1")]));
        // The workflow keeps starting agents: the session is anything but quiet.
        tracker.Apply(Ev("SubagentStart", 39 * 60, "fresh", WorkflowAgent));
        clock.Advance(TimeSpan.FromMinutes(41));

        DateTimeOffset? Activity(SubagentState agent) => agent.AgentId switch
        {
            "dead" => T0.AddMinutes(1),                // interrupted 40 minutes ago, no SubagentStop
            "busy" => clock.UtcNow.AddSeconds(-20),
            _ => null                                   // "fresh" has not written its transcript yet
        };
        var change = Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, a) => Activity(a)));

        var s = change.Session;
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.Equal(SessionPhase.Working, change.PreviousPhase);
        Assert.True(s.AwaitingSubagents);
        Assert.Equal(1, s.PendingWorkflows);
        Assert.Equal(["busy", "fresh"], s.RunningSubagents.Select(a => a.AgentId));
        Assert.Equal(clock.UtcNow, s.Subagents!.Single(a => a.AgentId == "dead").EndedAt);
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, a) => Activity(a)));
    }

    [Fact]
    public void An_agent_without_known_activity_waits_for_the_whole_session_to_go_quiet()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 60, "a1"));
        tracker.Apply(Ev("Stop", 61, message: "Lanciati."));
        tracker.Apply(Ev("SubagentStart", 25 * 60, "a2"));

        // a1 started 34 minutes ago, but its session heard from an agent 9 minutes ago.
        clock.Advance(TimeSpan.FromMinutes(34));
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => null));
        Assert.Equal(2, tracker.Sessions.Single().ActiveSubagents);

        clock.Advance(TimeSpan.FromMinutes(22));
        var change = Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => null));
        Assert.Equal(SessionPhase.Idle, change.Session.Phase);
        Assert.Equal("Lanciati.", change.Session.Message);
        Assert.Equal(0, change.Session.ActiveSubagents);
    }

    [Fact]
    public void No_agent_is_released_for_inactivity_while_the_session_waits_for_input()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "asks", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 2, "quiet", WorkflowAgent));
        tracker.Apply(Ev("Stop", 3, tasks: [Workflow("wf1")]));
        // The workflow agent asks for a permission and the user is away.
        tracker.Apply(new HookEvent(T0.AddSeconds(4), AgentKind.Claude, "Notification", "s1", @"C:\p\demo", "permission_prompt", null, null));
        clock.Advance(TimeSpan.FromMinutes(50));

        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => T0.AddSeconds(4)));
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30)));
        var waiting = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.NeedsInput, waiting.Phase);
        Assert.Equal(2, waiting.ActiveSubagents);
        Assert.Equal(1, waiting.PendingWorkflows);

        // Answered: the agent that asked writes again, the other one stays silent and is released.
        tracker.Apply(Ev("PostToolUse", 50 * 60));
        var change = Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30),
            (_, a) => a.AgentId == "asks" ? clock.UtcNow : T0.AddSeconds(4)));
        Assert.Equal(SessionPhase.Working, change.Session.Phase);
        Assert.Equal("asks", change.Session.RunningSubagents.Single().AgentId);
    }

    [Fact]
    public void Ending_the_last_silent_agent_ends_the_turn_only_when_no_workflow_is_left()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "w1", WorkflowAgent));
        tracker.Apply(Ev("SubagentStart", 2, "w2", WorkflowAgent));
        tracker.Apply(Ev("Stop", 3, tasks: [Workflow("wf1")], message: "Workflow avviato."));
        tracker.Apply(Ev("SubagentStop", 35 * 60, "w2", WorkflowAgent, tasks: [Workflow("wf1")]));
        clock.Advance(TimeSpan.FromMinutes(36));

        // w1 died long ago; the workflow was still in flight a minute ago: the session waits on it, between two phases.
        var change = Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => T0.AddMinutes(1)));
        Assert.Equal(0, change.Session.ActiveSubagents);
        Assert.Equal(SessionPhase.Working, change.Session.Phase);
        Assert.True(change.Session.AwaitingSubagents);
        Assert.Equal(1, change.Session.PendingWorkflows);

        // An older Claude Code (no list): the turn waited on its agents alone, and the last one is gone.
        var other = new SessionTracker(new FakeClock(T0.AddMinutes(36)));
        other.Apply(Ev("UserPromptSubmit"));
        other.Apply(Ev("SubagentStart", 1, "a1"));
        other.Apply(Ev("SubagentStart", 2, "a2"));
        other.Apply(Ev("Stop", 3, message: "Lanciati."));
        other.Apply(Ev("SubagentStop", 35 * 60, "a2"));
        change = Assert.Single(other.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => T0.AddMinutes(1)));
        Assert.Equal(SessionPhase.Idle, change.Session.Phase);
        Assert.Equal("Lanciati.", change.Session.Message);
        Assert.False(change.Session.AwaitingSubagents);
    }

    [Fact]
    public void The_children_synthesised_from_Codex_rollouts_keep_the_session_rule()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit", agent: AgentKind.Codex));
        tracker.Apply(Ev("SubagentStart", 1, "thread-1", CodexSubagentScanner.SyntheticAgentType, agent: AgentKind.Codex));
        // The pump announces a child it still sees again every half timeout: the session is not quiet.
        tracker.Apply(Ev("SubagentStart", 20 * 60, "thread-1", CodexSubagentScanner.SyntheticAgentType, agent: AgentKind.Codex));
        clock.Advance(TimeSpan.FromMinutes(40));

        // An activity older than the timeout does not end a child the scanner keeps announcing.
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => T0));
        Assert.Equal(1, tracker.Sessions.Single().ActiveSubagents);

        // The announcements stop: a recent activity still keeps it, as before; without one the timeout ends it.
        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => clock.UtcNow));
        Assert.Single(tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30), (_, _) => T0));
        Assert.Equal(0, tracker.Sessions.Single().ActiveSubagents);
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
