using System.Text.Json;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ClaudeTranscriptLedgerTests
{
    /// <summary>An assistant line with the usage fields Claude Code writes today (model, speed, geo, cache split, web search).</summary>
    private static string Line(string? requestId, string? model, long input, long output, long cacheRead, long cacheWrite,
        long? write5m = null, long? write1h = null, string? speed = null, string? geo = null, long? webSearches = null, string? messageId = null)
    {
        var usage = new Dictionary<string, object?>
        {
            ["input_tokens"] = input,
            ["output_tokens"] = output,
            ["cache_read_input_tokens"] = cacheRead,
            ["cache_creation_input_tokens"] = cacheWrite
        };
        if (write5m is not null || write1h is not null)
            usage["cache_creation"] = new Dictionary<string, object?> { ["ephemeral_5m_input_tokens"] = write5m ?? 0, ["ephemeral_1h_input_tokens"] = write1h ?? 0 };
        if (speed is not null) usage["speed"] = speed;
        if (geo is not null) usage["inference_geo"] = geo;
        if (webSearches is not null) usage["server_tool_use"] = new Dictionary<string, object?> { ["web_search_requests"] = webSearches, ["web_fetch_requests"] = 0 };

        var message = new Dictionary<string, object?> { ["role"] = "assistant", ["usage"] = usage };
        if (model is not null) message["model"] = model;
        if (messageId is not null) message["id"] = messageId;
        var line = new Dictionary<string, object?> { ["type"] = "assistant", ["message"] = message };
        if (requestId is not null) line["requestId"] = requestId;
        return JsonSerializer.Serialize(line);
    }

    private static string Join(params string[] lines) => string.Join("\n", lines) + "\n";

    private static LedgerTokens Tokens(UsageLedger ledger, string model) => ledger.Entries.Single(e => e.Key.Model == model).Tokens;

    [Fact]
    public void Ledger_splits_the_usage_by_model_and_always_sums_to_the_total()
    {
        using var dir = new TempDir();
        var file = dir.File("s.jsonl", Join(
            Line("r1", "claude-opus-5-5", 10, 20, 30, 40),
            Line("r2", "claude-opus-5-5", 1, 2, 3, 4),
            Line("r3", "claude-fable-5-1", 100, 200, 300, 400),
            Line(null, null, 5, 6, 7, 8)));
        var counter = new ClaudeTranscriptTokenCounter();

        var total = counter.Read(file);
        var ledger = counter.LedgerOf(file);

        Assert.Equal(total, ledger.ToTokenUsage());
        Assert.Equal(new LedgerTokens(11, 22, 33, 44, 0), Tokens(ledger, "claude-opus-5-5"));
        Assert.Equal(new LedgerTokens(100, 200, 300, 400, 0), Tokens(ledger, "claude-fable-5-1"));
        Assert.Equal(new LedgerTokens(5, 6, 7, 8, 0), Tokens(ledger, "")); // no model: priced as unknown later
    }

    [Fact]
    public void A_streamed_request_adds_only_its_output_growth_under_the_key_of_its_first_line()
    {
        using var dir = new TempDir();
        var file = dir.File("s.jsonl", Join(
            Line("r1", "claude-opus-5-5", 10, 5, 30, 40, messageId: "m1"),
            Line("r1", "claude-opus-5-5", 10, 50, 30, 40, messageId: "m1"),
            Line("r1", "claude-other", 10, 60, 30, 40, messageId: "m1")));
        var counter = new ClaudeTranscriptTokenCounter();

        counter.Read(file);
        var ledger = counter.LedgerOf(file);

        var entry = Assert.Single(ledger.Entries);
        Assert.Equal("claude-opus-5-5", entry.Key.Model);
        Assert.Equal(new LedgerTokens(10, 60, 30, 40, 0), entry.Tokens);
    }

    [Fact]
    public void Cache_creation_is_split_between_the_5_minute_and_the_1_hour_price()
    {
        using var dir = new TempDir();
        var file = dir.File("s.jsonl", Join(
            Line("r1", "m", 0, 0, 0, 1000, write5m: 100, write1h: 900),
            Line("r2", "m", 0, 0, 0, 70)));
        var counter = new ClaudeTranscriptTokenCounter();

        counter.Read(file);

        Assert.Equal(new LedgerTokens(0, 0, 0, 170, 900), Tokens(counter.LedgerOf(file), "m"));
    }

    [Fact]
    public void Fast_mode_and_a_restricted_geography_go_into_the_key()
    {
        using var dir = new TempDir();
        var file = dir.File("s.jsonl", Join(
            Line("r1", "m", 1, 1, 0, 0, speed: "fast", geo: "US"),
            Line("r2", "m", 1, 1, 0, 0, speed: "standard", geo: "not_available"),
            Line("r3", "m", 1, 1, 0, 0, geo: "global")));
        var counter = new ClaudeTranscriptTokenCounter();

        counter.Read(file);
        var keys = counter.LedgerOf(file).Entries.Select(e => e.Key).ToList();

        Assert.Contains(new UsageKey("m", PriceTier.Fast, "us", 0), keys);
        Assert.Contains(new UsageKey("m", PriceTier.Standard, null, 0), keys);
        Assert.Equal(2, keys.Count);
    }

    [Fact]
    public void The_context_band_comes_from_the_whole_prompt_of_the_request()
    {
        using var dir = new TempDir();
        var file = dir.File("s.jsonl", Join(
            Line("r1", "m", 1, 1, 280_000, 0),
            Line("r2", "m", 1, 1, 150_000, 60_000),
            Line("r3", "m", 1, 1, 1_000, 0)));
        var counter = new ClaudeTranscriptTokenCounter();

        counter.Read(file);
        var bands = counter.LedgerOf(file).Entries.Select(e => e.Key.ContextBand).OrderBy(b => b).ToList();

        Assert.Equal([0L, 200_000L, 272_000L], bands);
    }

    [Fact]
    public void Web_searches_count_once_per_request()
    {
        using var dir = new TempDir();
        var file = dir.File("s.jsonl", Join(
            Line("r1", "m", 1, 1, 0, 0, webSearches: 2),
            Line("r1", "m", 1, 2, 0, 0, webSearches: 2),
            Line("r2", "m", 1, 1, 0, 0, webSearches: 1)));
        var counter = new ClaudeTranscriptTokenCounter();

        counter.Read(file);

        Assert.Equal(3, Tokens(counter.LedgerOf(file), "m").WebSearches);
    }

    [Fact]
    public void Two_reads_add_only_the_appended_lines_and_a_truncated_file_starts_over()
    {
        using var dir = new TempDir();
        var file = dir.File("s.jsonl", Join(Line("r1", "m", 1, 1, 0, 0)));
        var counter = new ClaudeTranscriptTokenCounter();
        counter.Read(file);

        File.AppendAllText(file, Join(Line("r2", "m", 2, 2, 0, 0)));
        counter.Read(file);
        Assert.Equal(new LedgerTokens(3, 3, 0, 0, 0), Tokens(counter.LedgerOf(file), "m"));

        File.WriteAllText(file, Join(Line("r9", "n", 7, 7, 0, 0)));
        counter.Read(file);
        var entry = Assert.Single(counter.LedgerOf(file).Entries);
        Assert.Equal("n", entry.Key.Model);
    }

    [Fact]
    public void An_unknown_or_forgotten_transcript_has_an_empty_ledger()
    {
        using var dir = new TempDir();
        var file = dir.File("s.jsonl", Join(Line("r1", "m", 1, 1, 0, 0)));
        var counter = new ClaudeTranscriptTokenCounter();

        Assert.True(counter.LedgerOf(file).IsEmpty);
        counter.Read(file);
        counter.Forget(file);
        Assert.True(counter.LedgerOf(file).IsEmpty);
    }
}
