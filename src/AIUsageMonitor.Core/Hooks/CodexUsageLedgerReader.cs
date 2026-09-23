using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Usage of a Codex rollout split by model and price variant, read forward and incrementally (like
/// <see cref="ClaudeTranscriptTokenCounter"/>): every call parses only the bytes appended since the previous one.
/// </summary>
/// <remarks>
/// <see cref="CodexTokenCounter"/> reads only the newest cumulative <c>total_token_usage</c>, which is enough for the
/// totals but not for the cost: a thread can change model mid-way (4 of 60 local rollouts did on 2026-09-23). Here
/// every <c>token_count</c> contributes its growth over the previous total of the same rollout, under the model in
/// force at that point. Growth rather than <c>last_token_usage</c>: repeated events add nothing, and a resumed thread,
/// whose first total is inherited, is counted whole. The size of the request's own prompt
/// (<c>last_token_usage.input_tokens</c>) decides only the long-context band.
/// <para>
/// The cumulative total is not monotonic: Codex restarts it from zero when it wakes a subagent thread for a new task
/// (all 18 drops in 390 local rollouts on 2026-09-23 followed a <c>task_started</c>, and each new total equalled that
/// request's <c>last_token_usage</c>). A total that goes down is therefore a new count, taken whole like the first
/// one, so the ledger keeps everything the rollout consumed. <see cref="CodexTokenCounter"/>, which reads only the
/// newest total, would show just the part since the last restart for these threads: the app shows the ledger's total
/// instead (<see cref="UsageLedger.ToTokenUsage"/>), so tokens and cost describe the same usage.
/// </para>
/// <para>
/// A subagent spawned as a fork (<c>session_meta</c> with <c>forked_from_id</c> and a parent thread) starts its
/// rollout with a copy of the parent's history, <c>token_count</c> events included: those carry the PARENT's
/// cumulative totals, already billed to the parent, and the child's own first request continues from the last of
/// them. The copied totals are therefore only a baseline: they move the previous total without adding anything, up to
/// the child's first <c>inter_agent_communication_metadata</c>, the line that opens the turn its parent gave it.
/// Checked on 2026-09-24 against 188 forked rollouts written by Codex 0.144–0.155 (depth 1 to 3): every copied
/// <c>token_count</c> comes before that line and none of the child's own does; the copies held 1.94 billion input
/// tokens, 62% of those rollouts' totals.
/// </para>
/// </remarks>
/// <remarks>Not thread-safe: the pump does all its IO on one thread.</remarks>
public sealed class CodexUsageLedgerReader
{
    /// <summary>Record type of the line that opens a turn a subagent runs for another agent.</summary>
    private const string InterAgentMarker = "inter_agent_communication_metadata";

    private readonly Dictionary<string, RolloutState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The ledger of <paramref name="rolloutPath"/>, including everything appended since the last call. Never throws.</summary>
    public UsageLedger Read(string rolloutPath)
    {
        if (string.IsNullOrWhiteSpace(rolloutPath)) return UsageLedger.Empty;
        if (!_states.TryGetValue(rolloutPath, out var state)) _states[rolloutPath] = state = new RolloutState();

        try
        {
            if (!File.Exists(rolloutPath)) return state.ToLedger();

            // Codex (Rust std::fs) keeps the live rollout open with share ReadWrite|Delete: open it the same way.
            using var stream = new FileStream(rolloutPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Shorter than what we already consumed: the file was replaced or truncated, count everything again.
            if (stream.Length < state.Offset) state.Reset();
            stream.Seek(state.Offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            var text = reader.ReadToEnd();

            var consumed = 0;
            var lineStart = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                var line = text.AsSpan(lineStart, i - lineStart).TrimEnd('\r').ToString();
                lineStart = i + 1;
                consumed = i + 1;
                Apply(state, line);
            }

            // Only complete lines are consumed: a half-written one is read again on the next call.
            state.Offset += Encoding.UTF8.GetByteCount(text.AsSpan(0, consumed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException
                                      or OutOfMemoryException)
        {
            // Keep the last known ledger: the next sweep will try again.
        }

        return state.ToLedger();
    }

    /// <summary>The ledger of <paramref name="rolloutPath"/> as last read, without touching the file (empty when never read).</summary>
    public UsageLedger LedgerOf(string rolloutPath) =>
        !string.IsNullOrWhiteSpace(rolloutPath) && _states.TryGetValue(rolloutPath, out var state) ? state.ToLedger() : UsageLedger.Empty;

    /// <summary>Drops the state of a rollout whose session is gone.</summary>
    public void Forget(string rolloutPath) => _states.Remove(rolloutPath);

    private static void Apply(RolloutState state, string line)
    {
        // Cheap pre-filter: only the meta, settings, token_count and turn-opening lines matter.
        if (!line.Contains("token_count", StringComparison.Ordinal)
            && !line.Contains("\"model\"", StringComparison.Ordinal)
            && !line.Contains("service_tier", StringComparison.Ordinal)
            && !line.Contains("forked_from_id", StringComparison.Ordinal)
            && !line.Contains(InterAgentMarker, StringComparison.Ordinal)) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            var type = String(root, "type");
            // The first turn a forked child runs for its parent: the copied history is over.
            if (type == InterAgentMarker)
            {
                state.InheritedHistory = false;
                return;
            }
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
            var payloadType = String(payload, "type");

            if (type == "session_meta")
            {
                // Only as the rollout's opening line: a later one cannot turn counted usage back into a copy.
                if (state.LastTotal is null && IsForkedSubagent(root, payload)) state.InheritedHistory = true;
                ApplySettings(state, payload);
            }
            else if (type == "turn_context" || (type == "event_msg" && payloadType == "turn_context"))
            {
                ApplySettings(state, payload);
            }
            else if (type == "event_msg" && payloadType == "thread_settings_applied")
            {
                if (payload.TryGetProperty("thread_settings", out var settings) && settings.ValueKind == JsonValueKind.Object)
                    ApplySettings(state, settings);
            }
            else if (type == "event_msg" && payloadType == "token_count")
            {
                ApplyTokenCount(state, payload);
            }
        }
        catch (JsonException)
        {
            // Not JSON (or a half-written line that was rotated in): nothing to count.
        }
    }

    /// <summary>
    /// A subagent spawned as a fork of its parent: <c>forked_from_id</c> plus a parent thread. A conversation the user
    /// forked has no parent thread and no turn marker to end its copied history on, so it keeps counting it whole.
    /// </summary>
    private static bool IsForkedSubagent(JsonElement root, JsonElement payload) =>
        String(payload, "forked_from_id") is { Length: > 0 }
        && (String(payload, "parent_thread_id") ?? String(root, "parent_thread_id")) is { Length: > 0 };

    private static void ApplySettings(RolloutState state, JsonElement settings)
    {
        if (String(settings, "model") is { Length: > 0 } model) state.SetModel(model);
        if (String(settings, "service_tier") is { Length: > 0 } tier) state.Tier = TierOf(tier);
    }

    private static PriceTier TierOf(string serviceTier) => serviceTier.Trim().ToLowerInvariant() switch
    {
        "priority" or "fast" => PriceTier.Priority,
        "flex" => PriceTier.Flex,
        _ => PriceTier.Standard
    };

    private static void ApplyTokenCount(RolloutState state, JsonElement payload)
    {
        // Codex writes token_count with a null info while a turn is aborted: it says nothing about the totals.
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return;
        if (!info.TryGetProperty("total_token_usage", out var total) || total.ValueKind != JsonValueKind.Object) return;

        var current = new Totals(Long(total, "input_tokens"), Long(total, "cached_input_tokens"),
            Long(total, "cache_write_input_tokens"), Long(total, "output_tokens"));
        // A total copied from the parent of a forked child (see the class remarks): the baseline of the child's own
        // growth, never usage of its own.
        if (state.InheritedHistory)
        {
            state.LastTotal = current;
            return;
        }
        // A total below the previous one is a restart (see the class remarks): counted whole, like the first event.
        var previous = state.LastTotal is { } last && current.Input >= last.Input && current.Output >= last.Output
            ? last
            : Totals.Zero;
        state.LastTotal = current;

        var input = Math.Max(0, current.Input - previous.Input);
        var cached = Math.Max(0, current.Cached - previous.Cached);
        var cacheWrite = Math.Max(0, current.CacheWrite - previous.CacheWrite);
        var output = Math.Max(0, current.Output - previous.Output);

        var prompt = info.TryGetProperty("last_token_usage", out var request) && request.ValueKind == JsonValueKind.Object
            ? Long(request, "input_tokens")
            : input;
        var key = new UsageKey(state.Model ?? "", state.Tier, null, PricingThresholds.BandFor(prompt));
        // input_tokens includes the cached share, exactly as CodexTokenCounter maps it onto TokenUsage.
        var tokens = new LedgerTokens(Math.Max(0, input - cached), output, cached, cacheWrite, 0);
        (state.Model is null ? state.Pending : state.Ledger).Add(key, tokens);
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static long Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    private sealed record Totals(long Input, long Cached, long CacheWrite, long Output)
    {
        public static readonly Totals Zero = new(0, 0, 0, 0);
    }

    private sealed class RolloutState
    {
        public long Offset { get; set; }
        public string? Model { get; private set; }
        public PriceTier Tier { get; set; } = PriceTier.Standard;
        public Totals? LastTotal { get; set; }

        /// <summary>True while reading the history a forked subagent copied from its parent (see the class remarks).</summary>
        public bool InheritedHistory { get; set; }

        public UsageLedgerBuilder Ledger { get; } = new();

        /// <summary>
        /// Growth counted before the rollout named any model, under an empty model: a rollout whose first total comes
        /// before its first turn_context (a conversation the user forked, whose copied total is counted whole) moves
        /// it under the first model the rollout names instead of leaving it unpriced.
        /// </summary>
        public UsageLedgerBuilder Pending { get; } = new();

        public void SetModel(string model)
        {
            if (Model is null)
            {
                foreach (var entry in Pending.ToLedger().Entries) Ledger.Add(entry.Key with { Model = model }, entry.Tokens);
                Pending.Clear();
            }
            Model = model;
        }

        /// <summary>The priced entries plus, until a model is named, the pending growth under an empty model.</summary>
        public UsageLedger ToLedger() => Ledger.ToLedger() + Pending.ToLedger();

        public void Reset()
        {
            Offset = 0;
            Model = null;
            Tier = PriceTier.Standard;
            LastTotal = null;
            InheritedHistory = false;
            Ledger.Clear();
            Pending.Clear();
        }
    }
}
