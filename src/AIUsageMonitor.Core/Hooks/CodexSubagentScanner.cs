using System.Globalization;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Fallback for Codex, which does not necessarily fire <c>SubagentStart</c>/<c>SubagentStop</c> for the threads a
/// session spawns: a child rollout (<c>session_meta.parent_thread_id</c> equal to the parent thread) counts as a
/// running subagent while its newest line is younger than <see cref="ActiveWindow"/> and its newest turn event is
/// <c>task_started</c> (no <c>task_complete</c>/<c>turn_aborted</c> after it).
/// </summary>
/// <remarks>
/// The id reported for a child is <c>session_meta.payload.id</c>, the thread's OWN id (the same uuid the file name
/// carries). <c>payload.session_id</c> is the id of the root conversation and is shared by every thread of the tree,
/// so using it would collapse all the concurrent children of one parent onto a single subagent and make
/// "al lavoro · N agenti" unreachable.
/// </remarks>
/// <remarks>Build one instance per app lifetime: it caches the immutable <c>session_meta</c> of every rollout it has
/// parsed, so a sweep only re-reads the tail of the files that changed recently.</remarks>
public sealed class CodexSubagentScanner
{
    /// <summary>agent_type of the subagents synthesised from rollout files, so the UI can tell them from hook-reported ones.</summary>
    public const string SyntheticAgentType = "codex-thread";

    private const int MetaLines = 5;

    /// <summary>Entries kept in the session_meta cache before the vanished rollouts are dropped from it.</summary>
    private const int MetaCacheLimit = 2000;

    private static readonly string[] TurnEvents = ["task_started", "task_complete", "turn_aborted"];

    private readonly string _sessionsDir;
    private readonly IClock _clock;
    private readonly Dictionary<string, RolloutMeta> _meta = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CodexSubagentScanner(string sessionsDir, IClock? clock = null)
    {
        _sessionsDir = sessionsDir;
        _clock = clock ?? new SystemClock();
    }

