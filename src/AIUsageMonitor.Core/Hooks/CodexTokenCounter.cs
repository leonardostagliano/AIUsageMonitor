using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Token totals of a Codex thread and of the threads it spawned, read from the rollout files under
/// <c>~/.codex/sessions</c>.
/// </summary>
/// <remarks>
/// Codex does not expose a transcript path to its hooks, so a thread is located by its rollout: the file name ends
/// with the thread id (<c>rollout-&lt;timestamp&gt;-&lt;uuid&gt;.jsonl</c>) and, when it does not, the first lines of the
/// recent rollouts are scanned for a <c>session_meta</c> whose <c>id</c> — or, for a resumed conversation, whose
/// <c>session_id</c> — is the wanted thread (the same two-step lookup as <see cref="CodexSessionResolver"/>).
/// <para>
/// Every turn appends an <c>event_msg/token_count</c> whose <c>info.total_token_usage</c> is CUMULATIVE for the
/// thread, so only the newest one matters and the file is read backwards. <c>input_tokens</c> already includes
/// <c>cached_input_tokens</c>: the cached share is moved to <see cref="TokenUsage.CacheRead"/> so that
/// <see cref="TokenUsage.Total"/> keeps matching the <c>total_tokens</c> Codex reports.
/// </para>
/// </remarks>
/// <remarks>Build one instance per app lifetime: it caches the thread→rollout and rollout→session_meta lookups for
/// <see cref="CacheTtl"/>, so a refresh does not re-enumerate the sessions directory from scratch, and it remembers
/// the last total read for every thread it has seen.</remarks>
/// <remarks>
/// "Could not read" is never reported as "zero tokens": a rollout locked by an antivirus scan, rotated mid-read or
/// enumerated while the directory is busy leaves the last known total of that thread in place (as
/// <see cref="ClaudeTranscriptTokenCounter"/> does) instead of dropping a live session from millions to zero for a
/// whole <see cref="CacheTtl"/>. <see cref="TryReadThread"/> and <see cref="TryReadChildren"/> expose the difference
/// between a fresh read and a stale or incomplete one, the way <c>CodexSubagentScanner.TryGetActiveChildren</c> does.
/// </remarks>
/// <remarks>Not thread-safe for the caller's purposes beyond its own locks: the pump does all its IO on one thread.</remarks>
public sealed class CodexTokenCounter
{
    /// <summary>How long a thread→rollout lookup and a parsed <c>session_meta</c> are reused before being read again.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private const int MetaLines = 5;

    /// <summary>Entries kept in the session_meta cache before the expired ones are dropped from it.</summary>
    private const int MetaCacheLimit = 2000;

