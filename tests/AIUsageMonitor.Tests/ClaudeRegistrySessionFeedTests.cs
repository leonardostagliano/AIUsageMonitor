using System.Text.Json;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Sessions;
using AIUsageMonitor.Core.Settings;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ClaudeRegistrySessionFeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    /// <summary>FILETIME of an instant, as the probe reports a process creation time.</summary>
    private static long FileTime(DateTimeOffset at) => at.UtcDateTime.ToFileTimeUtc();

    private static void Record(string dir, int pid, string sessionId, string status, DateTimeOffset startedAt,
        string entrypoint = "claude-desktop", string kind = "interactive", string? waitingFor = null, string cwd = @"C:\p\demo",
        DateTimeOffset? statusAt = null)
    {
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pid"] = pid, ["sessionId"] = sessionId, ["cwd"] = cwd, ["startedAt"] = startedAt.ToUnixTimeMilliseconds(),
            ["procStart"] = "683", ["version"] = "2.1.282", ["kind"] = kind, ["entrypoint"] = entrypoint,
            ["status"] = status, ["waitingFor"] = waitingFor, ["updatedAt"] = startedAt.ToUnixTimeMilliseconds(),
            ["statusUpdatedAt"] = statusAt?.ToUnixTimeMilliseconds()
        });
        File.WriteAllText(Path.Combine(dir, $"{pid}.json"), json);
    }

    private sealed class Rig : IDisposable
    {
        public readonly TempDir Dir = new();
        public readonly FakeClock Clock = new(T0);
        public readonly FakeProcessProbe Probe = new();
        public readonly SessionTracker Tracker;
        public readonly ClaudeRegistrySessionFeed Feed;
        public readonly HashSet<string> HookOwned = [];
        public string Sessions => Path.Combine(Dir.Path, "sessions");
        public string Projects => Path.Combine(Dir.Path, "projects");

        public Rig()
        {
            Tracker = new SessionTracker(Clock);
            Feed = new ClaudeRegistrySessionFeed(new ClaudeSessionRegistryReader(Sessions), Probe, Clock,
                new ClaudeTranscriptFinder(Projects, Clock));
        }

        public ClaudeRegistrySync Sync()
        {
            var sync = Feed.Sync(Tracker.Sessions, HookOwned.Contains);
            foreach (var e in sync.Events) Tracker.Apply(e);
            return sync;
        }

        public void Dispose() => Dir.Dispose();
    }

    [Fact]
    public void A_desktop_session_without_hooks_is_adopted_and_follows_its_status()
    {
        using var rig = new Rig();
        var changes = new List<SessionChange>();
        rig.Tracker.Changed += changes.Add;
        rig.Probe.Start(4242, FileTime(T0.AddSeconds(-1)));
        Record(rig.Sessions, 4242, "d1", "idle", T0);
        var transcript = rig.Dir.File(Path.Combine("projects", ClaudeTranscriptFinder.ProjectDirName(@"C:\p\demo"), "d1.jsonl"), "");

        // Too young: a CLI session gets the time to send its own SessionStart first.
        rig.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Empty(rig.Sync().Events);

        rig.Clock.Advance(TimeSpan.FromSeconds(6));
        rig.Sync();
        var s = rig.Tracker.Sessions.Single();
        Assert.Equal("d1", s.SessionId);
        Assert.Equal(SessionOrigin.App, s.Origin);
        Assert.Equal("demo", s.DisplayName);
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("pronto", s.PhaseLabel);
        Assert.Equal(transcript, s.TranscriptPath);
        Assert.Equal(4242, s.Host!.Ppid);
        Assert.Equal("claude-desktop", s.Host.Entrypoint);

        Record(rig.Sessions, 4242, "d1", "busy", T0);
        rig.Sync();
        Assert.Equal(SessionPhase.Working, rig.Tracker.Sessions.Single().Phase);
        Assert.Empty(rig.Sync().Events);                        // same status, nothing to say

        Record(rig.Sessions, 4242, "d1", "waiting", T0, waitingFor: "permission prompt");
        rig.Sync();
        s = rig.Tracker.Sessions.Single();
        Assert.Equal(SessionPhase.NeedsInput, s.Phase);
        Assert.Equal("Permesso richiesto", s.Message);

        Record(rig.Sessions, 4242, "d1", "idle", T0);
        rig.Sync();
        s = rig.Tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Equal("finito", s.PhaseLabel);
        Assert.Equal(SessionPhase.NeedsInput, changes.Last().PreviousPhase);

        // The app closes the session: the record goes away.
        File.Delete(Path.Combine(rig.Sessions, "4242.json"));
        rig.Sync();
        Assert.Empty(rig.Tracker.Sessions);
    }

    [Fact]
    public void A_busy_session_is_adopted_working_and_a_cli_session_stays_a_terminal_session()
    {
        using var rig = new Rig();
        rig.Probe.Start(10, FileTime(T0.AddMinutes(-5).AddSeconds(-1)));
        Record(rig.Sessions, 10, "c1", "busy", T0.AddMinutes(-5), entrypoint: "cli");
        rig.Sync();
        var s = rig.Tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        Assert.Equal(SessionOrigin.Terminal, s.Origin);
    }

    [Fact]
    public void A_session_adopted_at_work_has_no_turn_start_until_its_next_turn()
    {
        using var rig = new Rig();
        rig.Probe.Start(10, FileTime(T0.AddMinutes(-5).AddSeconds(-1)));
        Record(rig.Sessions, 10, "c1", "busy", T0.AddMinutes(-5), entrypoint: "cli");

        var adoption = rig.Sync().Events;
        Assert.Equal(["SessionStart", "UserPromptSubmit"], adoption.Select(e => e.Event));
        Assert.True(adoption[1].Adopted);
        Assert.Null(rig.Tracker.Sessions.Single().TurnStartedAt);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Record(rig.Sessions, 10, "c1", "idle", T0.AddMinutes(-5), entrypoint: "cli");
        rig.Sync();
        var done = rig.Tracker.Sessions.Single();
        Assert.Equal("finito", done.PhaseLabel);
        Assert.Null(done.TurnStartedAt);                          // its card says "Finito" without a duration

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        Record(rig.Sessions, 10, "c1", "busy", T0.AddMinutes(-5), entrypoint: "cli");
        var next = Assert.Single(rig.Sync().Events);
        Assert.False(next.Adopted);
        Assert.Equal(rig.Clock.UtcNow, rig.Tracker.Sessions.Single().TurnStartedAt);
    }

    [Fact]
    public void Sessions_the_hooks_report_are_left_to_them_but_still_listed_as_live()
    {
        using var rig = new Rig();
        rig.Probe.Start(10, FileTime(T0.AddMinutes(-6)));
        Record(rig.Sessions, 10, "h1", "busy", T0.AddMinutes(-5), entrypoint: "cli");
        rig.HookOwned.Add("h1");
        var sync = rig.Sync();
        Assert.Empty(sync.Events);
        var live = Assert.Single(sync.Live);
        Assert.Equal(("h1", 10, FileTime(T0.AddMinutes(-6))), (live.SessionId, live.Pid, live.StartedAtFileTime!.Value));
    }

    [Fact]
    public void A_hook_session_that_goes_back_to_work_without_a_prompt_is_reported_once_per_busy_period()
    {
        using var rig = new Rig();
        rig.Probe.Start(10, FileTime(T0.AddMinutes(-6)));
        rig.HookOwned.Add("h1");
        HookEvent Hook(string evt, double plusSeconds) =>
            new(T0.AddSeconds(plusSeconds), AgentKind.Claude, evt, "h1", @"C:\p\demo", null, null, null);
        void Status(string status, double plusSeconds) =>
            Record(rig.Sessions, 10, "h1", status, T0.AddMinutes(-5), entrypoint: "cli", statusAt: T0.AddSeconds(plusSeconds));
        ClaudeRegistrySync SyncAt(double plusSeconds)
        {
            rig.Clock.UtcNow = T0.AddSeconds(plusSeconds);
            return rig.Sync();
        }

        // A turn and its Stop. The Stop hooks run before Claude Code writes idle: the busy of the turn they end says nothing.
        rig.Tracker.Apply(Hook("UserPromptSubmit", 0));
        rig.Tracker.Apply(Hook("Stop", 60));
        Status("busy", 0);
        Assert.Empty(SyncAt(61).Events);
        Status("idle", 61);
        Assert.Empty(SyncAt(62).Events);

        // A prompt typed by the user: the record goes busy a moment before the hook's own UserPromptSubmit lands, and
        // the hooks get that moment to report it themselves.
        Status("busy", 100);
        Assert.Empty(SyncAt(100.3).Events);
        rig.Tracker.Apply(Hook("UserPromptSubmit", 100.6));
        Assert.Empty(SyncAt(110).Events);
        rig.Tracker.Apply(Hook("Stop", 150));
        Status("idle", 150.2);
        Assert.Empty(SyncAt(151).Events);

        // A background task finishes and wakes the main agent: busy again, and no hook says so.
        Status("busy", 200);
        Assert.Empty(SyncAt(203).Events);
        var resumed = Assert.Single(SyncAt(209).Events);
        Assert.Equal(("UserPromptSubmit", "registry", false), (resumed.Event, resumed.Source, resumed.Adopted));
        var s = rig.Tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Working, s.Phase);
        // The turn began when the record went busy, not when this read noticed it.
        Assert.Equal(T0.AddSeconds(200), s.TurnStartedAt);
        Assert.Empty(SyncAt(215).Events);                          // once per busy period

        // No hook ever spoke for that turn, and none closes it: the record going idle does, silently (nobody asked for
        // that turn, there is nothing to announce), and only once.
        var changes = new List<SessionChange>();
        rig.Tracker.Changed += changes.Add;
        Status("idle", 230);
        var closed = Assert.Single(SyncAt(231).Events);
        Assert.Equal(("Notification", "idle_prompt", "registry"), (closed.Event, closed.NotificationType, closed.Source));
        Assert.Equal(SessionPhase.Idle, rig.Tracker.Sessions.Single().Phase);
        Assert.True(Assert.Single(changes).Silent);
        Assert.Empty(SyncAt(235).Events);
        // The next prompt is a turn of its own.
        Status("busy", 240);
        rig.Tracker.Apply(Hook("UserPromptSubmit", 240.5));
        Assert.Empty(SyncAt(250).Events);
        Assert.Equal(T0.AddSeconds(240.5), rig.Tracker.Sessions.Single().TurnStartedAt);
        rig.Tracker.Apply(Hook("Stop", 255));
        Status("idle", 255.2);
        Assert.Empty(SyncAt(256).Events);

        // A shell command typed at the prompt runs no turn: its record reads "shell", and nothing is reported.
        Status("shell", 258);
        Assert.Empty(SyncAt(268).Events);
        Status("idle", 269);
        Assert.Empty(SyncAt(270).Events);
        Assert.Equal(SessionPhase.Idle, rig.Tracker.Sessions.Single().Phase);

        // Woken again, and this time the resumed turn ends with its own Stop while the record still reads busy: nothing
        // to report, and nothing left for the registry to close once the record reads idle.
        Status("busy", 275);
        Assert.Equal("UserPromptSubmit", Assert.Single(SyncAt(284).Events).Event);
        rig.Tracker.Apply(Hook("Stop", 290));
        Assert.Empty(SyncAt(291).Events);
        Status("idle", 291.5);
        Assert.Empty(SyncAt(292).Events);
        Assert.Equal(SessionPhase.Idle, rig.Tracker.Sessions.Single().Phase);

        // Once a hook of a resumed turn has spoken, that turn is the hooks' to close: the record going idle before its
        // Stop is read changes nothing.
        Status("busy", 320);
        Assert.Equal("UserPromptSubmit", Assert.Single(SyncAt(329).Events).Event);
        rig.Tracker.Apply(Hook("PostToolUse", 330));
        Status("idle", 331);
        Assert.Empty(SyncAt(332).Events);
        Assert.Equal(SessionPhase.Working, rig.Tracker.Sessions.Single().Phase);
        rig.Tracker.Apply(Hook("Stop", 333));

        // A session in error is left to its hooks.
        rig.Tracker.Apply(Hook("StopFailure", 400));
        Status("busy", 410);
        Assert.Empty(SyncAt(430).Events);
        Assert.Equal(SessionPhase.Error, rig.Tracker.Sessions.Single().Phase);
    }

    /// <summary>
    /// Denying a permission (No, Esc) or dismissing a question or a plan is an interrupt: no hook reports it, not even
    /// Stop. The record going idle after the wait began, and staying so for <see cref="ClaudeRegistrySessionFeed.AdoptAfter"/>,
    /// says the wait is over: the session leaves NeedsInput silently and its card is retired, never turned into "Finito".
    /// </summary>
    [Fact]
    public void A_hook_session_whose_wait_is_denied_leaves_it_when_its_record_goes_idle()
    {
        using var rig = new Rig();
        rig.Probe.Start(10, FileTime(T0.AddMinutes(-6)));
        rig.HookOwned.Add("h1");
        var changes = new List<SessionChange>();
        rig.Tracker.Changed += changes.Add;
        HookEvent Hook(string evt, double plusSeconds, string? notificationType = null, string? agentId = null) =>
            new(T0.AddSeconds(plusSeconds), AgentKind.Claude, evt, "h1", @"C:\p\demo", notificationType, null, null, agentId,
                agentId is null ? null : "general-purpose");
        void Status(string status, double plusSeconds) =>
            Record(rig.Sessions, 10, "h1", status, T0.AddMinutes(-5), entrypoint: "cli", statusAt: T0.AddSeconds(plusSeconds));
        ClaudeRegistrySync SyncAt(double plusSeconds)
        {
            rig.Clock.UtcNow = T0.AddSeconds(plusSeconds);
            return rig.Sync();
        }
        var retire = new RetireIntent(NotificationComposer.SessionKey(AgentKind.Claude, "h1"));

        rig.Tracker.Apply(Hook("UserPromptSubmit", 0));
        rig.Tracker.Apply(Hook("Notification", 10, "permission_prompt"));
        // An idle written before the wait began, or a status this feed does not know, says nothing.
        Status("idle", 8);
        Assert.Empty(SyncAt(30).Events);
        Status("compacting", 20);
        Assert.Empty(SyncAt(40).Events);
        Status("waiting", 41);
        Assert.Empty(SyncAt(45).Events);

        // Denied: the record goes idle and no hook follows. The hooks get their time first, then the wait is over.
        Status("idle", 50);
        Assert.Empty(SyncAt(55).Events);
        var denied = Assert.Single(SyncAt(58).Events);
        Assert.Equal(("Stop", "registry", true), (denied.Event, denied.Source, denied.Quiet));
        var s = rig.Tracker.Sessions.Single();
        Assert.Equal(SessionPhase.Idle, s.Phase);
        Assert.Null(s.WaitingSince);
        var closed = changes.Last();
        Assert.Equal((SessionPhase.NeedsInput, true), (closed.PreviousPhase!.Value, closed.Silent));
        Assert.Equal(retire, NotificationComposer.Compose(closed, new AppSettings()));
        Assert.Empty(SyncAt(70).Events);                           // once

        // The next prompt is a turn of its own.
        Status("busy", 80);
        rig.Tracker.Apply(Hook("UserPromptSubmit", 80.5));
        Assert.Empty(SyncAt(90).Events);
        Assert.Equal(T0.AddSeconds(80.5), rig.Tracker.Sessions.Single().TurnStartedAt);

        // The turn ends waiting for its agent, and the agent asks for a permission. Answered, the record reads idle again
        // (the main agent has nothing to do): back to waiting for the agent, whose end is the end of the turn.
        rig.Tracker.Apply(Hook("SubagentStart", 85, agentId: "a1"));
        rig.Tracker.Apply(Hook("Stop", 100));
        Status("idle", 100.2);
        Assert.Empty(SyncAt(110).Events);
        Status("waiting", 118);
        rig.Tracker.Apply(Hook("Notification", 120, "permission_prompt"));
        Status("idle", 140);
        var answered = Assert.Single(SyncAt(150).Events);
        Assert.Equal(("PostToolUse", "registry", true), (answered.Event, answered.Source, answered.Quiet));
        s = rig.Tracker.Sessions.Single();
        Assert.Equal((SessionPhase.Working, 1, true), (s.Phase, s.ActiveSubagents, s.AwaitingSubagents));
        Assert.Equal(retire, NotificationComposer.Compose(changes.Last(), new AppSettings()));
        Assert.Empty(SyncAt(160).Events);
        rig.Tracker.Apply(Hook("SubagentStop", 200, agentId: "a1"));
        Assert.Equal(SessionPhase.Idle, rig.Tracker.Sessions.Single().Phase);
        Assert.IsType<ShowIntent>(NotificationComposer.Compose(changes.Last(), new AppSettings()));
    }

    [Fact]
    public void Stale_records_of_dead_or_recycled_pids_and_daemons_are_ignored()
    {
        using var rig = new Rig();
        Record(rig.Sessions, 10, "dead", "busy", T0.AddMinutes(-5));
        rig.Probe.Start(11, FileTime(T0.AddMinutes(-1)));          // pid reused after the record was written
        Record(rig.Sessions, 11, "recycled", "busy", T0.AddMinutes(-5));
        rig.Probe.Start(12, FileTime(T0.AddMinutes(-6)));
        Record(rig.Sessions, 12, "daemon", "idle", T0.AddMinutes(-5), kind: "daemon");
        File.WriteAllText(Path.Combine(rig.Sessions, "13.json"), "{ torn");
        File.WriteAllText(Path.Combine(rig.Sessions, "not-a-pid.json"), "{}");

        var sync = rig.Sync();
        Assert.Empty(sync.Events);
        Assert.Empty(sync.Live);
        Assert.Equal(["dead", "recycled"], sync.Stale.Order());
    }

    [Fact]
    public void Pump_drops_at_start_up_a_replayed_session_whose_process_left_only_a_stale_record()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(T0);
        var probe = new FakeProcessProbe().Start(30, FileTime(T0.AddMinutes(-6)));
        Record(paths.ClaudeSessionsDir, 20, "closed", "busy", T0.AddMinutes(-50), entrypoint: "cli");     // pid 20 is gone
        Record(paths.ClaudeSessionsDir, 21, "resumed", "idle", T0.AddMinutes(-50), entrypoint: "cli");    // gone, but...
        Record(paths.ClaudeSessionsDir, 30, "resumed", "busy", T0.AddMinutes(-5), entrypoint: "cli");     // ...resumed in 30
        Directory.CreateDirectory(paths.MonitorDir);
        string Line(string sid) =>
            $$"""{"ts":"{{T0.AddMinutes(-2):yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"claude","event":"UserPromptSubmit","session_id":"{{sid}}","cwd":"C:\\p\\x"}""" + "\n";
        File.WriteAllText(paths.EventsFile, Line("closed") + Line("resumed") + Line("unknown"));
        var tracker = new SessionTracker(clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            ClaudeRegistry = new ClaudeRegistrySessionFeed(new ClaudeSessionRegistryReader(paths.ClaudeSessionsDir), probe, clock)
        };

        pump.Start();
        Assert.Equal(["resumed", "unknown"], tracker.Sessions.Select(s => s.SessionId).Order());
    }

    [Fact]
    public void A_session_whose_process_dies_is_ended()
    {
        using var rig = new Rig();
        rig.Probe.Start(10, FileTime(T0.AddMinutes(-6)));
        Record(rig.Sessions, 10, "d1", "busy", T0.AddMinutes(-5));
        rig.Sync();
        Assert.Single(rig.Tracker.Sessions);

        rig.Probe.Kill(10);                                        // killed: its record stays behind
        rig.Sync();
        Assert.Empty(rig.Tracker.Sessions);
    }

    [Fact]
    public void Clear_in_the_same_process_ends_the_old_session_and_adopts_the_new_one()
    {
        using var rig = new Rig();
        rig.Probe.Start(10, FileTime(T0.AddMinutes(-6)));
        Record(rig.Sessions, 10, "old", "idle", T0.AddMinutes(-5));
        rig.Sync();
        Record(rig.Sessions, 10, "new", "idle", T0.AddMinutes(-5));
        rig.Sync();
        Assert.Equal("new", rig.Tracker.Sessions.Single().SessionId);
    }

    [Fact]
    public void The_transcript_finder_falls_back_to_every_project_folder()
    {
        using var dir = new TempDir();
        var clock = new FakeClock(T0);
        var path = dir.File(Path.Combine("projects", "C--very-long-name-123abc", "s9.jsonl"), "");
        var finder = new ClaudeTranscriptFinder(Path.Combine(dir.Path, "projects"), clock);
        Assert.Equal("C--Users-demo-My-proj", ClaudeTranscriptFinder.ProjectDirName(@"C:\Users\demo\My proj"));
        Assert.Equal(path, finder.Find(@"C:\somewhere\else", "s9"));
        Assert.Null(finder.Find(null, "../s9"));

        Assert.Null(finder.Find(null, "later"));
        var later = dir.File(Path.Combine("projects", "p", "later.jsonl"), "");
        Assert.Null(finder.Find(null, "later"));                  // the miss is cached for a while
        clock.Advance(ClaudeTranscriptFinder.MissTtl + TimeSpan.FromSeconds(1));
        Assert.Equal(later, finder.Find(null, "later"));
    }

    [Fact]
    public void Pump_adopts_registry_sessions_binds_their_process_and_leaves_hook_sessions_alone()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(T0);
        var probe = new FakeProcessProbe().Start(10, FileTime(T0.AddMinutes(-6))).Start(20, FileTime(T0.AddMinutes(-6)));
        Record(paths.ClaudeSessionsDir, 10, "app", "busy", T0.AddMinutes(-5));
        Record(paths.ClaudeSessionsDir, 20, "cli", "busy", T0.AddMinutes(-5), entrypoint: "cli");
        Directory.CreateDirectory(paths.MonitorDir);
        File.WriteAllText(paths.EventsFile,
            $$"""{"ts":"{{T0.AddMinutes(-1):yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"claude","event":"UserPromptSubmit","session_id":"cli","cwd":"C:\\p\\cli"}""" + "\n");
        var tracker = new SessionTracker(clock);
        var raised = 0;
        tracker.Changed += _ => raised++;
        var processes = new SessionProcessRegistry(null, probe, clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            Processes = processes,
            ClaudeRegistry = new ClaudeRegistrySessionFeed(new ClaudeSessionRegistryReader(paths.ClaudeSessionsDir), probe, clock)
        };

        pump.Start();
        Assert.Equal(0, raised);                                   // adopted silently at start-up
        Assert.Equal(["app", "cli"], tracker.Sessions.Select(s => s.SessionId).Order());
        Assert.Equal(SessionOrigin.App, tracker.Sessions.Single(s => s.SessionId == "app").Origin);
        Assert.Equal(BindingSource.Registry, processes.Get(AgentKind.Claude, "cli")!.Source);
        Assert.Equal(10, processes.Get(AgentKind.Claude, "app")!.Pid);

        Record(paths.ClaudeSessionsDir, 10, "app", "idle", T0.AddMinutes(-5));
        clock.Advance(TimeSpan.FromSeconds(4));
        pump.Pump();
        Assert.Equal("finito", tracker.Sessions.Single(s => s.SessionId == "app").PhaseLabel);
        // The hook session is not driven by its record: it stays as its events left it.
        Record(paths.ClaudeSessionsDir, 20, "cli", "idle", T0.AddMinutes(-5), entrypoint: "cli");
        clock.Advance(TimeSpan.FromSeconds(4));
        pump.Pump();
        Assert.Equal(SessionPhase.Working, tracker.Sessions.Single(s => s.SessionId == "cli").Phase);
    }
}
