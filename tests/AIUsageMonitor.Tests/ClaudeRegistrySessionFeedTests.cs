using System.Text.Json;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Sessions;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ClaudeRegistrySessionFeedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    /// <summary>FILETIME of an instant, as the probe reports a process creation time.</summary>
    private static long FileTime(DateTimeOffset at) => at.UtcDateTime.ToFileTimeUtc();

    private static void Record(string dir, int pid, string sessionId, string status, DateTimeOffset startedAt,
        string entrypoint = "claude-desktop", string kind = "interactive", string? waitingFor = null, string cwd = @"C:\p\demo")
    {
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pid"] = pid, ["sessionId"] = sessionId, ["cwd"] = cwd, ["startedAt"] = startedAt.ToUnixTimeMilliseconds(),
            ["procStart"] = "683", ["version"] = "2.1.282", ["kind"] = kind, ["entrypoint"] = entrypoint,
            ["status"] = status, ["waitingFor"] = waitingFor, ["updatedAt"] = startedAt.ToUnixTimeMilliseconds()
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
