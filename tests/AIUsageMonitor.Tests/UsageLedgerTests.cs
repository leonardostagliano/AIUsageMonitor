using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Tests;

public class UsageLedgerTests
{
    private static readonly UsageKey Opus = new("claude-opus-5-5", PriceTier.Standard, null, 0);
    private static readonly UsageKey Fable = new("claude-fable-5-1", PriceTier.Standard, null, 0);

    [Fact]
    public void From_merges_duplicate_keys_drops_zero_entries_and_sorts_by_key()
    {
        var ledger = UsageLedger.From([
            KeyValuePair.Create(Opus, new LedgerTokens(1, 2, 3, 4, 5)),
            KeyValuePair.Create(Fable, LedgerTokens.Zero),
            KeyValuePair.Create(Opus, new LedgerTokens(10, 20, 30, 40, 50, 1)),
            KeyValuePair.Create(Fable, new LedgerTokens(0, 1, 0, 0, 0))
        ]);

        Assert.Equal(2, ledger.Entries.Count);
        Assert.Equal(Fable, ledger.Entries[0].Key); // "claude-fable…" < "claude-opus…"
        Assert.Equal(new LedgerTokens(11, 22, 33, 44, 55, 1), ledger.Entries[1].Tokens);
    }

    [Fact]
    public void Ledgers_with_the_same_entries_are_equal_and_hash_alike()
    {
        var a = UsageLedger.From([KeyValuePair.Create(Opus, new LedgerTokens(1, 2, 3, 4, 5))]);
        var b = UsageLedger.From([KeyValuePair.Create(Opus, new LedgerTokens(1, 2, 3, 4, 5))]);
        var c = UsageLedger.From([KeyValuePair.Create(Opus, new LedgerTokens(1, 2, 3, 4, 6))]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.Equal(UsageLedger.Empty, UsageLedger.From([]));
    }

    [Fact]
    public void Plus_merges_two_ledgers()
    {
        var a = UsageLedger.From([KeyValuePair.Create(Opus, new LedgerTokens(1, 0, 0, 0, 0))]);
        var b = UsageLedger.From([KeyValuePair.Create(Opus, new LedgerTokens(2, 0, 0, 0, 0)), KeyValuePair.Create(Fable, new LedgerTokens(0, 5, 0, 0, 0))]);

        var sum = a + b;

        Assert.Equal(new LedgerTokens(3, 0, 0, 0, 0), sum.Entries.Single(e => e.Key == Opus).Tokens);
        Assert.Equal(new LedgerTokens(0, 5, 0, 0, 0), sum.Entries.Single(e => e.Key == Fable).Tokens);
        Assert.Same(a, a + UsageLedger.Empty);
    }

    [Fact]
    public void ToTokenUsage_puts_both_cache_write_durations_in_CacheWrite()
    {
        var ledger = UsageLedger.From([
            KeyValuePair.Create(Opus, new LedgerTokens(1, 2, 3, 4, 5, 9)),
            KeyValuePair.Create(Fable, new LedgerTokens(10, 20, 30, 40, 50))
        ]);

        Assert.Equal(new TokenUsage(11, 22, 33, 99), ledger.ToTokenUsage());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(200_000, 0)]
    [InlineData(200_001, 200_000)]
    [InlineData(272_000, 200_000)]
    [InlineData(272_001, 272_000)]
    [InlineData(1_000_000, 272_000)]
    public void BandFor_is_the_highest_known_threshold_the_prompt_exceeds(long prompt, long band) =>
        Assert.Equal(band, PricingThresholds.BandFor(prompt));

    [Fact]
    public void Builder_snapshot_is_cached_until_the_next_add_and_cleared_by_clear()
    {
        var builder = new UsageLedgerBuilder();
        Assert.Same(UsageLedger.Empty, builder.ToLedger());

        builder.Add(Opus, new LedgerTokens(1, 1, 0, 0, 0));
        var first = builder.ToLedger();
        Assert.Same(first, builder.ToLedger());

        builder.Add(Opus, LedgerTokens.Zero); // zero adds nothing and keeps the snapshot
        Assert.Same(first, builder.ToLedger());

        builder.Add(Opus, new LedgerTokens(1, 0, 0, 0, 0));
        Assert.Equal(new LedgerTokens(2, 1, 0, 0, 0), builder.ToLedger().Entries.Single().Tokens);

        builder.Clear();
        Assert.True(builder.ToLedger().IsEmpty);
    }
}
