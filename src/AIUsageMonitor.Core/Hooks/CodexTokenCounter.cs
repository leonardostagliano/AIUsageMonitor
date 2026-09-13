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
/// <see cref="CacheTtl"/>, so a refresh does not re-enumerate the sessions directory from scratch.</remarks>
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

    /// <summary>Cumulative token usage of <paramref name="threadId"/>; zero when its rollout is unknown or carries no totals.</summary>
    /// <remarks>Never throws: an unreadable or vanished rollout reads as zero.</remarks>
    public TokenUsage ReadThread(string threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId)) return TokenUsage.Zero;
        var path = ResolvePath(threadId);
        return path is null ? TokenUsage.Zero : ReadTokens(path) ?? TokenUsage.Zero;
    }

    /// <summary>
    /// The threads whose <c>session_meta.parent_thread_id</c> is <paramref name="parentThreadId"/>, with their own
    /// cumulative totals and the last write time of their rollout. Empty both when there are none and when the
    /// sessions directory could not be enumerated.
    /// </summary>
    public IReadOnlyList<(string ThreadId, TokenUsage Tokens, DateTime LastWriteUtc)> ReadChildren(string parentThreadId)
    {
        var children = new List<(string ThreadId, TokenUsage Tokens, DateTime LastWriteUtc)>();
        if (string.IsNullOrWhiteSpace(parentThreadId)) return children;

        foreach (var file in RecentRollouts())
        {
            var meta = MetaFor(file.FullName);
            if (meta?.ParentThreadId is not { } parent) continue;
            if (!string.Equals(parent, parentThreadId, StringComparison.OrdinalIgnoreCase)) continue;
            // A thread that names itself as its parent is not a child of anything.
            if (string.Equals(meta.ThreadId, parent, StringComparison.OrdinalIgnoreCase)) continue;

            var threadId = meta.ThreadId ?? ThreadIdFromFileName(file.Name);
            if (string.IsNullOrWhiteSpace(threadId)) continue;
            // Two rollouts of the same thread (a resumed child): the newest wins, the list is already ordered.
            if (children.Any(c => string.Equals(c.ThreadId, threadId, StringComparison.OrdinalIgnoreCase))) continue;

            children.Add((threadId, ReadTokens(file.FullName) ?? TokenUsage.Zero, file.LastWriteTimeUtc));
        }
        return children;
    }

    /// <summary>The rollout of <paramref name="threadId"/>, from the cache when it is still fresh.</summary>
    private string? ResolvePath(string threadId)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_byThread.TryGetValue(threadId, out var cached) && now - cached.At < CacheTtl
                && (cached.Path is null || File.Exists(cached.Path)))
                return cached.Path;
        }

        var path = ScanForThread(threadId);

        lock (_gate)
        {
            PruneThreadCache(now);
            _byThread[threadId] = (path, now);
        }
        return path;
    }

    private string? ScanForThread(string threadId)
    {
        try
        {
            if (!Directory.Exists(_sessionsDir)) return null;

            var byName = Directory.EnumerateFiles(_sessionsDir, $"*{threadId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (byName is not null) return byName;

            // No file carries the id (a renamed or resumed rollout): fall back to the session_meta of the recent ones,
            // preferring the thread's own id over the conversation id, which every thread of the tree shares.
            string? byConversation = null;
            foreach (var file in RecentRollouts())
            {
                var meta = MetaFor(file.FullName);
                if (meta is null) continue;
                if (string.Equals(meta.ThreadId, threadId, StringComparison.OrdinalIgnoreCase)) return file.FullName;
                if (byConversation is null && string.Equals(meta.SessionId, threadId, StringComparison.OrdinalIgnoreCase))
                    byConversation = file.FullName;
            }
            return byConversation;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>The rollouts touched within <see cref="LookBack"/>, newest first; empty when the directory cannot be read.</summary>
    private IReadOnlyList<FileInfo> RecentRollouts()
    {
        try
        {
            if (!Directory.Exists(_sessionsDir)) return [];
            var cutoff = (_clock.UtcNow - LookBack).UtcDateTime;
            return new DirectoryInfo(_sessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= cutoff)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFilesToScan)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// The newest <c>token_count</c> of a rollout mapped onto a <see cref="TokenUsage"/>; null when the file could not
    /// be read or carries no usable totals (a <c>token_count</c> with a null <c>info</c> is skipped, not counted as zero).
    /// </summary>
    private TokenUsage? ReadTokens(string path)
    {
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
            // One locked or vanished rollout reads as "unknown": the caller keeps zero.
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