    private readonly string _sessionsDir;
    private readonly IClock _clock;
    private readonly Dictionary<string, (string? Path, DateTimeOffset At)> _byThread = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (RolloutMeta Meta, DateTimeOffset At)> _meta = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Newest total actually read for each thread, never expired: it is what a failed read falls back to. One entry
    /// per thread ever asked about (four longs plus the id), so the same bound as the other caches is a safety net
    /// rather than a working limit — a machine reaches it only after thousands of distinct threads in one app run.
    /// </summary>
    private readonly Dictionary<string, TokenUsage> _lastKnown = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CodexTokenCounter(string sessionsDir, IClock? clock = null)
    {
        _sessionsDir = sessionsDir;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>How far back the enumerations look for candidate rollouts.</summary>
    public TimeSpan LookBack { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Upper bound on the rollouts inspected per scan (they are already filtered by <see cref="LookBack"/>).</summary>
    public int MaxFilesToScan { get; init; } = 500;

    /// <summary>Upper bound on the lines read from the end of a rollout while looking for its newest token_count.</summary>
    public int MaxLinesPerFile { get; init; } = 5000;

    /// <summary>
    /// Cumulative token usage of <paramref name="threadId"/>: the newest total of its rollout, the last total read
    /// for it when this sweep could not read one, zero when its rollout is unknown and none was ever read.
    /// </summary>
    /// <remarks>Never throws. Use <see cref="TryReadThread"/> to tell a fresh total from a stale one.</remarks>
    public TokenUsage ReadThread(string threadId)
    {
        TryReadThread(threadId, out var tokens);
        return tokens;
    }

    /// <summary>Returns the newest model name recorded in a thread rollout, or null when unavailable.</summary>
    /// <remarks>Best-effort metadata lookup; it does not change token totals or the cumulative-read cache.</remarks>
    public string? ReadModel(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId) || !TryResolvePath(threadId, out var path) || path is null) return null;
        try
        {
            foreach (var line in ReverseLineReader.ReadLinesFromEnd(path).Take(MaxLinesPerFile))
            {
                if (!line.Contains("\"model\"", StringComparison.Ordinal)) continue;
                if (ModelOf(line) is { } model) return model;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Metadata is optional and must not turn a healthy token read into an error.
        }
        return null;
    }

    /// <summary>
    /// Cumulative token usage of <paramref name="threadId"/>. Returns false when the totals could not be read at all
    /// (the rollout is locked, vanished mid-read, or the sessions directory could not be enumerated):
    /// <paramref name="tokens"/> is then the last total read for that thread — zero when there is none — and the
    /// caller must keep what it already shows instead of reading it as "this session used nothing".
    /// </summary>
    public bool TryReadThread(string threadId, out TokenUsage tokens)
    {
        tokens = TokenUsage.Zero;
        // Nothing to look for: a conclusive "no tokens", not a failed read.
        if (string.IsNullOrWhiteSpace(threadId)) return true;

        if (!TryResolvePath(threadId, out var path))
        {
            tokens = LastKnown(threadId);
            return false;
        }

        // No rollout carries this thread (yet): its last known total, or zero when it never had one. A rollout never
        // un-writes its token_count, so a thread whose file was rotated away keeps the total it had reached.
        if (path is null)
        {
            tokens = LastKnown(threadId);
            return true;
        }

        var usage = ReadTokens(path, out var failed);
        if (usage is { } fresh)
        {
            Remember(threadId, fresh);
            tokens = fresh;
            return true;
        }

        // The file was read but carries no usable total: a thread that has not finished its first turn yet.
        tokens = LastKnown(threadId);
        return !failed;
    }

    /// <summary>
    /// The threads whose <c>session_meta.parent_thread_id</c> is <paramref name="parentThreadId"/>, with their own
    /// cumulative totals and the last write time of their rollout. Empty both when there are none and when the
    /// sessions directory could not be enumerated — use <see cref="TryReadChildren"/> to tell the two apart.
    /// </summary>
    public IReadOnlyList<(string ThreadId, TokenUsage Tokens, DateTime LastWriteUtc)> ReadChildren(string parentThreadId)
    {
        TryReadChildren(parentThreadId, out var children);
        return children;
    }

    /// <summary>
    /// The children of <paramref name="parentThreadId"/> with their own cumulative totals. Returns false when the
    /// sweep was incomplete — the rollouts could not be enumerated, a candidate's <c>session_meta</c> could not be
    /// read (it may well be a child of this very parent), or a child's totals are not known at all — in which case
    /// <paramref name="children"/> holds only what could be established and the caller must merge it with the
    /// children it already knows instead of replacing them. A child whose rollout could not be re-read but whose
    /// total was read before keeps that total and does not make the sweep incomplete.
    /// </summary>
    public bool TryReadChildren(string parentThreadId, out IReadOnlyList<(string ThreadId, TokenUsage Tokens, DateTime LastWriteUtc)> children)
    {
        children = [];
        if (string.IsNullOrWhiteSpace(parentThreadId)) return true;
        if (!TryRecentRollouts(out var files)) return false;

        var found = new List<(string ThreadId, TokenUsage Tokens, DateTime LastWriteUtc)>();
        var complete = true;
        foreach (var file in files)
        {
            var meta = MetaFor(file.FullName);
            // A rollout whose first lines could not be read may well be a child: the sweep no longer proves anything
            // about it, so it is reported as incomplete instead of as "not a child".
            if (meta is null) { complete = false; continue; }
            if (meta.ParentThreadId is not { } parent) continue;
            if (!string.Equals(parent, parentThreadId, StringComparison.OrdinalIgnoreCase)) continue;
            // A thread that names itself as its parent is not a child of anything.
            if (string.Equals(meta.ThreadId, parent, StringComparison.OrdinalIgnoreCase)) continue;

            var threadId = meta.ThreadId ?? ThreadIdFromFileName(file.Name);
            if (string.IsNullOrWhiteSpace(threadId)) continue;
            // Two rollouts of the same thread (a resumed child): the newest wins, the list is already ordered.
            if (found.Any(c => string.Equals(c.ThreadId, threadId, StringComparison.OrdinalIgnoreCase))) continue;

            var usage = ReadTokens(file.FullName, out var failed);
            if (usage is { } fresh) Remember(threadId, fresh);
            else if (failed && !Knows(threadId)) complete = false;

            found.Add((threadId, usage ?? LastKnown(threadId), file.LastWriteTimeUtc));
        }

        children = found;
        return complete;
    }

    /// <summary>The last total read for <paramref name="threadId"/>, zero when none ever was.</summary>
    private TokenUsage LastKnown(string threadId)
    {
        lock (_gate) return _lastKnown.TryGetValue(threadId, out var tokens) ? tokens : TokenUsage.Zero;
    }

    private bool Knows(string threadId)
    {
        lock (_gate) return _lastKnown.ContainsKey(threadId);
    }

    /// <summary>Stores a total that was actually read, so a later failed read can fall back to it.</summary>
    private void Remember(string threadId, TokenUsage tokens)
    {
        lock (_gate)
        {
            // Past the bound the whole map is dropped rather than grown without limit: the live threads then rebuild
            // their entry on their very next refresh, which costs one read each.
            if (_lastKnown.Count >= MetaCacheLimit && !_lastKnown.ContainsKey(threadId)) _lastKnown.Clear();
            _lastKnown[threadId] = tokens;
        }
    }

    /// <summary>
    /// The rollout of <paramref name="threadId"/>, from the cache when it is still fresh. Returns false when the scan
    /// could not settle the question, in which case nothing is cached: a transient lock must not be remembered as
    /// "this thread has no rollout" for a whole <see cref="CacheTtl"/>.
    /// </summary>
    private bool TryResolvePath(string threadId, out string? path)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_byThread.TryGetValue(threadId, out var cached) && now - cached.At < CacheTtl
                && (cached.Path is null || File.Exists(cached.Path)))
            {
                path = cached.Path;
                return true;
            }
        }

        if (!TryScanForThread(threadId, out path)) return false;

        lock (_gate)
        {
            PruneThreadCache(now);
            _byThread[threadId] = (path, now);
        }
        return true;
    }

    /// <summary>The rollout of a thread, or null when no recent one belongs to it; false when the scan failed.</summary>
    private bool TryScanForThread(string threadId, out string? path)
    {
        path = null;
        try
        {
            // No sessions directory at all: a conclusive "no rollout", cheap enough to re-check on every sweep.
            if (!Directory.Exists(_sessionsDir)) return true;

            var byName = Directory.EnumerateFiles(_sessionsDir, $"*{threadId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (byName is not null)
            {
                path = byName;
                return true;
            }

            // No file carries the id (a renamed or resumed rollout): fall back to the session_meta of the recent ones,
            // preferring the thread's own id over the conversation id, which every thread of the tree shares.
            if (!TryRecentRollouts(out var files)) return false;

            string? byConversation = null;
            var complete = true;
            foreach (var file in files)
            {
                var meta = MetaFor(file.FullName);
                // Unreadable right now: it could be the very rollout we are looking for.
                if (meta is null) { complete = false; continue; }
                if (string.Equals(meta.ThreadId, threadId, StringComparison.OrdinalIgnoreCase))
                {
                    path = file.FullName;
                    return true;
                }
                if (byConversation is null && string.Equals(meta.SessionId, threadId, StringComparison.OrdinalIgnoreCase))
                    byConversation = file.FullName;
            }
            path = byConversation;
            // A miss is only conclusive when every candidate could be inspected.
            return path is not null || complete;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            path = null;
            return false;
        }
    }

    /// <summary>
    /// The rollouts touched within <see cref="LookBack"/>, newest first. Returns false when the enumeration failed —
    /// the empty list is then meaningless, as opposed to the empty list of a directory that simply has no rollouts.
    /// </summary>
    private bool TryRecentRollouts(out IReadOnlyList<FileInfo> files)
    {
        files = [];
        try
        {
            if (!Directory.Exists(_sessionsDir)) return true;
            var cutoff = (_clock.UtcNow - LookBack).UtcDateTime;
            files = new DirectoryInfo(_sessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= cutoff)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFilesToScan)
                .ToList();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            files = [];
            return false;
        }
    }

    /// <summary>
    /// The newest <c>token_count</c> of a rollout mapped onto a <see cref="TokenUsage"/>; null when the file carries
    /// no usable totals (a <c>token_count</c> with a null <c>info</c> is skipped, not counted as zero) or could not be
    /// read at all, which <paramref name="failed"/> reports so the caller can keep the last total instead of zero.
    /// </summary>
    private TokenUsage? ReadTokens(string path, out bool failed)
    {
        failed = false;
        try
        {
            foreach (var line in ReverseLineReader.ReadLinesFromEnd(path).Take(MaxLinesPerFile))
            {
                // Cheap pre-filter: only the lines that mention the event are parsed; an assistant message that
                // merely quotes it is then discarded by the type check.
                if (!line.Contains("token_count", StringComparison.Ordinal)) continue;
                if (TotalUsageOf(line) is { } usage) return usage;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // One locked or vanished rollout reads as "unknown": the caller keeps the last total it read.
            failed = true;
        }
        return null;
    }

    private static TokenUsage? TotalUsageOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "event_msg") return null;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            if (!payload.TryGetProperty("type", out var payloadType) || payloadType.ValueKind != JsonValueKind.String
                || payloadType.GetString() != "token_count") return null;
            // Codex writes token_count with a null info while a turn is aborted: it says nothing about the totals.
            if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return null;
            if (!info.TryGetProperty("total_token_usage", out var total) || total.ValueKind != JsonValueKind.Object) return null;

            var input = Long(total, "input_tokens");
            var cached = Long(total, "cached_input_tokens");
            return new TokenUsage(Math.Max(0, input - cached), Long(total, "output_tokens"), cached,
                Long(total, "cache_write_input_tokens"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ModelOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var recordType) || recordType.ValueKind != JsonValueKind.String) return null;
            var type = recordType.GetString();
            var accepted = string.Equals(type, "session_meta", StringComparison.Ordinal)
                || string.Equals(type, "turn_context", StringComparison.Ordinal);
            JsonElement payload = default;
            if (root.TryGetProperty("payload", out var candidate) && candidate.ValueKind == JsonValueKind.Object)
            {
                payload = candidate;
                // Older rollouts wrapped turn_context in event_msg; retain support for that shape.
                if (string.Equals(type, "event_msg", StringComparison.Ordinal)
                    && payload.TryGetProperty("type", out var payloadType)
                    && payloadType.ValueKind == JsonValueKind.String
                    && string.Equals(payloadType.GetString(), "turn_context", StringComparison.Ordinal)) accepted = true;
            }
            if (!accepted) return null;
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("model", out var model)
                && model.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(model.GetString())) return model.GetString();
            // A few rollout versions put model directly on the metadata record.
            if (root.TryGetProperty("model", out model) && model.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(model.GetString())) return model.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    private static long Long(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
            ? number
            : 0;

    /// <summary>
    /// session_meta of a rollout, cached for <see cref="CacheTtl"/>. Only a conclusive read is cached (the line was
    /// found, or the first <see cref="MetaLines"/> lines were all read without finding it): a rollout caught between
    /// its creation and the first flush of its session_meta would otherwise be remembered as "no parent".
    /// </summary>
    private RolloutMeta? MetaFor(string path)
    {
        var now = _clock.UtcNow;
        lock (_gate)
            if (_meta.TryGetValue(path, out var cached) && now - cached.At < CacheTtl) return cached.Meta;

        RolloutMeta meta;
        bool conclusive;
        try
        {
            (meta, conclusive) = ReadMeta(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Not cached: a rollout that could not be read now may well be readable on the next refresh.
            return null;
        }

        if (!conclusive) return meta;

        lock (_gate)
        {
            if (_meta.Count >= MetaCacheLimit) PruneMetaCache(now);
            _meta[path] = (meta, now);
        }
        return meta;
    }

    private void PruneMetaCache(DateTimeOffset now)
    {
        foreach (var path in _meta.Where(kv => now - kv.Value.At >= CacheTtl).Select(kv => kv.Key).ToList()) _meta.Remove(path);
        // Still full of fresh entries: start over rather than grow without bound.
        if (_meta.Count >= MetaCacheLimit) _meta.Clear();
    }

    private void PruneThreadCache(DateTimeOffset now)
    {
        if (_byThread.Count < MetaCacheLimit) return;
        foreach (var id in _byThread.Where(kv => now - kv.Value.At >= CacheTtl).Select(kv => kv.Key).ToList()) _byThread.Remove(id);
        if (_byThread.Count >= MetaCacheLimit) _byThread.Clear();
    }

    /// <summary>The meta plus whether the read settled the question (false when the file ended first).</summary>
    private static (RolloutMeta Meta, bool Conclusive) ReadMeta(string path)
    {
        // Codex (Rust std::fs) keeps the live rollout open with share ReadWrite|Delete: open it the same way or the read fails.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        for (var read = 0; read < MetaLines; read++)
        {
            var line = reader.ReadLine();
            // End of file before the meta line: the rollout may simply not have been flushed yet.
            if (line is null) return (new RolloutMeta(null, null, null), false);
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta") continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                // `payload.id` is the thread's own id; `payload.session_id` is the root conversation, shared by every
                // thread of the tree, so it only ever serves as a last-resort match.
                return (new RolloutMeta(
                    StringOrNull(payload, "id") ?? ThreadIdFromFileName(Path.GetFileName(path)),
                    StringOrNull(payload, "parent_thread_id") ?? StringOrNull(root, "parent_thread_id"),
                    StringOrNull(payload, "session_id")), true);
            }
            catch (JsonException)
            {
                // not JSON: keep looking in the first lines
            }
        }
        // MetaLines complete lines read and none of them was a session_meta: this file has none.
        return (new RolloutMeta(null, null, null), true);
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Thread id of a `rollout-&lt;timestamp&gt;-&lt;uuid&gt;.jsonl` whose session_meta carries no `id`.</summary>
    private static string ThreadIdFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 5 ? string.Join('-', parts[^5..]) : name;
    }

    private sealed record RolloutMeta(string? ThreadId, string? ParentThreadId, string? SessionId);
}
