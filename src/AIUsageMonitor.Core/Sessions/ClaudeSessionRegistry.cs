using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageMonitor.Core.Sessions;

/// <summary>
/// One record of the session registry Claude Code keeps in <c>~/.claude/sessions/&lt;pid&gt;.json</c>: every Claude Code
/// process writes its own at start-up — interactive CLI, <c>-p</c>, the SDK the desktop app runs — keeps its
/// <c>status</c> current and deletes it when it exits cleanly. A process that is killed leaves its record behind.
/// </summary>
/// <param name="StartedAt">When the process registered (Unix ms, 0 when unknown): a recycled pid was created after it.</param>
/// <param name="Status">"busy", "shell", "idle" or "waiting" (for a permission or an answer, see <paramref name="WaitingFor"/>).</param>
/// <param name="Kind">"interactive" (also for <c>-p</c> and the SDK), "bg", "daemon" or "daemon-worker".</param>
/// <param name="Entrypoint">Claude Code's <c>CLAUDE_CODE_ENTRYPOINT</c>: "cli", "claude-desktop", "sdk-ts"...</param>
public sealed record ClaudeSessionRecord(
    int Pid,
    string? SessionId,
    string? Cwd,
    long StartedAt,
    string? Kind,
    string? Entrypoint,
    string? Status,
    string? WaitingFor,
    string? Name,
    long? StatusUpdatedAt,
    string? BridgeSessionId)
{
    /// <summary>The supervisor of background sessions is not a conversation of its own.</summary>
    public bool IsConversation => !string.IsNullOrEmpty(SessionId) && Kind is not ("daemon" or "daemon-worker");
}

/// <summary>Reads <c>~/.claude/sessions</c>. Never throws: a torn or unreadable record is skipped until the next read.</summary>
public sealed class ClaudeSessionRegistryReader
{
    private static readonly Regex RecordName = new(@"^(\d+)\.json$", RegexOptions.CultureInvariant);

    /// <summary>A record is a few hundred bytes; anything much larger is not one.</summary>
    private const long MaxRecordBytes = 256 * 1024;

    private readonly string _directory;

    public ClaudeSessionRegistryReader(string directory) => _directory = directory;

    public IReadOnlyList<ClaudeSessionRecord> Read()
    {
        var records = new List<ClaudeSessionRecord>();
        IEnumerable<string> files;
        try
        {
            if (!Directory.Exists(_directory)) return records;
            files = Directory.EnumerateFiles(_directory, "*.json").ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return records;
        }

        foreach (var file in files)
        {
            var match = RecordName.Match(Path.GetFileName(file));
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var pid) || pid <= 0) continue;
            if (TryRead(file, pid) is { } record) records.Add(record);
        }
        return records;
    }

    private static ClaudeSessionRecord? TryRead(string file, int pid)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxRecordBytes) return null;
            // Claude Code rewrites the record in place: open it the way a concurrent writer allows.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            // The file name is the authority: Claude Code itself ignores a record whose name is not its pid.
            if (Number(root, "pid") is { } inside && inside != pid) return null;
            return new ClaudeSessionRecord(
                pid,
                Text(root, "sessionId"),
                Text(root, "cwd"),
                Number(root, "startedAt") ?? 0,
                Text(root, "kind"),
                Text(root, "entrypoint"),
                Text(root, "status"),
                Text(root, "waitingFor"),
                Text(root, "name"),
                Number(root, "statusUpdatedAt"),
                Text(root, "bridgeSessionId"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s ? s : null;

    private static long? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) && v >= 0 ? v : null;
}
