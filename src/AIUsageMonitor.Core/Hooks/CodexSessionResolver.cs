using System.Text;
using System.Text.Json;
using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>Finds the working directory of a Codex session from its rollout file (file name contains the thread id; session_meta holds cwd).</summary>
/// <remarks>Build one instance per app lifetime: it caches hits forever and misses for <see cref="MissTtl"/>, so a session that cannot be
/// resolved does not re-enumerate ~/.codex/sessions on every hook event.</remarks>
public sealed class CodexSessionResolver
{
    /// <summary>How long a failed lookup is remembered before the directory is scanned again.</summary>
    public static readonly TimeSpan MissTtl = TimeSpan.FromSeconds(60);

    private const int MaxScannedFiles = 50;
    private const int MetaLines = 5;

    private readonly string _sessionsDir;
    private readonly IClock _clock;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _misses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CodexSessionResolver(string sessionsDir, IClock? clock = null)
    {
        _sessionsDir = sessionsDir;
        _clock = clock ?? new SystemClock();
    }

    public string? ResolveCwd(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_cache.TryGetValue(sessionId, out var cached)) return cached;
            if (_misses.TryGetValue(sessionId, out var missedAt) && now - missedAt < MissTtl) return null;
        }

        var cwd = Scan(sessionId);

        lock (_gate)
        {
            if (cwd is not null)
            {
                _cache[sessionId] = cwd;
                _misses.Remove(sessionId);
            }
            else
            {
                _misses[sessionId] = now;
            }
        }
        return cwd;
    }

    private string? Scan(string sessionId)
    {
        try
        {
            if (!Directory.Exists(_sessionsDir)) return null;

            var byName = Directory.EnumerateFiles(_sessionsDir, $"*{sessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
            if (byName is not null && TryReadMeta(byName, out var named) && named.Cwd is not null) return named.Cwd;

            var recent = new DirectoryInfo(_sessionsDir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxScannedFiles);
            foreach (var file in recent)
            {
                // Per file: one unreadable rollout (a live session, an AV lock) must not abort the whole scan.
                if (!TryReadMeta(file.FullName, out var meta)) continue;
                if (string.Equals(meta.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return meta.Cwd;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException
                                      or JsonException or InvalidOperationException)
        {
            // Degrade to null: the caller falls back to the session id as display name.
        }
        return null;
    }

    public static string? ReadCwd(string sessionFile) => TryReadMeta(sessionFile, out var meta) ? meta.Cwd : null;

    private static bool TryReadMeta(string sessionFile, out (string? SessionId, string? Cwd) meta)
    {
        meta = (null, null);
        try
        {
            meta = ReadMeta(sessionFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static (string? SessionId, string? Cwd) ReadMeta(string sessionFile)
    {
        // Codex (Rust std::fs) keeps the live rollout file open with share ReadWrite|Delete: open it the same way or the read fails.
        using var stream = new FileStream(sessionFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
                var cwd = payload.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var sid = payload.TryGetProperty("session_id", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                return (sid, cwd);
            }
            catch (JsonException)
            {
                // not JSON: keep looking in the first lines
            }
        }
        return (null, null);
    }
}
