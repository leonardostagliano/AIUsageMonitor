using System.Globalization;
using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>Where the newest turn of a Codex thread stands, read from its rollout.</summary>
public enum CodexTurnState
{
    /// <summary>No turn event within the lines read, or the rollout could not be read: nothing can be said.</summary>
    Unknown,

    /// <summary>The newest turn event is <c>task_started</c>: the thread is working on a turn.</summary>
    Running,

    /// <summary>The newest turn event is <c>task_complete</c> or <c>turn_aborted</c>: the thread waits for its next turn.</summary>
    Finished
}

/// <summary>
/// A child thread a Codex session spawned (<c>source.subagent.thread_spawn</c>), as its own rollout describes it: the
/// name Codex gave it (<c>agent_nickname</c>, <c>agent_path</c>, either may be missing) and its newest turn event with
/// the instant written on that line (<paramref name="StateAt"/>, null when unknown).
/// </summary>
public sealed record CodexChildInfo(string ThreadId, string? Nickname, string? AgentPath, CodexTurnState State, DateTimeOffset? StateAt);

/// <summary>
/// Reads the rollouts of the child threads a Codex session spawns. As a fallback for Codex, which does not
/// necessarily fire <c>SubagentStart</c>/<c>SubagentStop</c> for them, a child rollout (<c>session_meta.parent_thread_id</c>
/// equal to the parent thread) counts as a running subagent while its newest line is younger than
/// <see cref="ActiveWindow"/> and its newest turn event is <c>task_started</c> (no <c>task_complete</c>/<c>turn_aborted</c>
/// after it). For the children the hooks do report, <see cref="ChildInfo"/> tells the state of their newest turn and
/// their name.
/// </summary>
/// <remarks>
/// The id reported for a child is <c>session_meta.payload.id</c>, the thread's OWN id (the same uuid the file name
/// carries, and the <c>agent_id</c> of Codex's own subagent hooks). <c>payload.session_id</c> is the id of the root
/// conversation and is shared by every thread of the tree, so using it would collapse all the concurrent children of
/// one parent onto a single subagent and make "al lavoro · N agenti" unreachable.
/// </remarks>
/// <remarks>
/// Only a thread Codex spawned for the session (<c>source.subagent.thread_spawn</c>) is a child. The guardian threads
/// Codex runs to review a session's actions (<c>source.subagent.other</c>, <c>thread_source: "guardian_review"</c>)
/// name the session as their parent too and run turns of a few seconds in bursts: they are never children. A rollout
/// written before Codex recorded a <c>source</c> keeps the parent rule, unless its <c>thread_source</c> says it is
/// something other than a subagent.
/// </remarks>
/// <remarks>Build one instance per app lifetime: it caches the immutable <c>session_meta</c> of every rollout it has
/// parsed, and the rollout of every child thread it has looked up, so a sweep only re-reads the tail of the files.</remarks>
public sealed class CodexSubagentScanner
{
    /// <summary>agent_type of the subagents synthesised from rollout files, so the UI can tell them from hook-reported ones.</summary>
    public const string SyntheticAgentType = "codex-thread";

    /// <summary>How long a thread id whose rollout could not be found is not looked up again.</summary>
    public static readonly TimeSpan RolloutMissTtl = TimeSpan.FromSeconds(60);

    private const int MetaLines = 5;

    /// <summary>Entries kept in the session_meta cache before the vanished rollouts are dropped from it.</summary>
    private const int MetaCacheLimit = 2000;

    private const string TaskStarted = "task_started";

    private static readonly string[] TurnEvents = [TaskStarted, "task_complete", "turn_aborted"];

