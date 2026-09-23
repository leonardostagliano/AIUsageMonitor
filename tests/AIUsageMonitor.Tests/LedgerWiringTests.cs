using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class LedgerWiringTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static UsageLedger Ledger(string model, long input) =>
        UsageLedger.From([KeyValuePair.Create(new UsageKey(model, PriceTier.Standard, null, 0), new LedgerTokens(input, 0, 0, 0, 0))]);

    private static HookEvent Ev(string evt, string sid = "s1", string? agentId = null, int plusSeconds = 0, AgentKind agent = AgentKind.Claude) =>
        new(T0.AddSeconds(plusSeconds), agent, evt, sid, "C:\\demo", null, null, null, AgentId: agentId);

    private static string Line(string evt, string sid, DateTimeOffset ts, string agent = "claude") =>
        $$"""{"ts":"{{ts:yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"{{agent}}","event":"{{evt}}","session_id":"{{sid}}","cwd":"C:\\demo\\proj","notification_type":null,"message":null,"source":null}""" + "\n";

    [Fact]
    public void UpdateTokens_stores_the_ledgers_and_raises_only_when_one_changed()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        tracker.UpdateTokens(AgentKind.Claude, "s1", null, null, null, Ledger("m", 5), new Dictionary<string, UsageLedger> { ["a1"] = Ledger("n", 7) });
        tracker.UpdateTokens(AgentKind.Claude, "s1", null, null, null, Ledger("m", 5), new Dictionary<string, UsageLedger> { ["a1"] = Ledger("n", 7) });
        tracker.UpdateTokens(AgentKind.Claude, "s1", null, null, null, null, null);

        Assert.Single(changes);
        var s = Assert.Single(tracker.Sessions);
        Assert.Equal(Ledger("m", 5), s.Ledger);
        Assert.Equal(Ledger("n", 7), s.Subagents!.Single().Ledger);
        Assert.Equal(Ledger("n", 7), s.SubagentLedger);
        Assert.Equal(Ledger("n", 7), s.ActiveSubagentLedger);
    }

    [Fact]
    public void UpdateTokensSilently_stores_the_ledgers_without_raising()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        var raised = 0;
        tracker.Changed += _ => raised++;

        tracker.UpdateTokensSilently(AgentKind.Claude, "s1", null, null, null, Ledger("m", 1), null);

        Assert.Equal(0, raised);
        Assert.Equal(Ledger("m", 1), tracker.Sessions.Single().Ledger);
    }

    private sealed class LedgerSource : ITokenSource
    {
        public TokenUsage? SessionTokens(SessionState session) => new(1, 1, 0, 0);
        public IReadOnlyDictionary<string, TokenUsage>? SubagentTokens(SessionState session) => null;
        public UsageLedger? SessionLedger(SessionState session) => Ledger("m", 1);
    }

    [Fact]
    public void The_pump_hands_the_session_ledger_to_the_tracker()
    {
        using var dir = new TempDir();
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        using var pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), tracker, paths, clock)
        {
            TokenRefreshEvery = TimeSpan.FromHours(1),
            TokenSource = new LedgerSource()
        };
        pump.Start();

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", T0) + Line("Stop", "s1", T0.AddSeconds(1)));
        pump.Pump();

        Assert.Equal(Ledger("m", 1), Assert.Single(tracker.Sessions).Ledger);
    }

    [Fact]
    public void AppTokenSource_reports_the_Claude_ledgers_of_the_session_and_of_its_subagents()
    {
        using var dir = new TempDir();
        var transcript = dir.File("projects/session.jsonl",
            """{"type":"assistant","requestId":"r1","message":{"id":"m1","model":"claude-opus-5-5","usage":{"input_tokens":3,"output_tokens":4,"cache_read_input_tokens":5,"cache_creation_input_tokens":6}}}""" + "\n");
        var agent = dir.File("projects/session/subagents/agent-a1.jsonl",
            """{"type":"assistant","requestId":"r2","message":{"id":"m2","model":"claude-fable-5-1","usage":{"input_tokens":1,"output_tokens":1,"cache_read_input_tokens":0,"cache_creation_input_tokens":0}}}""" + "\n");
        var session = new SessionState(AgentKind.Claude, "session", "demo", null, SessionPhase.Working, null, T0, T0,
            TranscriptPath: transcript, Subagents: [new("a1", "workflow", SubagentPhase.Done, T0, T0, agent, TokenUsage.Zero)]);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(T0));

        var tokens = source.SessionTokens(session);
        source.SubagentTokens(session);

        Assert.Equal(tokens, source.SessionLedger(session)!.ToTokenUsage());
        Assert.Equal("claude-opus-5-5", source.SessionLedger(session)!.Entries.Single().Key.Model);
        Assert.Equal("claude-fable-5-1", source.SubagentLedgers(session)!["a1"].Entries.Single().Key.Model);
    }

    [Fact]
    public void AppTokenSource_reports_the_Codex_ledger_from_the_thread_rollout()
    {
        using var dir = new TempDir();
        const string thread = "01a0cb74-88e5-7a93-8cf2-1a6c08b6a9c3";
        dir.File($".codex/sessions/2026/09/23/rollout-2026-09-23T10-00-00-{thread}.jsonl",
            """{"timestamp":"2026-09-23T10:00:00.000Z","type":"turn_context","payload":{"model":"gpt-6-luna"}}""" + "\n" +
            """{"timestamp":"2026-09-23T10:00:01.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":100,"cached_input_tokens":40,"cache_write_input_tokens":0,"output_tokens":7,"reasoning_output_tokens":0,"total_tokens":107}},"rate_limits":null}}""" + "\n");
        var session = new SessionState(AgentKind.Codex, thread, "demo", null, SessionPhase.Idle, null, T0, T0);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(T0));

        var tokens = source.SessionTokens(session);
        var ledger = source.SessionLedger(session);

        Assert.Equal(tokens, ledger!.ToTokenUsage());
        Assert.Equal("gpt-6-luna", ledger.Entries.Single().Key.Model);
    }
}