    /// <summary>
    /// How long a child rollout whose turn is still open may stay untouched before it stops counting as a running
    /// subagent. It is deliberately generous: a running child appends nothing to its rollout while a long exec or
    /// tool call is in flight (measured on this machine, gaps of several minutes inside turns that then completed
    /// normally are routine), and reporting such a child as finished would take its session to Idle and toast
    /// "Turno completato" mid-turn, only to flip back to "al lavoro" on the next scan and toast again at the real
    /// end. This is a backstop for a child killed without a terminal event, not a liveness probe: the pump's
    /// 30-minute <c>SubagentTimeout</c> is the real one.
    /// </summary>
    public TimeSpan ActiveWindow { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How far back the enumeration looks for candidate rollouts. It is deliberately much wider than
    /// <see cref="ActiveWindow"/>: Windows does not refresh the NTFS directory entry of a file a process keeps open,
    /// so the mtime the enumeration reports lags for the whole life of a live Codex thread and cannot be used to
    /// decide freshness — only to keep the candidate list small.
    /// </summary>
    public TimeSpan CandidateWindow { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Upper bound on the rollouts inspected per scan (they are already filtered by <see cref="CandidateWindow"/>).</summary>
    public int MaxFilesToScan { get; init; } = 500;

    /// <summary>Upper bound on the lines read from the end of a rollout while looking for its newest turn event.</summary>
    public int MaxLinesPerFile { get; init; } = 5000;

    public int CountActiveChildren(string parentThreadId) => ActiveChildren(parentThreadId).Count;

    /// <summary>
    /// Thread ids of the children of <paramref name="parentThreadId"/> whose turn is still running; empty both when
    /// there are none and when the scan failed. Use <see cref="TryGetActiveChildren"/> to tell the two apart.
    /// </summary>
    public IReadOnlyList<string> ActiveChildren(string parentThreadId)
    {
        TryGetActiveChildren(parentThreadId, out var children);
        return children;
    }

    /// <summary>
    /// Thread ids of the children of <paramref name="parentThreadId"/> whose turn is still running. Returns false when
    /// the rollouts could not be enumerated at all (missing directory, IO error): <paramref name="children"/> is then
    /// an empty, meaningless list and the caller must keep the children it already knows about instead of stopping
    /// them — an unreadable sweep would otherwise report a live child as finished.
    /// </summary>
    public bool TryGetActiveChildren(string parentThreadId, out IReadOnlyList<string> children)
    {
        children = [];
        if (string.IsNullOrWhiteSpace(parentThreadId)) return true;

        var active = new List<string>();
        var complete = true;
        try
        {
            if (!Directory.Exists(_sessionsDir)) return false;
            var now = _clock.UtcNow;
            var candidateCutoff = (now - CandidateWindow).UtcDateTime;
            var files = new DirectoryInfo(_sessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= candidateCutoff)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFilesToScan)
                .ToList();

            foreach (var file in files)
            {
                var meta = MetaFor(file.FullName);
                // A rollout whose first lines could not be read may well be a child of this very parent: the scan
                // no longer proves anything about it, so it is reported as incomplete instead of as "not a child".
                if (meta is null) { complete = false; continue; }
                if (meta.ParentThreadId is not { } parent) continue;
                if (!string.Equals(parent, parentThreadId, StringComparison.OrdinalIgnoreCase)) continue;
                // A thread that names itself as its parent is not a child of anything: counting it would make a
                // session wait for its own turn to finish before it may report "finito".
                if (string.Equals(meta.ThreadId, parent, StringComparison.OrdinalIgnoreCase)) continue;

                if (ReadTurnState(file.FullName) is not { } turn) { complete = false; continue; }
                if (!turn.Running) continue;
                // Freshness comes from the rollout's own newest timestamp, not from the directory entry the
                // enumeration cached; without one, a live re-query of the mtime is still better than that cache.
                var lastActivity = turn.NewestTs ?? LastWriteOrNull(file.FullName);
                if (lastActivity is null || now - lastActivity.Value > ActiveWindow) continue;

                var threadId = meta.ThreadId ?? ThreadIdFromFileName(file.Name);
                if (string.IsNullOrWhiteSpace(threadId)) continue;
                if (!active.Contains(threadId, StringComparer.OrdinalIgnoreCase)) active.Add(threadId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // The enumeration itself failed: the list collected so far says nothing about the children that were
            // never reached, so the scan is reported as failed rather than as "no children".
            children = active;
            return false;
        }
        children = active;
        return complete;
    }

    private static DateTimeOffset? LastWriteOrNull(string path)
    {
        try { return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the rollout backwards: <c>Running</c> is true when the newest turn event is a <c>task_started</c>, and
    /// <c>NewestTs</c> is the timestamp of the newest parseable line (null when no line carries one). Null when the
    /// file could not be read at all — "unknown", which must not be mistaken for "this child has finished".
    /// </summary>
    private (bool Running, DateTimeOffset? NewestTs)? ReadTurnState(string path)
    {
        DateTimeOffset? newest = null;
        var running = false;
        var turnFound = false;
        try
        {
            foreach (var line in ReverseLineReader.ReadLinesFromEnd(path).Take(MaxLinesPerFile))
            {
                if (newest is null && TimestampOf(line) is { } ts) newest = ts;
                if (!turnFound)
                {
                    // Cheap pre-filter: only the lines that mention one of the turn events are parsed; an assistant
                    // message that merely quotes one is then discarded by the type check.
                    if (TurnEvents.Any(e => line.Contains(e, StringComparison.Ordinal)) && TurnEventType(line) is { } type)
                    {
                        running = type == "task_started";
                        turnFound = true;
                    }
                }
                if (turnFound && newest is not null) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // One locked or vanished rollout must not abort the whole scan, but it must not read as "finished" either.
            return null;
        }
        return (running && turnFound, newest);
    }

    /// <summary>The `timestamp` of a rollout line, when it carries a parseable one.</summary>
    private static DateTimeOffset? TimestampOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("timestamp", out var ts) || ts.ValueKind != JsonValueKind.String) return null;
            return DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TurnEventType(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "event_msg") return null;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
            if (!payload.TryGetProperty("type", out var payloadType) || payloadType.ValueKind != JsonValueKind.String) return null;
            var name = payloadType.GetString();
            return name is not null && TurnEvents.Contains(name) ? name : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// session_meta of a rollout. Only a conclusive read is cached (the line was found, or the first
    /// <see cref="MetaLines"/> lines were all read without finding it): a rollout caught between its creation and the
    /// first flush of its session_meta would otherwise be remembered as "no parent" for the life of the process and
    /// that child could never be detected again.
    /// </summary>
    private RolloutMeta? MetaFor(string path)
    {
        lock (_gate)
            if (_meta.TryGetValue(path, out var cached)) return cached;

        RolloutMeta meta;
        bool conclusive;
        try
        {
            (meta, conclusive) = ReadMeta(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Not cached: a rollout that could not be read now may well be readable on the next sweep.
            return null;
        }

        if (!conclusive) return meta;

        lock (_gate)
        {
            if (_meta.Count >= MetaCacheLimit) EvictVanishedRollouts();
            _meta[path] = meta;
        }
        return meta;
    }

    /// <summary>Drops the cached metas of rollouts that no longer exist, so the cache cannot grow without bound.</summary>
    private void EvictVanishedRollouts()
    {
        foreach (var path in _meta.Keys.Where(p => !File.Exists(p)).ToList()) _meta.Remove(path);
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
            if (line is null) return (new RolloutMeta(null, null), false);
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta") continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                // `payload.id` is the thread's own id in both parent and child rollouts; `payload.session_id` is the
                // root conversation, identical for every thread of the tree, and must never be used as the thread id.
                return (new RolloutMeta(
                    StringOrNull(payload, "id") ?? ThreadIdFromFileName(Path.GetFileName(path)),
                    StringOrNull(payload, "parent_thread_id") ?? StringOrNull(root, "parent_thread_id")), true);
            }
            catch (JsonException)
            {
                // not JSON: keep looking in the first lines
            }
        }
        // MetaLines complete lines read and none of them was a session_meta: this file has none.
        return (new RolloutMeta(null, null), true);
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

    private sealed record RolloutMeta(string? ThreadId, string? ParentThreadId);
}
