using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class HookEventPumpTests
{
    private static string SubagentLine(string evt, string sid, string agentId, DateTimeOffset ts) =>
        $$"""{"ts":"{{ts:yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"claude","event":"{{evt}}","session_id":"{{sid}}","cwd":"C:\\demo\\proj","notification_type":null,"message":null,"source":null,"agent_id":"{{agentId}}","agent_type":"general-purpose"}""" + "\n";

    private static string Line(string evt, string sid, DateTimeOffset ts, string? notificationType = null, string agent = "claude") =>
        $$"""{"ts":"{{ts:yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"{{agent}}","event":"{{evt}}","session_id":"{{sid}}","cwd":"C:\\demo\\proj","notification_type":{{(notificationType is null ? "null" : $"\"{notificationType}\"")}},"message":null,"source":null}""" + "\n";

    /// <summary>
    /// Writes the rollout of a Codex child thread under the sessions directory the scanner reads. The turn event
    /// carries <paramref name="lastActivity"/> as its timestamp, which is what the scanner reads to decide freshness.
    /// The session_meta mirrors the real format: <c>id</c> is the thread's own id and <c>session_id</c> is the root
    /// conversation, shared by every child of the same parent.
    /// </summary>
    private static void CodexChildRollout(AppPaths paths, string threadId, string parentThreadId, bool running, DateTimeOffset lastActivity)
    {
        var stamp = lastActivity.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var metaStamp = lastActivity.AddSeconds(-2).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var turn = running
            ? $$$"""{"timestamp":"{{{stamp}}}","type":"event_msg","payload":{"type":"task_started","turn_id":"t1"}}"""
            : $$$"""{"timestamp":"{{{stamp}}}","type":"event_msg","payload":{"type":"task_complete","turn_id":"t1","last_agent_message":"done"}}""";
        var meta = $$$"""{"timestamp":"{{{metaStamp}}}","type":"session_meta","payload":{"session_id":"{{{parentThreadId}}}","id":"{{{threadId}}}","parent_thread_id":"{{{parentThreadId}}}","cwd":"C:\\demo\\proj","thread_source":"subagent"}}""";
        var file = Path.Combine(paths.CodexSessionsDir, $"rollout-{threadId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, meta + "\n" + turn + "\n");
        File.SetLastWriteTimeUtc(file, lastActivity.UtcDateTime);
    }

    [Fact]
    public void Start_replays_recent_events_silently_and_drops_stale_sessions()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        Directory.CreateDirectory(paths.MonitorDir);
        File.WriteAllText(paths.EventsFile,
            Line("Stop", "ancient", now.AddHours(-30)) +
            Line("Stop", "stale", now.AddHours(-13)) +
            Line("UserPromptSubmit", "live", now.AddMinutes(-5)) +
            Line("SessionEnd", "ended", now.AddMinutes(-4)));
        var tracker = new SessionTracker(new FakeClock(now));
        var raised = 0;
        tracker.Changed += _ => raised++;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, new FakeClock(now));

        pump.Start();

        Assert.Equal("live", Assert.Single(tracker.Sessions).SessionId);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Pump_applies_new_lines_and_raises_changes()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var tracker = new SessionTracker(new FakeClock(now));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, new FakeClock(now));
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now));
        pump.Pump();
        File.AppendAllText(paths.EventsFile, Line("Notification", "s1", now.AddSeconds(1), "permission_prompt"));
        pump.Pump();

        Assert.Equal(2, changes.Count);
        Assert.Equal(SessionPhase.NeedsInput, tracker.Sessions.Single().Phase);
    }

    [Fact]
    public async Task Watcher_picks_up_appends_without_manual_pump()
    {
        using var dir = new TempDir();
        var now = DateTimeOffset.UtcNow;
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var tracker = new SessionTracker(new SystemClock());
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, new SystemClock());
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "w1", now));

        for (var i = 0; i < 50 && tracker.Sessions.Count == 0; i++) await Task.Delay(100);
        Assert.Single(tracker.Sessions);
    }

    private sealed class FlakyClock : IClock
    {
        public FlakyClock(DateTimeOffset now) => Now = now;
        public DateTimeOffset Now { get; set; }
        public bool Explode { get; set; }
        public DateTimeOffset UtcNow => Explode ? throw new InvalidOperationException("clock exploded") : Now;
    }

    [Fact]
    public void Pump_keeps_delivering_events_when_a_changed_subscriber_throws()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var trackerErrors = new List<Exception>();
        var tracker = new SessionTracker(new FakeClock(now)) { OnError = trackerErrors.Add };
        var seen = new List<string>();
        tracker.Changed += c => { seen.Add(c.Session.SessionId); throw new InvalidOperationException("subscriber on the UI thread"); };
        var pumpErrors = new List<Exception>();
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, new FakeClock(now))
        {
            OnError = pumpErrors.Add
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now) + Line("UserPromptSubmit", "s2", now.AddSeconds(1)));
        pump.Pump();
        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s3", now.AddSeconds(2)));
        pump.Pump();

        Assert.Equal(new[] { "s1", "s2", "s3" }, seen);
        Assert.Equal(3, tracker.Sessions.Count);
        Assert.Equal(3, trackerErrors.Count);
        Assert.Empty(pumpErrors);
    }

    [Fact]
    public void Pump_does_not_propagate_unexpected_errors_and_reports_them()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FlakyClock(now);
        var errors = new List<Exception>();
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), new SessionTracker(new FakeClock(now)), paths, clock)
        {
            OnError = errors.Add
        };
        pump.Start();

        clock.Explode = true;
        pump.Pump();

        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
    }

    [Fact]
    public void Start_survives_a_failing_replay()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        Directory.CreateDirectory(paths.MonitorDir);
        File.WriteAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now));
        var clock = new FlakyClock(now) { Explode = true };
        var errors = new List<Exception>();
        var tracker = new SessionTracker(new FakeClock(now));
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            OnError = errors.Add,
            PollInterval = TimeSpan.FromMinutes(10)
        };

        pump.Start();

        Assert.Single(errors);
        Assert.Empty(tracker.Sessions);

        // The pump stays usable: the next cycle picks the file up again.
        clock.Explode = false;
        pump.Pump();
        Assert.Single(tracker.Sessions);
    }

    [Fact]
    public void Start_releases_replayed_subagents_that_went_silent_without_toasting()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        Directory.CreateDirectory(paths.MonitorDir);
        File.WriteAllText(paths.EventsFile,
            Line("UserPromptSubmit", "s1", now.AddMinutes(-40)) +
            SubagentLine("SubagentStart", "s1", "a1", now.AddMinutes(-40)) +
            Line("Stop", "s1", now.AddMinutes(-40)));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var raised = 0;
        tracker.Changed += _ => raised++;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock);

        pump.Start();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Idle, session.Phase);
        Assert.Equal(0, session.ActiveSubagents);
        Assert.False(session.AwaitingSubagents);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void Pump_releases_a_session_whose_subagents_went_silent()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.Zero
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "s1", now) +
            SubagentLine("SubagentStart", "s1", "a1", now) +
            Line("Stop", "s1", now));
        pump.Pump();
        Assert.Equal("al lavoro · 1 agente", tracker.Sessions.Single().PhaseLabel);

        clock.Advance(TimeSpan.FromMinutes(31));
        pump.Pump();

        var last = changes.Last();
        Assert.Equal(SessionChangeKind.Updated, last.Kind);
        Assert.Equal(SessionPhase.Idle, last.Session.Phase);
        Assert.Equal(0, last.Session.ActiveSubagents);
        Assert.Equal("finito", last.Session.PhaseLabel);
    }

    [Fact]
    public void Pump_keeps_the_session_working_before_the_subagent_timeout()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.Zero
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "s1", now) +
            SubagentLine("SubagentStart", "s1", "a1", now) +
            Line("Stop", "s1", now));
        pump.Pump();

        clock.Advance(TimeSpan.FromMinutes(29));
        pump.Pump();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Working, session.Phase);
        Assert.Equal(1, session.ActiveSubagents);
    }

    [Fact]
    public void Pump_synthesises_subagent_events_for_the_live_child_threads_of_a_codex_session()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.Zero,
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, clock)
        };
        // Driven by hand: Start() would also arm the FileSystemWatcher and the 2 s poll, and a background Pump()
        // racing these explicit ones would scan the rollouts at an unpredictable moment.
        Directory.CreateDirectory(paths.MonitorDir);

        CodexChildRollout(paths, "child-1", "c1", running: true, now.AddSeconds(-20));
        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "c1", now, agent: "codex") +
            Line("Stop", "c1", now, agent: "codex"));
        pump.Pump();

        var working = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Working, working.Phase);
        Assert.Equal("al lavoro · 1 agente", working.PhaseLabel);
        Assert.Equal("child-1", Assert.Single(working.Subagents!).AgentId);
        Assert.Equal(CodexSubagentScanner.SyntheticAgentType, working.Subagents!.Single().AgentType);
        // The Stop must never be applied before the child is announced: an Idle raised here would toast "finito"
        // while the child thread is still running, and the row would flip back to "al lavoro" on the next scan.
        Assert.DoesNotContain(changes, c => c.Session.Phase == SessionPhase.Idle);

        clock.Advance(TimeSpan.FromSeconds(30));
        CodexChildRollout(paths, "child-1", "c1", running: false, clock.UtcNow);
        pump.Pump();

        var done = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Idle, done.Phase);
        Assert.Equal(0, done.ActiveSubagents);
        Assert.Equal("finito", done.PhaseLabel);
        Assert.Equal(SessionPhase.Working, changes.Last().PreviousPhase);
        // Exactly one completion over the whole scenario: one toast, not one early and one late.
        Assert.Single(changes, c => c.Session.Phase == SessionPhase.Idle);
    }

    /// <summary>
    /// Two child threads of the same Codex session run at once: the row must read "al lavoro · 2 agenti", count down
    /// to "al lavoro · 1 agente" when the first one completes and reach "finito" — with a single Idle change over the
    /// whole scenario, so the App toasts "Turno completato" exactly once — when the second one does.
    /// </summary>
    [Fact]
    public void Pump_counts_two_concurrent_codex_child_threads_and_toasts_once()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.Zero,
            CodexScanEvery = TimeSpan.Zero,
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, clock)
        };
        // Driven by hand: Start() would also arm the FileSystemWatcher and the 2 s poll, and a background Pump()
        // racing these explicit ones would scan the rollouts at an unpredictable moment.
        Directory.CreateDirectory(paths.MonitorDir);

        CodexChildRollout(paths, "child-1", "c1", running: true, now.AddSeconds(-20));
        CodexChildRollout(paths, "child-2", "c1", running: true, now.AddSeconds(-10));
        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "c1", now, agent: "codex") +
            Line("Stop", "c1", now, agent: "codex"));
        pump.Pump();

        var both = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Working, both.Phase);
        Assert.Equal(2, both.ActiveSubagents);
        Assert.Equal("al lavoro · 2 agenti", both.PhaseLabel);
        Assert.Equal(["child-1", "child-2"],
            both.Subagents!.Select(s => s.AgentId).OrderBy(id => id, StringComparer.Ordinal));

        clock.Advance(TimeSpan.FromSeconds(30));
        CodexChildRollout(paths, "child-1", "c1", running: false, clock.UtcNow);
        pump.Pump();

        var one = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Working, one.Phase);
        Assert.Equal("al lavoro · 1 agente", one.PhaseLabel);
        Assert.DoesNotContain(changes, c => c.Session.Phase == SessionPhase.Idle);

        clock.Advance(TimeSpan.FromSeconds(30));
        CodexChildRollout(paths, "child-2", "c1", running: false, clock.UtcNow);
        pump.Pump();

        var done = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Idle, done.Phase);
        Assert.Equal("finito", done.PhaseLabel);
        Assert.Single(changes, c => c.Session.Phase == SessionPhase.Idle);
    }

    /// <summary>
    /// A child that goes quiet inside an open turn (a long exec appends nothing to its rollout) must keep its session
    /// at "al lavoro": releasing it would toast "Turno completato" mid-turn and toast again at the real end.
    /// </summary>
    [Fact]
    public void Pump_keeps_a_quiet_codex_child_whose_turn_is_still_open()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.Zero,
            CodexScanEvery = TimeSpan.Zero,
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, clock)
        };
        // Driven by hand: Start() would also arm the FileSystemWatcher and the 2 s poll, and a background Pump()
        // racing these explicit ones would scan the rollouts at an unpredictable moment.
        Directory.CreateDirectory(paths.MonitorDir);

        CodexChildRollout(paths, "child-1", "c1", running: true, now.AddSeconds(-20));
        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "c1", now, agent: "codex") +
            Line("Stop", "c1", now, agent: "codex"));
        pump.Pump();

        // Five minutes of silence while the child runs a long tool call: the rollout is not touched at all.
        clock.Advance(TimeSpan.FromMinutes(5));
        pump.Pump();

        var live = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Working, live.Phase);
        Assert.Equal("al lavoro · 1 agente", live.PhaseLabel);
        Assert.DoesNotContain(changes, c => c.Session.Phase == SessionPhase.Idle);
    }

    /// <summary>The rollout scan has its own cadence: it must not wait for the 5-minute stale sweep.</summary>
    [Fact]
    public void Pump_scans_codex_rollouts_on_its_own_cadence()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.FromHours(1),
            CodexScanEvery = TimeSpan.FromSeconds(30),
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, clock)
        };
        // Driven by hand: Start() would also arm the FileSystemWatcher and the 2 s poll, and a background Pump()
        // racing these explicit ones would scan the rollouts at an unpredictable moment.
        Directory.CreateDirectory(paths.MonitorDir);

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "c1", now, agent: "codex"));
        pump.Pump();

        // A child thread that starts mid-turn, with no hook event to trigger a scan.
        clock.Advance(TimeSpan.FromSeconds(31));
        CodexChildRollout(paths, "child-1", "c1", running: true, clock.UtcNow);
        pump.Pump();

        Assert.Equal(1, Assert.Single(tracker.Sessions).ActiveSubagents);
    }

    /// <summary>A scan that could not read the rollouts must not be taken for "every child finished".</summary>
    [Fact]
    public void Pump_keeps_the_codex_children_when_the_rollout_scan_fails()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.Zero,
            CodexScanEvery = TimeSpan.Zero,
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, clock)
        };
        // Driven by hand: Start() would also arm the FileSystemWatcher and the 2 s poll, and a background Pump()
        // racing these explicit ones would scan the rollouts at an unpredictable moment.
        Directory.CreateDirectory(paths.MonitorDir);

        CodexChildRollout(paths, "child-1", "c1", running: true, now.AddSeconds(-20));
        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "c1", now, agent: "codex") +
            Line("Stop", "c1", now, agent: "codex"));
        pump.Pump();
        Assert.Equal(1, Assert.Single(tracker.Sessions).ActiveSubagents);

        // Renamed rather than deleted: a rename is atomic, while a deleted directory can stay visible (and
        // enumerate as empty) for a moment, which would make the scan succeed with no children.
        Directory.Move(paths.CodexSessionsDir, paths.CodexSessionsDir + ".away");
        clock.Advance(TimeSpan.FromSeconds(30));
        pump.Pump();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Working, session.Phase);
        Assert.Equal(1, session.ActiveSubagents);
    }

    /// <summary>A child thread that outlives the subagent timeout is re-announced, so the sweep never releases it.</summary>
    [Fact]
    public void Pump_reannounces_a_codex_child_that_is_still_running()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.Zero,
            CodexScanEvery = TimeSpan.Zero,
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, clock)
        };
        // Driven by hand: Start() would also arm the FileSystemWatcher and the 2 s poll, and a background Pump()
        // racing these explicit ones would scan the rollouts at an unpredictable moment.
        Directory.CreateDirectory(paths.MonitorDir);

        CodexChildRollout(paths, "child-1", "c1", running: true, now.AddSeconds(-20));
        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "c1", now, agent: "codex") +
            Line("Stop", "c1", now, agent: "codex"));
        pump.Pump();

        // The child keeps working for 40 minutes, well past SubagentTimeout.
        for (var minute = 10; minute <= 40; minute += 10)
        {
            clock.Advance(TimeSpan.FromMinutes(10));
            CodexChildRollout(paths, "child-1", "c1", running: true, clock.UtcNow);
            pump.Pump();

            var live = Assert.Single(tracker.Sessions);
            Assert.Equal(SessionPhase.Working, live.Phase);
            Assert.Equal(1, live.ActiveSubagents);
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        CodexChildRollout(paths, "child-1", "c1", running: false, clock.UtcNow);
        pump.Pump();

        var done = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Idle, done.Phase);
        Assert.Equal("finito", done.PhaseLabel);
    }

    [Fact]
    public void Start_picks_up_codex_child_threads_without_toasting_the_replay()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        Directory.CreateDirectory(paths.MonitorDir);
        File.WriteAllText(paths.EventsFile,
            Line("UserPromptSubmit", "c1", now.AddMinutes(-2), agent: "codex") +
            Line("Stop", "c1", now.AddMinutes(-1), agent: "codex"));
        CodexChildRollout(paths, "child-1", "c1", running: true, now.AddSeconds(-20));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var raised = 0;
        tracker.Changed += _ => raised++;
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, clock)
        };

        pump.Start();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Working, session.Phase);
        Assert.Equal(1, session.ActiveSubagents);
        Assert.Equal(0, raised);
    }

    /// <summary>
    /// Stands in for the App's counters: it hands back whatever the test staged for a session (and its subagents)
    /// and records which sessions were asked, so a test can tell a refresh that happened from one that did not.
    /// </summary>
    private sealed class FakeTokenSource : ITokenSource
    {
        public Dictionary<string, TokenUsage> Session { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Dictionary<string, TokenUsage>> Subagents { get; } = new(StringComparer.Ordinal);
        public List<string> Reads { get; } = [];
        public Exception? Throw { get; set; }

        public TokenUsage? SessionTokens(SessionState session)
        {
            Reads.Add(session.SessionId);
            if (Throw is not null) throw Throw;
            return Session.TryGetValue(session.SessionId, out var tokens) ? tokens : null;
        }

        public IReadOnlyDictionary<string, TokenUsage>? SubagentTokens(SessionState session) =>
            Subagents.TryGetValue(session.SessionId, out var subagents) ? subagents : null;
    }

    [Fact]
    public void Pump_refreshes_the_tokens_right_after_a_stop()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var source = new FakeTokenSource();
        source.Session["s1"] = new TokenUsage(11, 22, 33, 44);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            // Long cadence: only the Stop can refresh here, so the test cannot pass by way of the periodic pass.
            TokenRefreshEvery = TimeSpan.FromHours(1),
            TokenSource = source
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now));
        pump.Pump();
        Assert.Null(Assert.Single(tracker.Sessions).Tokens);

        File.AppendAllText(paths.EventsFile, Line("Stop", "s1", now.AddSeconds(1)));
        pump.Pump();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Idle, session.Phase);
        Assert.Equal(new TokenUsage(11, 22, 33, 44), session.Tokens);
    }

    [Fact]
    public void Pump_refreshes_the_tokens_right_after_a_stop_failure()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var source = new FakeTokenSource();
        source.Session["s1"] = new TokenUsage(1, 2, 3, 4);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            TokenRefreshEvery = TimeSpan.FromHours(1),
            TokenSource = source
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now) + Line("StopFailure", "s1", now.AddSeconds(1)));
        pump.Pump();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Error, session.Phase);
        Assert.Equal(new TokenUsage(1, 2, 3, 4), session.Tokens);
    }

    [Fact]
    public void Pump_refreshes_the_subagent_tokens_right_after_a_subagent_stop()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var source = new FakeTokenSource();
        source.Session["s1"] = new TokenUsage(10, 10, 10, 10);
        source.Subagents["s1"] = new Dictionary<string, TokenUsage>(StringComparer.Ordinal) { ["a1"] = new(1, 1, 1, 1) };
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            TokenRefreshEvery = TimeSpan.FromHours(1),
            TokenSource = source
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "s1", now) +
            SubagentLine("SubagentStart", "s1", "a1", now.AddSeconds(1)) +
            Line("Stop", "s1", now.AddSeconds(2)) +
            SubagentLine("SubagentStop", "s1", "a1", now.AddSeconds(3)));
        pump.Pump();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Idle, session.Phase);
        Assert.Equal(new TokenUsage(10, 10, 10, 10), session.Tokens);
        Assert.Equal(new TokenUsage(1, 1, 1, 1), session.SubagentTokens);
    }

    [Fact]
    public void Pump_refreshes_the_working_sessions_on_the_token_cadence_and_leaves_the_idle_ones_alone()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var source = new FakeTokenSource();
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            TokenRefreshEvery = TimeSpan.FromSeconds(30),
            TokenSource = source
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "working", now) +
            Line("Stop", "idle", now) +
            Line("Notification", "needsinput", now, "permission_prompt"));
        pump.Pump();
        Assert.Equal(3, tracker.Sessions.Count);
        source.Reads.Clear(); // the Stop of "idle" refreshed once, as it should

        source.Session["working"] = new TokenUsage(1, 0, 0, 0);
        source.Session["needsinput"] = new TokenUsage(2, 0, 0, 0);
        source.Session["idle"] = new TokenUsage(3, 0, 0, 0);
        clock.Advance(TimeSpan.FromSeconds(31));
        pump.Pump();

        Assert.Equal(["needsinput", "working"], source.Reads.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(new TokenUsage(1, 0, 0, 0), tracker.Sessions.Single(s => s.SessionId == "working").Tokens);
        Assert.Equal(new TokenUsage(2, 0, 0, 0), tracker.Sessions.Single(s => s.SessionId == "needsinput").Tokens);
        Assert.Null(tracker.Sessions.Single(s => s.SessionId == "idle").Tokens);
    }

    [Fact]
    public void Pump_does_not_refresh_the_tokens_before_the_cadence_elapses()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var source = new FakeTokenSource();
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            TokenRefreshEvery = TimeSpan.FromSeconds(30),
            TokenSource = source
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now));
        pump.Pump();
        clock.Advance(TimeSpan.FromSeconds(29));
        pump.Pump();

        Assert.Empty(source.Reads);
    }

    [Fact]
    public void Pump_raises_no_change_when_the_token_totals_are_unchanged()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        var source = new FakeTokenSource();
        source.Session["s1"] = new TokenUsage(1, 2, 3, 4);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            TokenRefreshEvery = TimeSpan.FromSeconds(30),
            TokenSource = source
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now));
        pump.Pump();
        Assert.Single(changes);

        clock.Advance(TimeSpan.FromSeconds(31));
        pump.Pump();
        Assert.Equal(2, changes.Count); // the first totals land as one Updated

        clock.Advance(TimeSpan.FromSeconds(31));
        pump.Pump();
        Assert.Equal(2, changes.Count); // same totals: no UI churn
    }

    [Fact]
    public void Pump_reports_a_failing_token_source_and_keeps_pumping()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var errors = new List<Exception>();
        var source = new FakeTokenSource { Throw = new IOException("transcript locked") };
        source.Session["s1"] = new TokenUsage(1, 2, 3, 4);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            TokenRefreshEvery = TimeSpan.FromSeconds(30),
            TokenSource = source,
            OnError = errors.Add
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now) + Line("Stop", "s1", now.AddSeconds(1)));
        pump.Pump();

        Assert.IsType<IOException>(Assert.Single(errors));
        Assert.Equal(SessionPhase.Idle, Assert.Single(tracker.Sessions).Phase);

        // The next cycle picks the counters up again.
        source.Throw = null;
        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", now.AddSeconds(2)) + Line("Stop", "s1", now.AddSeconds(3)));
        pump.Pump();

        Assert.Equal(new TokenUsage(1, 2, 3, 4), Assert.Single(tracker.Sessions).Tokens);
    }

    /// <summary>
    /// The last SubagentStop of a Codex session is synthesised by the rollout scan, not read from events.jsonl: it
    /// must refresh the tokens too, or the row would keep the totals of the child's second-to-last scan forever.
    /// </summary>
    [Fact]
    public void Pump_refreshes_the_tokens_when_the_last_codex_child_thread_stops()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var source = new FakeTokenSource();
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            StaleSweepEvery = TimeSpan.Zero,
            CodexScanEvery = TimeSpan.Zero,
            TokenRefreshEvery = TimeSpan.FromHours(1),
            TokenSource = source,
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, clock)
        };
        // Driven by hand: Start() would also arm the FileSystemWatcher and the 2 s poll, and a background Pump()
        // racing these explicit ones would scan the rollouts at an unpredictable moment.
        Directory.CreateDirectory(paths.MonitorDir);

        CodexChildRollout(paths, "child-1", "c1", running: true, now.AddSeconds(-20));
        File.AppendAllText(paths.EventsFile,
            Line("UserPromptSubmit", "c1", now, agent: "codex") +
            Line("Stop", "c1", now, agent: "codex"));
        pump.Pump();

        source.Session["c1"] = new TokenUsage(100, 200, 300, 400);
        source.Subagents["c1"] = new Dictionary<string, TokenUsage>(StringComparer.Ordinal) { ["child-1"] = new(1, 2, 3, 4) };
        clock.Advance(TimeSpan.FromSeconds(30));
        CodexChildRollout(paths, "child-1", "c1", running: false, clock.UtcNow);
        pump.Pump();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(SessionPhase.Idle, session.Phase);
        Assert.Equal(new TokenUsage(100, 200, 300, 400), session.Tokens);
        Assert.Equal(new TokenUsage(1, 2, 3, 4), session.SubagentTokens);
    }

    /// <summary>
    /// The silent replay restores sessions whose turn ended before the app started: nothing will ever apply a Stop
    /// for them and the periodic pass skips them (they are Idle), so without the one-shot fill their token column
    /// would stay empty until the user typed a new prompt — most rows, after every restart.
    /// </summary>
    [Fact]
    public void Pump_fills_the_tokens_of_every_replayed_session_once_and_silently()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        Directory.CreateDirectory(paths.MonitorDir);
        File.WriteAllText(paths.EventsFile,
            Line("UserPromptSubmit", "idle", now.AddMinutes(-20)) +
            SubagentLine("SubagentStart", "idle", "a1", now.AddMinutes(-19)) +
            SubagentLine("SubagentStop", "idle", "a1", now.AddMinutes(-18)) +
            Line("Stop", "idle", now.AddMinutes(-17)) +
            Line("StopFailure", "error", now.AddMinutes(-15)) +
            Line("Notification", "needsinput", now.AddMinutes(-10), "permission_prompt") +
            Line("UserPromptSubmit", "working", now.AddMinutes(-5)));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        var source = new FakeTokenSource();
        source.Session["idle"] = new TokenUsage(1, 0, 0, 0);
        source.Session["error"] = new TokenUsage(2, 0, 0, 0);
        source.Session["needsinput"] = new TokenUsage(3, 0, 0, 0);
        source.Session["working"] = new TokenUsage(4, 0, 0, 0);
        source.Subagents["idle"] = new Dictionary<string, TokenUsage>(StringComparer.Ordinal) { ["a1"] = new(9, 0, 0, 0) };
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            // No background poll: the one-shot fill must be observed on the explicit Pump() calls of this test.
            PollInterval = TimeSpan.FromHours(1),
            TokenRefreshEvery = TimeSpan.FromSeconds(30),
            TokenSource = source
        };

        pump.Start();

        // Start() runs on the UI thread: reading a large transcript there would freeze the notch at launch.
        Assert.Empty(source.Reads);
        Assert.All(tracker.Sessions, s => Assert.Null(s.Tokens));

        pump.Pump();

        Assert.Equal(["error", "idle", "needsinput", "working"], source.Reads.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(new TokenUsage(1, 0, 0, 0), tracker.Sessions.Single(s => s.SessionId == "idle").Tokens);
        Assert.Equal(new TokenUsage(9, 0, 0, 0), tracker.Sessions.Single(s => s.SessionId == "idle").SubagentTokens);
        Assert.Equal(new TokenUsage(2, 0, 0, 0), tracker.Sessions.Single(s => s.SessionId == "error").Tokens);
        Assert.Equal(new TokenUsage(3, 0, 0, 0), tracker.Sessions.Single(s => s.SessionId == "needsinput").Tokens);
        Assert.Equal(new TokenUsage(4, 0, 0, 0), tracker.Sessions.Single(s => s.SessionId == "working").Tokens);
        // Silent: a non-silent fill would toast "Errore API" and "Input richiesto" for sessions replayed from history.
        Assert.Empty(changes);

        // One shot only: from here on the cadence rules again, so the Idle and Error rows are left alone.
        source.Reads.Clear();
        clock.Advance(TimeSpan.FromSeconds(31));
        pump.Pump();

        Assert.Equal(["needsinput", "working"], source.Reads.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void Pump_does_not_fill_the_tokens_when_there_is_no_token_source()
    {
        using var dir = new TempDir();
        var now = new DateTimeOffset(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        Directory.CreateDirectory(paths.MonitorDir);
        File.WriteAllText(paths.EventsFile, Line("Stop", "idle", now.AddMinutes(-5)));
        var clock = new FakeClock(now);
        var tracker = new SessionTracker(clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            PollInterval = TimeSpan.FromHours(1)
        };

        pump.Start();
        pump.Pump();

        Assert.Null(Assert.Single(tracker.Sessions).Tokens);
    }
}