    private readonly string _sessionsDir;
    private readonly IClock _clock;
    private readonly Dictionary<string, RolloutMeta> _meta = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _rollouts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _rolloutMisses = new(StringComparer.OrdinalIgnoreCase);
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
                // A guardian thread names the session as its parent too, but it only reviews the session's actions.
                if (!meta.IsChild) continue;
                // A thread that names itself as its parent is not a child of anything: counting it would make a
                // session wait for its own turn to finish before it may report "finito".
                if (string.Equals(meta.ThreadId, parent, StringComparison.OrdinalIgnoreCase)) continue;

                if (ReadTurn(file.FullName) is not { } turn) { complete = false; continue; }
                if (turn.Event != TaskStarted) continue;
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

    /// <summary>
    /// The spawned child thread <paramref name="threadId"/> as its rollout describes it: its name and where its newest
    /// turn stands. Null when no rollout carries that thread id (not found, or not yet written), when that rollout is
    /// not a spawned child (a guardian, a root thread) and when its session_meta cannot be read. Never throws.
    /// </summary>
    /// <remarks>
    /// The rollout is found by the thread id its file name carries and remembered; an id with no rollout is looked
    /// up again only after <see cref="RolloutMissTtl"/>, so the ids of the other agents a session reports cost one
    /// enumeration a minute at most.
    /// </remarks>
    public CodexChildInfo? ChildInfo(string threadId)
    {
        if (!LooksLikeThreadId(threadId)) return null;
        var path = RolloutOf(threadId);
        if (path is null) return null;
        var meta = MetaFor(path);
        if (meta is not { IsChild: true, ParentThreadId: { } parent }) return null;
        if (string.Equals(meta.ThreadId, parent, StringComparison.OrdinalIgnoreCase)) return null;

        var turn = ReadTurn(path);
        var state = turn?.Event switch
        {
            null => CodexTurnState.Unknown,
            TaskStarted => CodexTurnState.Running,
            _ => CodexTurnState.Finished
        };
        return new CodexChildInfo(threadId, meta.Nickname, meta.AgentPath, state, turn?.EventAt);
    }

