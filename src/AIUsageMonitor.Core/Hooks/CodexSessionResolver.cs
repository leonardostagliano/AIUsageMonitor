using System.Text.Json;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>Finds the working directory of a Codex session from its rollout file (file name contains the thread id; session_meta holds cwd).</summary>
public sealed class CodexSessionResolver
{
    private readonly string _sessionsDir;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public CodexSessionResolver(string sessionsDir) => _sessionsDir = sessionsDir;

    public string? ResolveCwd(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate)
        {
            if (_cache.TryGetValue(sessionId, out var cached)) return cached;
        }

        string? cwd = null;
        try
        {
            if (Directory.Exists(_sessionsDir))
            {
                var byName = Directory.EnumerateFiles(_sessionsDir, $"*{sessionId}.jsonl", SearchOption.AllDirectories).FirstOrDefault();
                if (byName is not null) cwd = ReadCwd(byName);

                if (cwd is null)
                {
                    var recent = new DirectoryInfo(_sessionsDir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                        .OrderByDescending(f => f.LastWriteTimeUtc).Take(50);
                    foreach (var file in recent)
                    {
                        var (metaSessionId, metaCwd) = ReadMeta(file.FullName);
                        if (string.Equals(metaSessionId, sessionId, StringComparison.OrdinalIgnoreCase)) { cwd = metaCwd; break; }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            cwd = null;
        }

        if (cwd is not null)
        {
            lock (_gate) _cache[sessionId] = cwd;
        }
        return cwd;
    }

    public static string? ReadCwd(string sessionFile) => ReadMeta(sessionFile).Cwd;

    private static (string? SessionId, string? Cwd) ReadMeta(string sessionFile)
    {
        foreach (var line in File.ReadLines(sessionFile).Take(5))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("type", out var type) || type.GetString() != "session_meta") continue;
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
