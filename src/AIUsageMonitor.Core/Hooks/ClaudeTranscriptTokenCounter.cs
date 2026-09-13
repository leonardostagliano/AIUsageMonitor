using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Running token total of a Claude Code transcript (<c>~/.claude/projects/&lt;proj&gt;/&lt;session&gt;.jsonl</c>, and the
/// <c>subagents/…</c> files of the agents it spawns), kept incrementally: every call reads only the bytes appended
/// since the previous one, so a 60 MB transcript is parsed once and then only at its tail.
/// </summary>
/// <remarks>
/// Claude writes one <c>type: "assistant"</c> line per content block of the same API response — text, thinking,
/// tool_use — and every one of them repeats the SAME <c>message.usage</c>. Summing them naively overstates the total
/// by ~2.7× on a real session, so the usage of a <c>requestId</c> is counted once and the following lines that carry
/// it are skipped. A line without <c>requestId</c> cannot be deduped and counts on its own.
/// </remarks>
/// <remarks>Not thread-safe: callers serialize access (the pump does all its IO on its own thread).</remarks>
public sealed class ClaudeTranscriptTokenCounter
{
    private readonly Dictionary<string, TranscriptState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Upper bound on the request ids remembered per transcript. Duplicate lines of one response are adjacent, so a
    /// generous bound is only a safety net; once it is hit the oldest ids are forgotten and a very late duplicate
    /// would be counted twice, which is preferable to a set that grows for the whole life of the app.
    /// </summary>
    public int MaxSeenRequestIds { get; init; } = 10_000;

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

            var requestId = root.TryGetProperty("requestId", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;
            // The id is remembered only once its usage has actually been added: a first line that carries the id but
            // no usage must not hide the real numbers of the lines that follow it.
            if (requestId is not null && !state.MarkSeen(requestId, MaxSeenRequestIds)) return;

            state.Total += new TokenUsage(
                Long(usage, "input_tokens"),
                Long(usage, "output_tokens"),
                Long(usage, "cache_read_input_tokens"),
                Long(usage, "cache_creation_input_tokens"));
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

    /// <summary>Per-transcript state: where the reading stopped, which responses were counted, and the running total.</summary>
    private sealed class TranscriptState
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private readonly Queue<string> _order = new();

        public long Offset { get; set; }

        public TokenUsage Total { get; set; } = TokenUsage.Zero;

        public void Reset()
        {
            Offset = 0;
            Total = TokenUsage.Zero;
            _seen.Clear();
            _order.Clear();
        }

        /// <summary>Records <paramref name="requestId"/>; false when it had already been counted.</summary>
        public bool MarkSeen(string requestId, int limit)
        {
            if (!_seen.Add(requestId)) return false;
            _order.Enqueue(requestId);
            while (_order.Count > limit && _order.TryDequeue(out var oldest)) _seen.Remove(oldest);
            return true;
        }
    }
}