    /// <summary>
    /// Only ids made of letters, digits, '-' and '_' are looked up: the id goes into a file search pattern, and the
    /// synthetic ids the tracker gives anonymous agents ("anon:…") are no thread of Codex.
    /// </summary>
    private static bool LooksLikeThreadId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>The rollout whose file name and session_meta carry <paramref name="threadId"/>; null when none is found.</summary>
    private string? RolloutOf(string threadId)
    {
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_rollouts.TryGetValue(threadId, out var known))
            {
                if (File.Exists(known)) return known;
                _rollouts.Remove(threadId);
            }
            if (_rolloutMisses.TryGetValue(threadId, out var missedAt) && now - missedAt < RolloutMissTtl) return null;
        }

        string? found = null;
        try
        {
            if (Directory.Exists(_sessionsDir))
                foreach (var candidate in Directory.EnumerateFiles(_sessionsDir, $"*{threadId}.jsonl", SearchOption.AllDirectories))
                {
                    if (!string.Equals(MetaFor(candidate)?.ThreadId, threadId, StringComparison.OrdinalIgnoreCase)) continue;
                    found = candidate;
                    break;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Not remembered as a miss: an enumeration that failed now may well work on the next scan.
            return null;
        }

        lock (_gate)
        {
            if (found is null)
            {
                foreach (var old in _rolloutMisses.Where(kv => now - kv.Value >= RolloutMissTtl).Select(kv => kv.Key).ToList())
                    _rolloutMisses.Remove(old);
                _rolloutMisses[threadId] = now;
            }
            else
            {
                _rollouts[threadId] = found;
                _rolloutMisses.Remove(threadId);
            }
        }
        return found;
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
    /// Reads the rollout backwards: <c>Event</c> is the newest turn event (<c>task_started</c>, <c>task_complete</c>,
    /// <c>turn_aborted</c>; null when none is found within <see cref="MaxLinesPerFile"/> lines) and <c>EventAt</c> the
    /// timestamp of its line; <c>NewestTs</c> is the timestamp of the newest parseable line (null when no line carries
    /// one). Null when the file could not be read at all — "unknown", which must not be mistaken for "this child has
    /// finished".
    /// </summary>
    private (string? Event, DateTimeOffset? EventAt, DateTimeOffset? NewestTs)? ReadTurn(string path)
    {
        DateTimeOffset? newest = null;
        string? turnEvent = null;
        DateTimeOffset? turnAt = null;
        try
        {
            foreach (var line in ReverseLineReader.ReadLinesFromEnd(path).Take(MaxLinesPerFile))
            {
                if (newest is null && TimestampOf(line) is { } ts) newest = ts;
                if (turnEvent is null)
                {
                    // Cheap pre-filter: only the lines that mention one of the turn events are parsed; an assistant
                    // message that merely quotes one is then discarded by the type check.
                    if (TurnEvents.Any(e => line.Contains(e, StringComparison.Ordinal)) && TurnEventType(line) is { } type)
                    {
                        turnEvent = type;
                        turnAt = TimestampOf(line);
                    }
                }
                if (turnEvent is not null && newest is not null) break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // One locked or vanished rollout must not abort the whole scan, but it must not read as "finished" either.
            return null;
        }
        return (turnEvent, turnAt, newest);
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
                var spawn = ThreadSpawnOf(payload, out var hasSource);
                // A rollout with a source is a child only when Codex spawned it for its parent; one written before
                // Codex recorded a source is a child unless its thread_source says otherwise.
                var isChild = hasSource
                    ? spawn is not null
                    : StringOrNull(payload, "thread_source") is null or "subagent";
                // `payload.id` is the thread's own id in both parent and child rollouts; `payload.session_id` is the
                // root conversation, identical for every thread of the tree, and must never be used as the thread id.
                return (new RolloutMeta(
                    StringOrNull(payload, "id") ?? ThreadIdFromFileName(Path.GetFileName(path)),
                    StringOrNull(payload, "parent_thread_id") ?? StringOrNull(root, "parent_thread_id")
                        ?? (spawn is { } parentSpawn ? StringOrNull(parentSpawn, "parent_thread_id") : null),
                    isChild,
                    StringOrNull(payload, "agent_nickname") ?? (spawn is { } nameSpawn ? StringOrNull(nameSpawn, "agent_nickname") : null),
                    StringOrNull(payload, "agent_path") ?? (spawn is { } pathSpawn ? StringOrNull(pathSpawn, "agent_path") : null)), true);
            }
            catch (JsonException)
            {
                // not JSON: keep looking in the first lines
            }
        }
        // MetaLines complete lines read and none of them was a session_meta: this file has none.
        return (new RolloutMeta(null, null), true);
    }

    /// <summary>
    /// <c>payload.source.subagent.thread_spawn</c> when present. <paramref name="hasSource"/> tells whether the rollout
    /// records a source at all (a string such as "cli" for a root thread, an object for a subagent); an absent or
    /// null source is a rollout written before Codex recorded one.
    /// </summary>
    private static JsonElement? ThreadSpawnOf(JsonElement payload, out bool hasSource)
    {
        hasSource = payload.TryGetProperty("source", out var source) && source.ValueKind != JsonValueKind.Null;
        if (!hasSource || source.ValueKind != JsonValueKind.Object) return null;
        if (!source.TryGetProperty("subagent", out var subagent) || subagent.ValueKind != JsonValueKind.Object) return null;
        return subagent.TryGetProperty("thread_spawn", out var spawn) && spawn.ValueKind == JsonValueKind.Object ? spawn : null;
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

    /// <summary>
    /// What the session_meta of a rollout says: the thread's own id, its parent, whether it is a spawned child (not a
    /// guardian, not a root thread) and the name Codex gave it.
    /// </summary>
    private sealed record RolloutMeta(string? ThreadId, string? ParentThreadId, bool IsChild = false, string? Nickname = null,
        string? AgentPath = null);
}
