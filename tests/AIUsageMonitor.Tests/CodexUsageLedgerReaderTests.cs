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

    private static string TokenCount(long input, long cached, long output, long? lastInput = null, long cacheWrite = 0)
    {
        var info = new Dictionary<string, object?>
        {
            ["total_token_usage"] = Usage(input, cached, cacheWrite, output),
            ["model_context_window"] = 258400
        };
        if (lastInput is not null) info["last_token_usage"] = Usage(lastInput.Value, 0, 0, 0);
        return Json(new { timestamp = Ts, type = "event_msg", payload = new { type = "token_count", info, rate_limits = (object?)null } });
    }

    private static readonly string NullInfo =
        Json(new { timestamp = Ts, type = "event_msg", payload = new { type = "token_count", info = (object?)null, rate_limits = (object?)null } });

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
    public void Cache_writes_are_priced_as_5_minute_writes()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-w.jsonl", Join(TurnContext("m"), TokenCount(100, 0, 1, cacheWrite: 40)));

        Assert.Equal(new LedgerTokens(100, 1, 0, 40, 0), Tokens(new CodexUsageLedgerReader().Read(file), "m"));
    }

    [Fact]
    public void Reads_are_incremental_and_a_total_that_goes_down_adds_nothing()
    {
        using var dir = new TempDir();
        var file = dir.File("rollout-i.jsonl", Join(TurnContext("m"), TokenCount(100, 0, 10)));
        var reader = new CodexUsageLedgerReader();
        reader.Read(file);

        File.AppendAllText(file, Join(TokenCount(250, 0, 25)));
        Assert.Equal(new LedgerTokens(250, 25, 0, 0, 0), Tokens(reader.Read(file), "m"));

        File.AppendAllText(file, Join(TokenCount(50, 0, 5), TokenCount(80, 0, 9)));
        Assert.Equal(new LedgerTokens(280, 29, 0, 0, 0), Tokens(reader.Read(file), "m"));
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
