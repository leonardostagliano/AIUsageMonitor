using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class HookEventPumpTests
{
    private static string SubagentLine(string evt, string sid, string agentId, DateTimeOffset ts) =>
        $$"""{"ts":"{{ts:yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"claude","event":"{{evt}}","session_id":"{{sid}}","cwd":"C:\\demo\\proj","notification_type":null,"message":null,"source":null,"agent_id":"{{agentId}}","agent_type":"general-purpose"}""" + "\n";

    private static string Line(string evt, string sid, DateTimeOffset ts, string? notificationType = null) =>
        $$"""{"ts":"{{ts:yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"claude","event":"{{evt}}","session_id":"{{sid}}","cwd":"C:\\demo\\proj","notification_type":{{(notificationType is null ? "null" : $"\"{notificationType}\"")}},"message":null,"source":null}""" + "\n";

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
}
