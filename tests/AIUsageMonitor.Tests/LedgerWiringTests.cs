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
    public void SubagentLedger_sums_every_known_subagent_and_ActiveSubagentLedger_only_the_running_ones()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.Apply(Ev("SubagentStart", agentId: "a2", plusSeconds: 2));
        tracker.Apply(Ev("SubagentStop", agentId: "a2", plusSeconds: 3));

        tracker.UpdateTokens(AgentKind.Claude, "s1", null, null, null, null,
            new Dictionary<string, UsageLedger> { ["a1"] = Ledger("n", 7), ["a2"] = Ledger("k", 3) });

        var s = Assert.Single(tracker.Sessions);
        Assert.Equal(Ledger("n", 7) + Ledger("k", 3), s.SubagentLedger);
        Assert.Equal(Ledger("n", 7), s.ActiveSubagentLedger);
    }

    [Fact]
    public void Each_ledger_raises_Changed_on_its_own_and_keeps_the_token_update_of_the_same_call()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", agentId: "a1", plusSeconds: 1));
        tracker.UpdateTokens(AgentKind.Claude, "s1", null, null, null, Ledger("m", 5), new Dictionary<string, UsageLedger> { ["a1"] = Ledger("n", 7) });
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        // Only the session ledger moves.
        tracker.UpdateTokens(AgentKind.Claude, "s1", null, null, null, Ledger("m", 6), null);
        Assert.Single(changes);
        Assert.Equal(Ledger("m", 6), tracker.Sessions.Single().Ledger);

        // Only the ledger of a1 moves (the session ledger is not reported at all).
        tracker.UpdateTokens(AgentKind.Claude, "s1", null, null, null, null, new Dictionary<string, UsageLedger> { ["a1"] = Ledger("n", 8) });
        Assert.Equal(2, changes.Count);
        Assert.Equal(Ledger("n", 8), tracker.Sessions.Single().Subagents!.Single().Ledger);

        // Tokens and ledger of a1 move in the same call, next to an unchanged session ledger: one change, and neither
        // update of the subagent overwrites the other.
        var tokens = new TokenUsage(9, 1, 0, 0);
        tracker.UpdateTokens(AgentKind.Claude, "s1", null, new Dictionary<string, TokenUsage> { ["a1"] = tokens }, null, Ledger("m", 6),
            new Dictionary<string, UsageLedger> { ["a1"] = Ledger("n", 9) });
        Assert.Equal(3, changes.Count);
        var session = Assert.Single(tracker.Sessions);
        Assert.Same(session, changes[^1].Session);
        Assert.Equal(Ledger("m", 6), session.Ledger);
        var a1 = Assert.Single(session.Subagents!);
        Assert.Equal(tokens, a1.Tokens);
        Assert.Equal(Ledger("n", 9), a1.Ledger);
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
        public IReadOnlyDictionary<string, UsageLedger>? SubagentLedgers(SessionState session) =>
            new Dictionary<string, UsageLedger> { ["a1"] = Ledger("n", 1) };
    }

    private static string SubagentLine(string evt, string sid, string agentId, DateTimeOffset ts) =>
        $$"""{"ts":"{{ts:yyyy-MM-ddTHH:mm:ss.fffZ}}","agent":"claude","event":"{{evt}}","session_id":"{{sid}}","cwd":"C:\\demo\\proj","notification_type":null,"message":null,"source":null,"agent_id":"{{agentId}}","agent_type":"general-purpose"}""" + "\n";

    [Fact]
    public void The_pump_hands_the_session_and_subagent_ledgers_to_the_tracker()
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

        File.AppendAllText(paths.EventsFile, Line("UserPromptSubmit", "s1", T0) + SubagentLine("SubagentStart", "s1", "a1", T0.AddSeconds(1))
                                             + Line("Stop", "s1", T0.AddSeconds(2)));
        pump.Pump();

        var session = Assert.Single(tracker.Sessions);
        Assert.Equal(Ledger("m", 1), session.Ledger);
        Assert.Equal(Ledger("n", 1), Assert.Single(session.Subagents!).Ledger);
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

    private static string CodexTokenCount(long input, long cached, long output) =>
        $$$"""{"timestamp":"2026-09-23T10:05:01.000Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":{{{input}}},"cached_input_tokens":{{{cached}}},"cache_write_input_tokens":0,"output_tokens":{{{output}}},"reasoning_output_tokens":0,"total_tokens":{{{input + output}}}}},"rate_limits":null}}""" + "\n";

    [Fact]
    public void AppTokenSource_reports_the_Codex_ledger_of_a_child_thread_and_follows_it_once_finished()
    {
        using var dir = new TempDir();
        const string parent = "01a0cb74-88e5-7a93-8cf2-1a6c08b6a9c3";
        const string child = "01a0cb74-99f6-7b04-9d03-2b7d19c7bad4";
        var rollout = dir.File($".codex/sessions/2026/09/23/rollout-2026-09-23T10-05-00-{child}.jsonl",
            $$$"""{"timestamp":"2026-09-23T10:05:00.000Z","type":"session_meta","payload":{"session_id":"{{{parent}}}","id":"{{{child}}}","parent_thread_id":"{{{parent}}}","cwd":"C:\\demo\\proj"}}""" + "\n" +
            """{"timestamp":"2026-09-23T10:05:00.500Z","type":"turn_context","payload":{"model":"gpt-6-sol"}}""" + "\n" +
            CodexTokenCount(50, 10, 5));
        File.SetLastWriteTimeUtc(rollout, T0.UtcDateTime);
        var clock = new FakeClock(T0);
        var session = new SessionState(AgentKind.Codex, parent, "demo", null, SessionPhase.Working, null, T0, T0,
            Subagents: [new(child, "worker", SubagentPhase.Done, T0, T0, null, TokenUsage.Zero)]);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), clock);

        var tokens = source.SubagentTokens(session)![child];
        var ledger = source.SubagentLedgers(session)![child];
        Assert.Equal(tokens, ledger.ToTokenUsage());
        Assert.Equal("gpt-6-sol", ledger.Entries.Single().Key.Model);

        // The finished child still writes a last total, read after every cache of the counter has expired.
        File.AppendAllText(rollout, CodexTokenCount(80, 20, 9));
        File.SetLastWriteTimeUtc(rollout, T0.UtcDateTime);
        clock.Advance(CodexTokenCounter.CacheTtl + TimeSpan.FromSeconds(1));

        tokens = source.SubagentTokens(session)![child];
        Assert.Equal(new TokenUsage(60, 9, 20, 0), tokens);
        Assert.Equal(tokens, source.SubagentLedgers(session)![child].ToTokenUsage());
    }

    private const string CodexParent = "01a0cb74-88e5-7a93-8cf2-1a6c08b6a9c3";

    private static string ForkedChildMeta(string child) =>
        $$$"""{"timestamp":"2026-09-23T10:05:00.000Z","type":"session_meta","payload":{"session_id":"{{{CodexParent}}}","id":"{{{child}}}","forked_from_id":"{{{CodexParent}}}","parent_thread_id":"{{{CodexParent}}}","cwd":"C:\\demo\\proj","thread_source":"subagent"}}""" + "\n";

    private static string CodexLine(string type, string payload) =>
        $$$"""{"timestamp":"2026-09-23T10:05:00.500Z","type":"{{{type}}}","payload":{{{payload}}}}""" + "\n";

    [Fact]
    public void AppTokenSource_reports_a_forked_Codex_child_with_the_tokens_of_its_own_turns_only()
    {
        using var dir = new TempDir();
        const string child = "01a0cb74-99f6-7b04-9d03-2b7d19c7bad4";
        // A fork copies the parent's history, token_count included: the parent's 1M tokens are not the child's.
        var rollout = dir.File($".codex/sessions/2026/09/23/rollout-2026-09-23T10-05-00-{child}.jsonl",
            ForkedChildMeta(child) +
            CodexLine("turn_context", """{"model":"gpt-6-sol"}""") +
            CodexTokenCount(1_000_000, 900_000, 5_000) +
            CodexLine("turn_context", """{"model":"gpt-6-sol"}"""));
        var session = new SessionState(AgentKind.Codex, CodexParent, "demo", null, SessionPhase.Working, null, T0, T0,
            Subagents: [new(child, "worker", SubagentPhase.Running, T0, null, null, TokenUsage.Zero)]);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(T0));

        Assert.Null(source.SubagentTokens(session));
        Assert.Null(source.SubagentLedgers(session));

        File.AppendAllText(rollout, CodexLine("inter_agent_communication_metadata", """{"trigger_turn":true}""") +
                                    CodexTokenCount(1_000_300, 900_200, 5_040));

        var tokens = source.SubagentTokens(session)![child];
        Assert.Equal(new TokenUsage(100, 40, 200, 0), tokens);
        Assert.Equal(tokens, source.SubagentLedgers(session)![child].ToTokenUsage());
    }

    [Fact]
    public void AppTokenSource_reports_a_Codex_thread_with_the_usage_before_and_after_a_restart_of_its_total()
    {
        using var dir = new TempDir();
        // Codex restarts the cumulative total when it wakes a thread for a new task: the newest total (300) is only
        // the last task, the ledger — and so the row — keeps both.
        dir.File($".codex/sessions/2026/09/23/rollout-2026-09-23T10-00-00-{CodexParent}.jsonl",
            CodexLine("turn_context", """{"model":"gpt-6-luna"}""") +
            CodexTokenCount(1_000, 0, 10) +
            CodexLine("event_msg", """{"type":"task_started","turn_id":"t2"}""") +
            CodexTokenCount(300, 0, 3));
        var session = new SessionState(AgentKind.Codex, CodexParent, "demo", null, SessionPhase.Idle, null, T0, T0);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(T0));

        var tokens = source.SessionTokens(session);

        Assert.Equal(new TokenUsage(1_300, 13, 0, 0), tokens);
        Assert.Equal(tokens, source.SessionLedger(session)!.ToTokenUsage());
    }

    [Fact]
    public void AppTokenSource_releases_the_Codex_child_the_tracker_no_longer_lists()
    {
        using var dir = new TempDir();
        const string kept = "01a0cb74-99f6-7b04-9d03-2b7d19c7bad4";
        const string dropped = "01a0cb74-aa07-7c15-8e14-3c8e2ad8cbe5";
        foreach (var child in (string[])[kept, dropped])
            dir.File($".codex/sessions/2026/09/23/rollout-2026-09-23T10-05-00-{child}.jsonl",
                CodexLine("turn_context", """{"model":"gpt-6-sol"}""") + CodexTokenCount(50, 10, 5));
        SubagentState Done(string id) => new(id, "worker", SubagentPhase.Done, T0, T0, null, TokenUsage.Zero);
        var both = new SessionState(AgentKind.Codex, CodexParent, "demo", null, SessionPhase.Working, null, T0, T0,
            Subagents: [Done(kept), Done(dropped)]);
        var source = new AppTokenSource(new AppPaths(dir.Path, dir.Sub("local")), new FakeClock(T0));
        Assert.Equal(2, source.SubagentTokens(both)!.Count);

        // A finished child is read again from the rollout it was found in, and a vanished rollout keeps the ledger
        // read so far: only a released state lets a later read find that the rollout is gone.
        File.Delete(Directory.EnumerateFiles(dir.Path, $"*{dropped}.jsonl", SearchOption.AllDirectories).Single());
        Assert.Equal(2, source.SubagentTokens(both)!.Count);

        // The tracker trims the oldest finished subagents: the next read releases what it kept for the dropped one.
        source.SubagentTokens(both with { Subagents = [Done(kept)] });

        Assert.Equal([kept], source.SubagentTokens(both)!.Keys);
    }
}
