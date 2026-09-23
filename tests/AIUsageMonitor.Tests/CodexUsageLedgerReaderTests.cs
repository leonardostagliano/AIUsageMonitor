using System.Text.Json;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CodexUsageLedgerReaderTests
{
    private const string Ts = "2026-09-23T10:00:00.000Z";

    // Built with JsonSerializer rather than interpolated raw strings: the nested objects end in runs of closing
    // braces that an interpolation hole next to them would turn into a compile error.
    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static string TurnContext(string model) =>
        Json(new { timestamp = Ts, type = "turn_context", payload = new { turn_id = "t", cwd = "C:\\demo", model } });

    private static string ThreadSettings(string model, string tier) =>
        Json(new { timestamp = Ts, type = "event_msg", payload = new { type = "thread_settings_applied", thread_id = "x",
            thread_settings = new { model, model_provider_id = "openai", service_tier = tier } } });

    private static Dictionary<string, long> Usage(long input, long cached, long cacheWrite, long output) => new()
    {
        ["input_tokens"] = input,
        ["cached_input_tokens"] = cached,
        ["cache_write_input_tokens"] = cacheWrite,
        ["output_tokens"] = output,
        ["reasoning_output_tokens"] = 0,
        ["total_tokens"] = input + output
    };

    private static string TokenCount(long input, long cached, long output, long? lastInput = null, long cacheWrite = 0) =>
        TokenCountOf(Usage(input, cached, cacheWrite, output), lastInput is null ? null : Usage(lastInput.Value, 0, 0, 0));

    /// <summary>The first token_count after Codex restarts a woken subagent's total: the total is that request alone.</summary>
    private static string RestartedTokenCount(long input, long cached, long output) =>
        TokenCountOf(Usage(input, cached, 0, output), Usage(input, cached, 0, output));

    private static string TokenCountOf(Dictionary<string, long> total, Dictionary<string, long>? last)
    {
        var info = new Dictionary<string, object?>
        {
            ["total_token_usage"] = total,
            ["model_context_window"] = 258400
        };
        if (last is not null) info["last_token_usage"] = last;
        return Json(new { timestamp = Ts, type = "event_msg", payload = new { type = "token_count", info, rate_limits = (object?)null } });
    }

    private static readonly string NullInfo =
        Json(new { timestamp = Ts, type = "event_msg", payload = new { type = "token_count", info = (object?)null, rate_limits = (object?)null } });

    private static string Event(string type) =>
        Json(new { timestamp = Ts, type = "event_msg", payload = new { type, turn_id = "t2" } });

    private static string ForkedSessionMeta() =>
        Json(new { timestamp = Ts, type = "session_meta", payload = new { id = "child", forked_from_id = "parent", cwd = "C:\\demo" } });

    /// <summary>session_meta of a subagent spawned with <c>spawn_agent {fork_turns:"all"}</c>, as Codex 0.144 writes it.</summary>
    private static string ForkedSubagentMeta() =>
        Json(new { timestamp = Ts, type = "session_meta", payload = new
        {
            session_id = "parent", id = "child", forked_from_id = "parent", parent_thread_id = "parent", cwd = "C:\\demo",
            source = new { subagent = new { thread_spawn = new { parent_thread_id = "parent", depth = 1, agent_path = "/root/worker" } } },
            thread_source = "subagent"
        } });

    /// <summary>The line that opens the turn a parent gives its subagent.</summary>
    private static string InterAgentTurn() =>
        Json(new { timestamp = Ts, type = "inter_agent_communication_metadata", payload = new { trigger_turn = true } });

    private static string Compacted() =>
        Json(new { timestamp = Ts, type = "compacted", payload = new { message = "", replacement_history = Array.Empty<object>() } });

    // Written by hand rather than with JsonSerializer, whose default encoder escapes every non-ASCII character: these
    // lines must hold the raw multi-byte UTF-8 that Codex writes (Italian prompts, emoji, accented paths).
    private static string RawTurnContext(string model, string jsonCwd) =>
        "{\"timestamp\":\"" + Ts + "\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"t\",\"cwd\":\"" + jsonCwd
        + "\",\"model\":\"" + model + "\"}}";

    private static string RawUserMessage(string text) =>
        "{\"timestamp\":\"" + Ts + "\",\"type\":\"response_item\",\"payload\":{\"type\":\"message\",\"role\":\"user\","
        + "\"content\":[{\"type\":\"input_text\",\"text\":\"" + text + "\"}]}}";

    private static string Join(params string[] lines) => string.Join("\n", lines) + "\n";

    private static LedgerTokens Tokens(UsageLedger ledger, string model) => ledger.Entries.Single(e => e.Key.Model == model).Tokens;

    [Fact]
    public void Growth_is_attributed_to_the_model_in_force_and_sums_to_the_cumulative_total()
    {
        using var dir = new TempDir();
        const string thread = "01a0cb74-88e5-7a93-8cf2-1a6c08b6a9c3";
        var file = dir.File($"sessions/2026/09/23/rollout-2026-09-23T10-00-00-{thread}.jsonl", Join(
            TurnContext("gpt-6-luna"),
            TokenCount(1000, 200, 50, lastInput: 1000),
            TokenCount(1000, 200, 50, lastInput: 1000), // repeated event: adds nothing
            NullInfo,
            TurnContext("gpt-6-sol"),
            TokenCount(3000, 700, 90, lastInput: 2000)));
        var reader = new CodexUsageLedgerReader();

        var ledger = reader.Read(file);

        Assert.Equal(new LedgerTokens(800, 50, 200, 0, 0), Tokens(ledger, "gpt-6-luna"));
        Assert.Equal(new LedgerTokens(1500, 40, 500, 0, 0), Tokens(ledger, "gpt-6-sol"));
        var counter = new CodexTokenCounter(Path.Combine(dir.Path, "sessions"), new FakeClock(DateTimeOffset.UtcNow));
        Assert.Equal(counter.ReadThread(thread), ledger.ToTokenUsage());
    }

    [Fact]
    public void A_resumed_thread_counts_its_inherited_first_total_whole_with_the_band_of_its_last_request()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-r.jsonl", Join(
            TurnContext("gpt-6-astra"),
            TokenCount(5_689_154, 5_203_200, 4_986, lastInput: 90_000)));
        var reader = new CodexUsageLedgerReader();

        var entry = Assert.Single(reader.Read(file).Entries);

        Assert.Equal(new UsageKey("gpt-6-astra", PriceTier.Standard, null, 0), entry.Key);
        Assert.Equal(new LedgerTokens(485_954, 4_986, 5_203_200, 0, 0), entry.Tokens);
    }

    [Fact]
    public void A_prompt_above_272k_gets_the_272k_band()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-b.jsonl", Join(TurnContext("gpt-6-astra"), TokenCount(300_000, 0, 10, lastInput: 300_000)));

        Assert.Equal(272_000, Assert.Single(new CodexUsageLedgerReader().Read(file).Entries).Key.ContextBand);
    }

    [Fact]
    public void Without_last_token_usage_the_band_comes_from_the_input_growth()
    {
        using var dir = new TempDir();
        // The second event grows by 50k: its band is 0, although the cumulative input (300k) is above 272k.
        var file = dir.File("rollout-g.jsonl", Join(TurnContext("m"), TokenCount(250_000, 0, 10), TokenCount(300_000, 0, 20)));

        var entries = new CodexUsageLedgerReader().Read(file).Entries;

        Assert.Equal(
            [new LedgerEntry(new UsageKey("m", PriceTier.Standard, null, 0), new LedgerTokens(50_000, 10, 0, 0, 0)),
             new LedgerEntry(new UsageKey("m", PriceTier.Standard, null, 200_000), new LedgerTokens(250_000, 10, 0, 0, 0))],
            entries);
    }

    [Fact]
    public void Thread_settings_set_the_model_and_the_priority_tier()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-p.jsonl", Join(
            ThreadSettings("gpt-6-sol", "priority"),
            TokenCount(100, 0, 10),
            ThreadSettings("gpt-6-sol", "flex"),
            TokenCount(300, 0, 30),
            ThreadSettings("gpt-6-sol", "default"),
            TokenCount(600, 0, 60)));

        var keys = new CodexUsageLedgerReader().Read(file).Entries.Select(e => e.Key.Tier).ToList();

        Assert.Equal([PriceTier.Standard, PriceTier.Priority, PriceTier.Flex], keys.OrderBy(t => t).ToList());
    }

    [Fact]
    public void The_fast_service_tier_is_priced_as_priority()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-f.jsonl", Join(ThreadSettings("m", "fast"), TokenCount(100, 0, 10)));

        Assert.Equal(PriceTier.Priority, Assert.Single(new CodexUsageLedgerReader().Read(file).Entries).Key.Tier);
    }

    [Fact]
    public void The_model_can_come_from_session_meta()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-sm.jsonl", Join(
            Json(new { timestamp = Ts, type = "session_meta", payload = new { id = "x", model = "m1" } }),
            TokenCount(100, 0, 10)));

        Assert.Equal("m1", Assert.Single(new CodexUsageLedgerReader().Read(file).Entries).Key.Model);
    }

    [Fact]
    public void The_model_can_come_from_a_legacy_turn_context_event()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-lt.jsonl", Join(
            Json(new { timestamp = Ts, type = "event_msg", payload = new { type = "turn_context", model = "m2" } }),
            TokenCount(100, 0, 10)));

        Assert.Equal("m2", Assert.Single(new CodexUsageLedgerReader().Read(file).Entries).Key.Model);
    }

    [Fact]
    public void Growth_before_the_first_model_moves_under_the_first_model_the_rollout_names()
    {
        // Shaped like a conversation the user forked (no parent thread): its inherited total arrives before its first
        // turn_context and, with no turn marker to end the copy on, is counted whole.
        using var dir = new TempDir();
        var file = dir.File("rollout-k.jsonl", Join(
            ForkedSessionMeta(),
            Compacted(),
            TokenCount(34_723_453, 34_250_496, 48_154, lastInput: 0),
            Event("task_started")));
        var reader = new CodexUsageLedgerReader();

        // Until a model is named, the growth is kept under an empty model rather than dropped.
        var pending = Assert.Single(reader.Read(file).Entries);
        Assert.Equal(new UsageKey("", PriceTier.Standard, null, 0), pending.Key);

        File.AppendAllText(file, Join(TurnContext("gpt-6-sol"), TokenCount(34_800_000, 34_300_000, 49_000, lastInput: 76_547)));
        var entry = Assert.Single(reader.Read(file).Entries);

        Assert.Equal(new UsageKey("gpt-6-sol", PriceTier.Standard, null, 0), entry.Key);
        Assert.Equal(new LedgerTokens(34_800_000 - 34_300_000, 49_000, 34_300_000, 0, 0), entry.Tokens);
    }

    [Fact]
    public void A_forked_subagent_counts_only_its_own_growth_over_the_totals_copied_from_its_parent()
    {
        // The opening of a real forked rollout (child 019f587b of parent 019f582f, Codex 0.144): the copied history
        // carries the parent's totals, the child's own turn starts at inter_agent_communication_metadata and its
        // first total continues from the last copied one.
        using var dir = new TempDir();
        var file = dir.File("rollout-f.jsonl", Join(
            ForkedSubagentMeta(),
            Compacted(),
            TokenCount(34_723_453, 34_250_496, 48_154, lastInput: 0),
            Event("task_started"),
            TurnContext("gpt-5.6-sol"),
            // Quoting the marker in a message is not the marker.
            RawUserMessage("inter_agent_communication_metadata"),
            TokenCount(34_745_812, 34_260_480, 48_409, lastInput: 22_359),
            TokenCount(34_773_421, 34_281_728, 48_481, lastInput: 27_609),
            Event("task_complete"),
            Event("task_started"),
            TurnContext("gpt-5.6-sol"),
            InterAgentTurn()));
        var reader = new CodexUsageLedgerReader();

        Assert.True(reader.Read(file).IsEmpty);

        File.AppendAllText(file, Join(
            TokenCount(34_795_927, 34_302_976, 48_649, lastInput: 22_506),
            TokenCount(34_821_620, 34_325_248, 48_769, lastInput: 25_693)));
        var entry = Assert.Single(reader.Read(file).Entries);

        Assert.Equal(new UsageKey("gpt-5.6-sol", PriceTier.Standard, null, 0), entry.Key);
        // 22 506 + 25 693 input, 21 248 + 22 272 of it cached, 168 + 120 output: the two requests of the child itself.
        Assert.Equal(new LedgerTokens(48_199 - 43_520, 288, 43_520, 0, 0), entry.Tokens);
        Assert.Equal(entry, Assert.Single(new CodexUsageLedgerReader().Read(file).Entries));
        Assert.Equal(entry, Assert.Single(reader.LedgerOf(file).Entries));
    }

    [Fact]
    public void LedgerOf_reads_no_file()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-l.jsonl", Join(TurnContext("m"), TokenCount(100, 0, 10)));
        var reader = new CodexUsageLedgerReader();

        Assert.True(reader.LedgerOf(file).IsEmpty);
        var ledger = reader.Read(file);
        File.AppendAllText(file, Join(TokenCount(250, 0, 25)));

        Assert.Equal(ledger, reader.LedgerOf(file));
    }

    [Fact]
    public void Cache_writes_are_priced_as_5_minute_writes()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-w.jsonl", Join(TurnContext("m"), TokenCount(100, 0, 1, cacheWrite: 40)));

        Assert.Equal(new LedgerTokens(100, 1, 0, 40, 0), Tokens(new CodexUsageLedgerReader().Read(file), "m"));
    }

    [Fact]
    public void Reads_are_incremental_and_a_total_that_goes_down_is_a_restart_counted_whole()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-i.jsonl", Join(TurnContext("m"), TokenCount(100, 0, 10)));
        var reader = new CodexUsageLedgerReader();
        reader.Read(file);

        File.AppendAllText(file, Join(TokenCount(250, 0, 25)));
        Assert.Equal(new LedgerTokens(250, 25, 0, 0, 0), Tokens(reader.Read(file), "m"));

        File.AppendAllText(file, Join(TokenCount(50, 0, 5), TokenCount(80, 0, 9)));
        Assert.Equal(new LedgerTokens(100 + 150 + 50 + 30, 10 + 15 + 5 + 4, 0, 0, 0), Tokens(reader.Read(file), "m"));
    }

    [Fact]
    public void A_subagent_woken_for_a_new_task_keeps_the_usage_before_and_after_its_total_restarts()
    {
        // Shaped like a real rollout: Codex restarts the cumulative total from zero when it wakes a subagent thread
        // for a new task, and the first total after task_started is that request's last_token_usage.
        using var dir = new TempDir();
        var file = dir.File("rollout-s.jsonl", Join(
            TurnContext("gpt-6-sol"),
            TokenCount(10_949_533, 10_664_704, 60_030, lastInput: 171_111),
            Event("task_complete"),
            Event("task_started"),
            TurnContext("gpt-6-sol"),
            RestartedTokenCount(171_531, 170_752, 350),
            TokenCount(1_400_000, 1_300_000, 5_000, lastInput: 180_000)));

        var entry = Assert.Single(new CodexUsageLedgerReader().Read(file).Entries);

        Assert.Equal(new LedgerTokens(
            Input: (10_949_533 - 10_664_704) + (1_400_000 - 1_300_000),
            Output: 60_030 + 5_000,
            CacheRead: 10_664_704 + 1_300_000,
            CacheWrite5m: 0,
            CacheWrite1h: 0), entry.Tokens);
    }

    [Fact]
    public void A_half_written_last_line_is_left_for_the_next_read_and_counted_once()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-h.jsonl", Join(TurnContext("m")) + TokenCount(100, 0, 10));
        var reader = new CodexUsageLedgerReader();

        Assert.True(reader.Read(file).IsEmpty);

        File.AppendAllText(file, "\n");
        Assert.Equal(new LedgerTokens(100, 10, 0, 0, 0), Tokens(reader.Read(file), "m"));
        Assert.Equal(new LedgerTokens(100, 10, 0, 0, 0), Tokens(reader.Read(file), "m"));
    }

    [Fact]
    public void Byte_offsets_stay_exact_after_multi_byte_text()
    {
        using var dir = new TempDir();
        // 2 400 more bytes than UTF-16 characters: an offset kept in characters would land before the two token_count
        // lines below and read them again, counting the older total as a restart.
        var prompt = string.Concat(Enumerable.Repeat("perch\u00E9 \u00E8 gi\u00E0 cos\u00EC \U0001F600 ", 400));
        var file = dir.File("rollout-u.jsonl", Join(
            RawTurnContext("m", "C:\\\\Users\\\\citt\u00E0"),
            RawUserMessage(prompt),
            TokenCount(100, 0, 10),
            TokenCount(200, 0, 20)));
        Assert.True(new FileInfo(file).Length >= File.ReadAllText(file).Length + 2_400);
        var reader = new CodexUsageLedgerReader();
        reader.Read(file);

        File.AppendAllText(file, Join(TokenCount(300, 0, 30)));
        Assert.Equal(new LedgerTokens(300, 30, 0, 0, 0), Tokens(reader.Read(file), "m"));

        File.AppendAllText(file, Join(RawUserMessage("ancora un po' di testo: \u00E8 cos\u00EC"), TokenCount(450, 0, 45)));
        Assert.Equal(new LedgerTokens(450, 45, 0, 0, 0), Tokens(reader.Read(file), "m"));
    }

    [Fact]
    public void Crlf_line_endings_give_the_same_ledger_read_whole_or_incrementally()
    {
        using var dir = new TempDir();
        string[] head = [TurnContext("m"), TokenCount(100, 20, 10, lastInput: 100)];
        string[] tail = [TurnContext("n"), TokenCount(300, 50, 30, lastInput: 200)];
        static string Crlf(string[] lines) => string.Join("\r\n", lines) + "\r\n";
        var expected = new CodexUsageLedgerReader().Read(dir.File("rollout-lf.jsonl", Join([.. head, .. tail])));
        Assert.Equal(2, expected.Entries.Count);

        var whole = new CodexUsageLedgerReader().Read(dir.File("rollout-crlf.jsonl", Crlf([.. head, .. tail])));

        var file = dir.File("rollout-crlf-i.jsonl", Crlf(head));
        var reader = new CodexUsageLedgerReader();
        reader.Read(file);
        File.AppendAllText(file, Crlf(tail));
        var incremental = reader.Read(file);

        Assert.Equal(expected, whole);
        Assert.Equal(expected, incremental);
    }

    [Fact]
    public void A_truncated_rollout_starts_over_and_a_forgotten_one_is_read_again()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-t.jsonl", Join(TurnContext("m"), TokenCount(100, 0, 10), TokenCount(200, 0, 20)));
        var reader = new CodexUsageLedgerReader();
        reader.Read(file);

        File.WriteAllText(file, Join(TurnContext("n"), TokenCount(7, 0, 1)));
        var entry = Assert.Single(reader.Read(file).Entries);
        Assert.Equal("n", entry.Key.Model);

        reader.Forget(file);
        Assert.Equal(new LedgerTokens(7, 1, 0, 0, 0), Assert.Single(reader.Read(file).Entries).Tokens);
    }

    [Fact]
    public void A_missing_file_is_an_empty_ledger()
    {
        using var dir = new TempDir();
        Assert.True(new CodexUsageLedgerReader().Read(Path.Combine(dir.Path, "nope.jsonl")).IsEmpty);
    }

    [Fact]
    public void TryResolveRollout_finds_the_rollout_of_a_thread_by_file_name()
    {
        using var dir = new TempDir();
        const string thread = "01a0cb39-2f0a-7a63-b12f-dc3964792c14";
        var file = dir.File($"sessions/2026/09/23/rollout-2026-09-23T00-25-18-{thread}.jsonl", Join(TurnContext("m")));
        var counter = new CodexTokenCounter(Path.Combine(dir.Path, "sessions"), new FakeClock(DateTimeOffset.UtcNow));

        Assert.True(counter.TryResolveRollout(thread, out var path));
        // TempDir.File keeps the forward slashes of its relative path; the enumeration returns backslashes.
        Assert.Equal(Path.GetFullPath(file), path);
        Assert.True(counter.TryResolveRollout("0000-none", out var none));
        Assert.Null(none);
    }
}
