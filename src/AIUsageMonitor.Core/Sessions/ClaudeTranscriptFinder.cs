using System.Text;
using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Core.Sessions;

/// <summary>
/// Finds <c>~/.claude/projects/&lt;project&gt;/&lt;session&gt;.jsonl</c> for a session known only from the registry (its hooks
/// deliver the path; the registry does not). The project folder is the cwd with every character that is not a letter
/// or a digit turned into '-', which is tried first; a very long cwd gets another name, so a miss falls back to
/// looking for the file name in every project folder.
/// </summary>
/// <remarks>Hits are cached for good, misses for <see cref="MissTtl"/>: the file appears only with the first prompt.</remarks>
public sealed class ClaudeTranscriptFinder
{
    public static readonly TimeSpan MissTtl = TimeSpan.FromSeconds(30);

    private const int MaxProjectDirs = 5000;

    private readonly string _projectsDir;
    private readonly IClock _clock;
    private readonly Dictionary<string, string> _hits = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _misses = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public ClaudeTranscriptFinder(string projectsDir, IClock clock)
    {
        _projectsDir = projectsDir;
        _clock = clock;
    }

    /// <summary>The project folder name Claude Code derives from a cwd.</summary>
    public static string ProjectDirName(string cwd)
    {
        var name = new StringBuilder(cwd.Length);
        foreach (var c in cwd) name.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
        return name.ToString();
    }

    public string? Find(string? cwd, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.IndexOfAny(['/', '\\', '*', '?', ':']) >= 0) return null;
        var now = _clock.UtcNow;
        lock (_gate)
        {
            if (_hits.TryGetValue(sessionId, out var hit)) return hit;
            if (_misses.TryGetValue(sessionId, out var at) && now - at < MissTtl) return null;
        }

        var found = Search(cwd, sessionId);
        lock (_gate)
        {
            if (found is null) _misses[sessionId] = now;
            else
            {
                _hits[sessionId] = found;
                _misses.Remove(sessionId);
            }
        }
        return found;
    }

    private string? Search(string? cwd, string sessionId)
    {
        var fileName = sessionId + ".jsonl";
        try
        {
            if (!Directory.Exists(_projectsDir)) return null;
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                var direct = Path.Combine(_projectsDir, ProjectDirName(cwd), fileName);
                if (File.Exists(direct)) return direct;
            }
            foreach (var dir in Directory.EnumerateDirectories(_projectsDir).Take(MaxProjectDirs))
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A miss: the next sync tries again.
        }
        return null;
    }
}
