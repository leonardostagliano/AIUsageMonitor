using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>
/// Fallback for Codex, which does not necessarily fire <c>SubagentStart</c>/<c>SubagentStop</c> for the threads a
/// session spawns: a child rollout (<c>session_meta.parent_thread_id</c> equal to the parent thread) counts as a
/// running subagent while it was written within <see cref="ActiveWindow"/> and its newest turn event is
/// <c>task_started</c> (no <c>task_complete</c>/<c>turn_aborted</c> after it).
/// </summary>
/// <remarks>Build one instance per app lifetime: it caches the immutable <c>session_meta</c> of every rollout it has
/// parsed, so a sweep only re-reads the tail of the files that changed recently.</remarks>
public sealed class CodexSubagentScanner
{
    /// <summary>agent_type of the subagents synthesised from rollout files, so the UI can tell them from hook-reported ones.</summary>
    public const string SyntheticAgentType = "codex-thread";

    private const int MetaLines = 5;

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

    /// <summary>How long a child rollout may stay untouched before it stops counting as a running subagent.</summary>
    public TimeSpan ActiveWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Upper bound on the rollouts inspected per scan (they are already filtered by <see cref="ActiveWindow"/>).</summary>
    public int MaxFilesToScan { get; init; } = 200;

    /// <summary>Upper bound on the lines read from the end of a rollout while looking for its newest turn event.</summary>
    public int MaxLinesPerFile { get; init; } = 5000;

    public int CountActiveChildren(string parentThreadId) => ActiveChildren(parentThreadId).Count;

    /// <summary>Thread ids of the children of <paramref name="parentThreadId"/> whose turn is still running; empty when there are none.</summary>
    public IReadOnlyList<string> ActiveChildren(string parentThreadId)
    {
        if (string.IsNullOrWhiteSpace(parentThreadId)) return [];
        var active = new List<string>();
        try
        {
            if (!Directory.Exists(_sessionsDir)) return [];
            var cutoff = (_clock.UtcNow - ActiveWindow).UtcDateTime;
            var files = new DirectoryInfo(_sessionsDir)
                .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTimeUtc >= cutoff)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFilesToScan);

            foreach (var file in files)
            {
                var meta = MetaFor(file.FullName);
                if (meta?.ParentThreadId is not { } parent) continue;
                if (!string.Equals(parent, parentThreadId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsTurnRunning(file.FullName)) continue;

                var threadId = meta.ThreadId ?? ThreadIdFromFileName(file.Name);
                if (string.IsNullOrWhiteSpace(threadId)) continue;
                if (!active.Contains(threadId, StringComparer.OrdinalIgnoreCase)) active.Add(threadId);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Degrade to what was collected so far: the caller keeps the subagents it already knows about and
            // the timeout sweep releases them if the directory stays unreadable.
        }
        return active;
    }

    /// <summary>True when the newest turn event of the rollout is a task_started (lines are read from the end, newest first).</summary>
    private bool IsTurnRunning(string path)
    {
        try
        {
            foreach (var line in ReverseLineReader.ReadLinesFromEnd(path).Take(MaxLinesPerFile))
            {
                // Cheap pre-filter: only the lines that mention one of the turn events are parsed; an assistant
                // message that merely quotes one is then discarded by the type check.
                if (!TurnEvents.Any(e => line.Contains(e, StringComparison.Ordinal))) continue;
                if (TurnEventType(line) is not { } type) continue;
                return type == "task_started";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // One locked or vanished rollout must not abort the whole scan.
        }
        return false;
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

    /// <summary>session_meta of a rollout, cached forever: it is the first line of the file and never changes.</summary>
    private RolloutMeta? MetaFor(string path)
    {
        lock (_gate)
            if (_meta.TryGetValue(path, out var cached)) return cached;

        RolloutMeta meta;
        try
        {
            meta = ReadMeta(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            // Not cached: a rollout that could not be read now may well be readable on the next sweep.
            return null;
        }

        lock (_gate) _meta[path] = meta;
        return meta;
    }

    private static RolloutMeta ReadMeta(string path)
    {
        // Codex (Rust std::fs) keeps the live rollout open with share ReadWrite|Delete: open it the same way or the read fails.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        for (var read = 0; read < MetaLines; read++)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta") continue;
                if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) continue;
                return new RolloutMeta(StringOrNull(payload, "session_id"), StringOrNull(payload, "parent_thread_id") ?? StringOrNull(root, "parent_thread_id"));
            }
            catch (JsonException)
            {
                // not JSON: keep looking in the first lines
            }
        }
        return new RolloutMeta(null, null);
    }

    private static string? StringOrNull(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Thread id of a `rollout-&lt;timestamp&gt;-&lt;uuid&gt;.jsonl` whose session_meta carries no session_id.</summary>
    private static string ThreadIdFromFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 5 ? string.Join('-', parts[^5..]) : name;
    }

    private sealed record RolloutMeta(string? ThreadId, string? ParentThreadId);
}
