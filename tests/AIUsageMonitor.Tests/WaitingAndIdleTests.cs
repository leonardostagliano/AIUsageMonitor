using System.Text.Json;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;
using AIUsageMonitor.Core.Sessions;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

/// <summary>
/// "attende input" while agents work (idle_prompt, a permission granted without a hook) and the finished sessions of
/// the apps and of the cloud that must not linger.
/// </summary>
public class WaitingAndIdleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 9, 0, 0, TimeSpan.Zero);

    private static HookEvent Ev(string evt, int plusSeconds = 0, string sid = "s1", string? agentId = null,
        string? notificationType = null, IReadOnlyList<BackgroundTask>? tasks = null, SessionOrigin? origin = null) =>
        new(T0.AddSeconds(plusSeconds), AgentKind.Claude, evt, sid, @"C:\p\demo", notificationType, null, null, agentId,
            BackgroundTasks: tasks, Origin: origin);

    [Fact]
    public void Idle_prompt_is_ignored_while_background_agents_work()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, agentId: "bg"));
        tracker.Apply(Ev("Stop", 2, tasks: [new BackgroundTask("bg", BackgroundTask.SubagentType, "general-purpose")]));

        Assert.Null(tracker.Apply(Ev("Notification", 70, notificationType: "idle_prompt")));
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.Equal(SessionPhase.Working, tracker.AggregatePhase(AgentKind.Claude));
    }

    [Fact]
    public void A_session_only_waiting_for_its_next_prompt_does_not_hide_one_at_work()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit", sid: "done"));
        tracker.Apply(Ev("Stop", 1, sid: "done"));
        tracker.Apply(Ev("Notification", 61, sid: "done", notificationType: "idle_prompt"));
        var done = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.NeedsInput, done.Phase);
        Assert.True(done.AwaitsPrompt);
        Assert.Equal(T0.AddSeconds(61), done.WaitingSince);
        Assert.Equal(SessionPhase.NeedsInput, tracker.AggregatePhase(AgentKind.Claude));   // alone, it still shows

        tracker.Apply(Ev("UserPromptSubmit", 62, sid: "busy"));
        Assert.Equal(SessionPhase.Working, tracker.AggregatePhase(AgentKind.Claude));
        Assert.Equal(new SummaryPill(PhaseTone.Working, "1 al lavoro"), NotchPresentation.Summary(tracker.Sessions));

        // A real permission prompt is still the most urgent thing on screen.
        tracker.Apply(Ev("Notification", 63, sid: "asks", notificationType: "permission_prompt"));
        Assert.False(tracker.Sessions.Single(x => x.SessionId == "asks").AwaitsPrompt);
        Assert.Equal(SessionPhase.NeedsInput, tracker.AggregatePhase(AgentKind.Claude));
        Assert.Equal(new SummaryPill(PhaseTone.NeedsInput, "1 attende input"), NotchPresentation.Summary(tracker.Sessions));
    }

    [Fact]
    public void An_agent_that_starts_ends_an_idle_prompt_wait_but_not_a_pending_permission()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("Stop", sid: "idle"));
        tracker.Apply(Ev("Notification", 61, sid: "idle", notificationType: "idle_prompt"));
        tracker.Apply(Ev("SubagentStart", 62, sid: "idle", agentId: "a1"));
        var idle = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, idle.Phase);
        Assert.False(idle.AwaitsPrompt);
        Assert.Null(idle.WaitingSince);

        tracker.Apply(Ev("UserPromptSubmit", sid: "perm"));
        tracker.Apply(Ev("Notification", 5, sid: "perm", notificationType: "permission_prompt"));
        tracker.Apply(Ev("SubagentStart", 6, sid: "perm", agentId: "w1"));
        var perm = tracker.Sessions.Single(x => x.SessionId == "perm");
        Assert.Equal(SessionPhase.NeedsInput, perm.Phase);
        Assert.Equal(T0.AddSeconds(5), perm.WaitingSince);
    }

    private static void Record(string dir, int pid, string sessionId, string status, DateTimeOffset startedAt, DateTimeOffset statusAt,
        string entrypoint = "cli", bool spare = false)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"{pid}.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pid"] = pid, ["sessionId"] = sessionId, ["cwd"] = @"C:\p\demo", ["startedAt"] = startedAt.ToUnixTimeMilliseconds(),
            ["kind"] = "interactive", ["entrypoint"] = entrypoint, ["status"] = status,
            ["statusUpdatedAt"] = statusAt.ToUnixTimeMilliseconds(), ["spare"] = spare
        }));
    }

    [Fact]
    public void A_granted_permission_is_seen_through_the_session_registry()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(T0.AddMinutes(1));
        var probe = new FakeProcessProbe().Start(10, T0.AddSeconds(-1).UtcDateTime.ToFileTimeUtc());
        var tracker = new SessionTracker(clock);
        var feed = new ClaudeRegistrySessionFeed(new ClaudeSessionRegistryReader(dir.Path), probe, clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("Notification", 10, notificationType: "permission_prompt"));
        tracker.Apply(Ev("SubagentStart", 20, agentId: "a1"));      // the agent the permission was for
        bool HookOwned(string _) => true;

        // Still waiting according to the registry: nothing to say.
        Record(dir.Path, 10, "s1", "waiting", T0, T0.AddSeconds(9));
        Assert.Empty(feed.Sync(tracker.Sessions, HookOwned).Events);
        // A busy written before the prompt appeared says nothing either.
        Record(dir.Path, 10, "s1", "busy", T0, T0.AddSeconds(5));
        Assert.Empty(feed.Sync(tracker.Sessions, HookOwned).Events);

        Record(dir.Path, 10, "s1", "busy", T0, T0.AddSeconds(15));
        var change = Assert.Single(feed.Sync(tracker.Sessions, HookOwned).Events);
        tracker.Apply(change);
        var s = tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.Equal("al lavoro · 1 agente", s.PhaseLabel);
        Assert.Empty(feed.Sync(tracker.Sessions, HookOwned).Events);
    }

    [Fact]
    public void Idle_app_conversations_are_adopted_only_while_recent_and_spare_processes_never()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(T0.AddHours(2));
        var probe = new FakeProcessProbe();
        foreach (var pid in new[] { 10, 11, 12, 13, 14 }) probe.Start(pid, T0.AddSeconds(-1).UtcDateTime.ToFileTimeUtc());
        Record(dir.Path, 10, "old-idle-app", "idle", T0, T0.AddMinutes(5), "claude-desktop");
        Record(dir.Path, 11, "recent-idle-app", "idle", T0, clock.UtcNow.AddMinutes(-3), "claude-desktop");
        Record(dir.Path, 12, "busy-app", "busy", T0, T0.AddMinutes(5), "claude-desktop");
        Record(dir.Path, 13, "old-idle-cli", "idle", T0, T0.AddMinutes(5), "cli");
        Record(dir.Path, 14, "spare", "idle", T0, T0, "claude-desktop", spare: true);
        var tracker = new SessionTracker(clock);
        var feed = new ClaudeRegistrySessionFeed(new ClaudeSessionRegistryReader(dir.Path), probe, clock);

        foreach (var e in feed.Sync(tracker.Sessions, _ => false).Events) tracker.Apply(e);
        Assert.Equal(["busy-app", "old-idle-cli", "recent-idle-app"], tracker.Sessions.Select(s => s.SessionId).Order());

        // The old conversation comes back as soon as it works again.
        Record(dir.Path, 10, "old-idle-app", "busy", T0, clock.UtcNow, "claude-desktop");
        foreach (var e in feed.Sync(tracker.Sessions, _ => false).Events) tracker.Apply(e);
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single(s => s.SessionId == "old-idle-app").Phase);
    }

    [Fact]
    public void Finished_app_sessions_leave_after_the_idle_window_terminal_ones_stay()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("Stop", sid: "app", origin: SessionOrigin.App));
        tracker.Apply(Ev("UserPromptSubmit", sid: "app-busy", origin: SessionOrigin.App));
        tracker.Apply(Ev("Stop", sid: "cli"));
        clock.Advance(TimeSpan.FromMinutes(11));

        var removed = Assert.Single(tracker.RemoveIdle(SessionOrigin.App, TimeSpan.FromMinutes(10)));
        Assert.Equal("app", removed.Session.SessionId);
        Assert.Equal(["app-busy", "cli"], tracker.Sessions.Select(s => s.SessionId).Order());
    }

    [Fact]
    public void Pump_sweeps_finished_app_sessions_on_the_liveness_cadence()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            AppIdleWindow = TimeSpan.FromMinutes(10)
        };
        pump.Start();
        pump.Inject([Ev("Stop", sid: "app", origin: SessionOrigin.App)]);
        pump.Pump();
        Assert.Single(tracker.Sessions);

        clock.Advance(TimeSpan.FromMinutes(11));
        pump.Pump();
        Assert.Empty(tracker.Sessions);
    }
}
