using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Sessions;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class SessionProcessRegistryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);
    private const long Born = 134_000_000_000_000_000;

    private static SessionState Session(string id, DateTimeOffset? lastEvent = null, SessionOrigin origin = SessionOrigin.Terminal) =>
        new(AgentKind.Claude, id, id, null, SessionPhase.Idle, null, lastEvent ?? T0, T0, Origin: origin);

    [Fact]
    public void A_session_whose_process_died_is_ended_after_two_sweeps()
    {
        var probe = new FakeProcessProbe().Start(100, Born);
        var registry = new SessionProcessRegistry(null, probe, new FakeClock(T0));
        Assert.True(registry.Bind(AgentKind.Claude, "s1", 100, BindingSource.Terminal));
        Assert.Equal(Born, registry.Get(AgentKind.Claude, "s1")!.StartedAtFileTime);

        Assert.Empty(registry.FindEnded([Session("s1")]));
        probe.Kill(100);
        Assert.Empty(registry.FindEnded([Session("s1")]));      // first sighting: suspect
        Assert.Single(registry.FindEnded([Session("s1")]));     // confirmed
        Assert.Null(registry.Get(AgentKind.Claude, "s1"));
    }

    [Fact]
    public void One_odd_answer_of_the_probe_does_not_end_a_live_session()
    {
        var probe = new FakeProcessProbe().Start(100, Born);
        var registry = new SessionProcessRegistry(null, probe, new FakeClock(T0));
        registry.Bind(AgentKind.Claude, "s1", 100, BindingSource.Terminal);

        probe.Kill(100);
        Assert.Empty(registry.FindEnded([Session("s1")]));
        probe.Start(100, Born);
        Assert.Empty(registry.FindEnded([Session("s1")]));
        probe.Kill(100);
        Assert.Empty(registry.FindEnded([Session("s1")]));      // suspect again, not confirmed
    }

    [Fact]
    public void A_recycled_pid_counts_as_dead_and_an_unreadable_process_as_alive()
    {
        var probe = new FakeProcessProbe().Start(100, Born);
        var registry = new SessionProcessRegistry(null, probe, new FakeClock(T0));
        registry.Bind(AgentKind.Claude, "s1", 100, BindingSource.Terminal);
        registry.Bind(AgentKind.Claude, "s2", 200, BindingSource.Terminal, Born);

        probe.Start(100, Born + TimeSpan.FromMinutes(5).Ticks);    // same pid, another process
        probe.Processes[200] = ProcessSnapshot.Unknown;
        var ended = registry.FindEnded([Session("s1"), Session("s2")], confirm: false);
        Assert.Equal("s1", Assert.Single(ended).SessionId);
    }

    [Fact]
    public void A_dead_pid_is_never_bound_and_unbound_or_cloud_sessions_are_never_ended()
    {
        var probe = new FakeProcessProbe();
        var registry = new SessionProcessRegistry(null, probe, new FakeClock(T0));
        Assert.False(registry.Bind(AgentKind.Claude, "s1", 100, BindingSource.Terminal));
        Assert.Empty(registry.FindEnded([Session("s1"), Session("session_01x", origin: SessionOrigin.Cloud)], confirm: false));
    }

    [Fact]
    public void The_walk_never_replaces_a_live_binding_from_the_claude_registry()
    {
        var probe = new FakeProcessProbe().Start(100, Born).Start(300, Born + 5);
        var registry = new SessionProcessRegistry(null, probe, new FakeClock(T0));
        registry.Bind(AgentKind.Claude, "s1", 100, BindingSource.Registry, Born);

        Assert.False(registry.Bind(AgentKind.Claude, "s1", 300, BindingSource.Terminal));
        Assert.Equal(100, registry.Get(AgentKind.Claude, "s1")!.Pid);

        probe.Kill(100);
        Assert.True(registry.Bind(AgentKind.Claude, "s1", 300, BindingSource.Terminal));
        Assert.Equal(300, registry.Get(AgentKind.Claude, "s1")!.Pid);
    }

    [Fact]
    public void Bindings_and_ended_sessions_survive_a_restart_and_a_later_event_revives_a_session()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, "session-processes.json");
        var probe = new FakeProcessProbe().Start(100, Born).Start(101, Born);
        var clock = new FakeClock(T0);
        var first = new SessionProcessRegistry(file, probe, clock);
        first.Bind(AgentKind.Claude, "gone", 100, BindingSource.Terminal);
        first.Bind(AgentKind.Codex, "alive", 101, BindingSource.Terminal);
        probe.Kill(100);
        Assert.Single(first.FindEnded([Session("gone"), Session("alive") with { Agent = AgentKind.Codex }], confirm: false));

        var second = new SessionProcessRegistry(file, probe, clock);
        second.Load();
        Assert.Equal(101, second.Get(AgentKind.Codex, "alive")!.Pid);
        // The replay brings "gone" back with its old events: it is still ended.
        Assert.Single(second.FindEnded([Session("gone", T0.AddMinutes(-1))], confirm: false));
        // Resumed later in a new process: a newer event and a new binding bring it back.
        probe.Start(102, Born + 1);
        Assert.True(second.Bind(AgentKind.Claude, "gone", 102, BindingSource.Terminal));
        Assert.Empty(second.FindEnded([Session("gone", T0.AddMinutes(10))], confirm: false));
    }

    [Fact]
    public void Prune_drops_bindings_of_untracked_sessions_and_old_endings()
    {
        var probe = new FakeProcessProbe().Start(100, Born).Start(101, Born);
        var clock = new FakeClock(T0);
        var registry = new SessionProcessRegistry(null, probe, clock);
        registry.Bind(AgentKind.Claude, "kept", 100, BindingSource.Terminal);
        registry.Bind(AgentKind.Claude, "dropped", 101, BindingSource.Terminal);
        registry.Prune([Session("kept")]);
        Assert.NotNull(registry.Get(AgentKind.Claude, "kept"));
        Assert.Null(registry.Get(AgentKind.Claude, "dropped"));

        probe.Kill(100);
        registry.FindEnded([Session("kept")], confirm: false);
        clock.Advance(SessionProcessRegistry.EndedMemory + TimeSpan.FromMinutes(1));
        registry.Prune([]);
        Assert.Empty(registry.FindEnded([Session("kept", T0.AddMinutes(-5))], confirm: false));
    }

    [Fact]
    public void An_unreadable_state_file_starts_empty_and_is_reported()
    {
        using var dir = new TempDir();
        var file = dir.File("session-processes.json", "{ not json");
        var errors = new List<Exception>();
        var registry = new SessionProcessRegistry(file, new FakeProcessProbe(), new FakeClock(T0)) { OnError = errors.Add };
        registry.Load();
        Assert.Single(errors);
        Assert.Null(registry.Get(AgentKind.Claude, "s1"));
    }

    private static string Line(string evt, string sid, DateTimeOffset ts) =>
        $$"""{"ts":"{{ts:yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"claude","event":"{{evt}}","session_id":"{{sid}}","cwd":"C:\\demo\\proj"}""" + "\n";

    [Fact]
    public void Pump_ends_a_session_whose_terminal_was_closed_and_drops_it_silently_at_the_next_start()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(T0);
        var probe = new FakeProcessProbe().Start(100, Born);
        var file = Path.Combine(paths.LocalAppDataDir, "session-processes.json");
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        var processes = new SessionProcessRegistry(file, probe, clock);
        using (var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
               { Processes = processes, LivenessSweepEvery = TimeSpan.FromSeconds(10) })
        {
            pump.Start();
            File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", T0));
            pump.Pump();
            processes.Bind(AgentKind.Claude, "s1", 100, BindingSource.Terminal);

            probe.Kill(100);
            clock.Advance(TimeSpan.FromSeconds(11));
            pump.Pump();
            Assert.Single(tracker.Sessions);                    // suspect only
            clock.Advance(TimeSpan.FromSeconds(11));
            pump.Pump();
            Assert.Empty(tracker.Sessions);
            Assert.Equal(SessionChangeKind.Removed, changes.Last().Kind);
        }

        // The app restarts: the replay would restore s1 from its UserPromptSubmit, but it is known to be over.
        var restarted = new SessionTracker(clock);
        var raised = 0;
        restarted.Changed += _ => raised++;
        using var again = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), restarted, paths, clock)
        { Processes = new SessionProcessRegistry(file, probe, clock) };
        again.Start();
        Assert.Empty(restarted.Sessions);
        Assert.Equal(0, raised);
    }
}
