using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Running token total of a Claude Code transcript (<c>~/.claude/projects/&lt;proj&gt;/&lt;session&gt;.jsonl</c>, and the
/// <c>subagents/…</c> files of the agents it spawns), kept incrementally: every call reads only the bytes appended
/// since the previous one, so a 60 MB transcript is parsed once and then only at its tail.
/// </summary>
/// <remarks>
/// Claude writes one <c>type: "assistant"</c> line per content block of the same API response — text, thinking,
/// tool_use — and every one of them repeats the <c>message.usage</c> of that response. Summing them naively
/// overstates the total by ~2.7× on a real session, so each <c>requestId</c> contributes its usage only once.
/// <para>
/// That repeated usage is identical only in the main transcript. In a subagent transcript the intermediate lines of a
/// streamed response carry a PARTIAL usage (<c>output_tokens</c> still growing, the other three components already
/// final) and the last line carries the real one: on the measured corpus 571 of 586 subagent transcripts have at
/// least one request whose first and last line differ, and keeping the first line loses most of the output tokens.
/// So the counter remembers the usage already counted per <c>requestId</c> and adds only the growth a later line
/// brings, never a negative delta (no request out of 18 736 was ever non-monotone, and a decrease must not lower the
/// total). Counting only the line with a non-null <c>stop_reason</c> is not an option: 4 317 of those requests have
/// no such line at all. A line without <c>requestId</c> cannot be deduped and counts on its own.
/// </para>
/// </remarks>
/// <remarks>Not thread-safe: callers serialize access (the pump does all its IO on its own thread).</remarks>
public sealed class ClaudeTranscriptTokenCounter
{
    private readonly Dictionary<string, TranscriptState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Upper bound on the requests remembered per transcript (the id plus the usage already counted for it).
    /// Duplicate lines of one response are adjacent, so the bound is only a safety net; once it is hit the oldest
    /// requests are forgotten and a very late duplicate would be counted again, which is preferable to a map that
    /// grows for the whole life of the app. 2 000 leaves more than 3× headroom over the busiest transcript measured
    /// (582 distinct request ids), and each entry now carries four longs on top of the 28-character id.
    /// </summary>
    public int MaxSeenRequestIds { get; init; } = 2_000;

    /// <summary>Token total of <paramref name="transcriptPath"/>, including everything appended since the last call.</summary>
    /// <remarks>Never throws: an unreadable file or an unparseable line leaves the last known total in place.</remarks>
    public TokenUsage Read(string transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath)) return TokenUsage.Zero;

        if (!_states.TryGetValue(transcriptPath, out var state))
        {
            state = new TranscriptState();
            _states[transcriptPath] = state;
        }

        try
        {
            // A transcript that does not exist (yet) is not an error: the session may not have written it so far.
            if (!File.Exists(transcriptPath)) return state.Total;

            using var stream = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            // Shorter than what we already consumed: the file was replaced or truncated, so the running total no
            // longer describes it and everything must be counted again.
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
                Accumulate(state, line);
            }

            // Only complete lines are consumed: a half-written one is read again on the next call.
            state.Offset += Encoding.UTF8.GetByteCount(text.AsSpan(0, consumed));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException
                                      or OutOfMemoryException)
        {
            // Keep the last known total: the next sweep will try again.
        }

        return state.Total;
    }

    /// <summary>Returns the model on the newest assistant message in a transcript, or null when unavailable.</summary>
    /// <remarks>This is a deliberately independent, backwards-compatible metadata read; it does not affect token state.</remarks>
    public string? ReadModel(string transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath)) return null;
        try
        {
            foreach (var line in ReverseLineReader.ReadLinesFromEnd(transcriptPath).Take(2_000))
            {
                if (!line.Contains("\"assistant\"", StringComparison.Ordinal)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                        || type.GetString() != "assistant") continue;
                    if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) continue;
                    if (message.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(model.GetString()) && model.GetString() != "<synthetic>") return model.GetString();
                }
                catch (JsonException) { /* keep looking past a partial/corrupt line */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // Metadata is best effort, just like token reads.
        }
        return null;
    }

    /// <summary>Byte offset of the first line of <paramref name="transcriptPath"/> not yet consumed (0 when unknown).</summary>
    public long OffsetOf(string transcriptPath) =>
        _states.TryGetValue(transcriptPath, out var state) ? state.Offset : 0;

    /// <summary>Drops the state of a transcript (its session is gone), so its ids and offset stop costing memory.</summary>
    public void Forget(string transcriptPath) => _states.Remove(transcriptPath);

    private void Accumulate(TranscriptState state, string line)
    {
        // Cheap pre-filter: a transcript is mostly user and tool_result lines, and parsing them costs more than the
        // substring scan that rules them out.
        if (!line.Contains("\"assistant\"", StringComparison.Ordinal)) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) return;
            if (type.GetString() != "assistant") return;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return;

            // Prefer message.id: it remains stable when a stream mixes lines with and without requestId.
            // Prefix the fallback to avoid collisions between the two identifier namespaces.
            var messageKey = message.TryGetProperty("id", out var messageId) && messageId.ValueKind == JsonValueKind.String
                ? messageId.GetString()
                : null;
            var requestKey = root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            var requestId = !string.IsNullOrWhiteSpace(messageKey) ? $"message:{messageKey}" :
                !string.IsNullOrWhiteSpace(requestKey) ? $"request:{requestKey}" : null;
            var counted = new TokenUsage(
                Long(usage, "input_tokens"),
                Long(usage, "output_tokens"),
                Long(usage, "cache_read_input_tokens"),
                Long(usage, "cache_creation_input_tokens"));

            // A line without requestId cannot be deduped and adds whole.
            if (requestId is null)
            {
                state.Total += counted;
                return;
            }

            // Otherwise only the growth over what this request has already contributed is added, so the final
            // (largest) usage of a streamed response wins whether its lines land in one read or in two consecutive
            // ones. A first line carrying the id but no usage at all returned above without remembering anything, so
            // the real numbers of the lines that follow it are still added in full.
            var before = state.Counted(requestId);
            state.Total += new TokenUsage(
                Math.Max(0, counted.Input - before.Input),
                Math.Max(0, counted.Output - before.Output),
                Math.Max(0, counted.CacheRead - before.CacheRead),
                Math.Max(0, counted.CacheWrite - before.CacheWrite));
            state.Remember(requestId, new TokenUsage(
                Math.Max(counted.Input, before.Input),
                Math.Max(counted.Output, before.Output),
                Math.Max(counted.CacheRead, before.CacheRead),
                Math.Max(counted.CacheWrite, before.CacheWrite)), MaxSeenRequestIds);
        }
        catch (JsonException)
        {
            // Not a JSON line (or a half-written one that was rotated in): nothing to count.
        }
    }

    private static long Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>Per-transcript state: where the reading stopped, what each response contributed, and the total.</summary>
    private sealed class TranscriptState
    {
        private readonly Dictionary<string, TokenUsage> _counted = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public long Offset { get; set; }

        public TokenUsage Total { get; set; } = TokenUsage.Zero;

        public void Reset()
        {
            Offset = 0;
            Total = TokenUsage.Zero;
            _counted.Clear();
            _order.Clear();
        }

        /// <summary>Usage already added to the total for <paramref name="requestId"/> (zero when never seen).</summary>
        public TokenUsage Counted(string requestId) =>
            _counted.TryGetValue(requestId, out var usage) ? usage : TokenUsage.Zero;

        /// <summary>Stores what <paramref name="requestId"/> has contributed, evicting the oldest past the bound.</summary>
        public void Remember(string requestId, TokenUsage usage, int limit)
        {
            // Eviction order is first-seen: the repeated lines of one response are adjacent, so re-enqueueing an id
            // on every one of them would only make the queue grow without changing which requests survive.
            if (!_counted.ContainsKey(requestId)) _order.Enqueue(requestId);
            _counted[requestId] = usage;
            while (_order.Count > limit && _order.TryDequeue(out var oldest)) _counted.Remove(oldest);
        }
    }
}
