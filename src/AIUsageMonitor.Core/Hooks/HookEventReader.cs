using System.Text;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

/// <summary>Incrementally tails events.jsonl. Not thread-safe: callers serialize access (HookEventPump does).</summary>
public sealed class HookEventReader
{
    public const long RotateThresholdBytes = 5L * 1024 * 1024;

    private readonly string _path;
    private readonly string _rotatedPath;

    public HookEventReader(string path, string rotatedPath)
    {
        _path = path;
        _rotatedPath = rotatedPath;
    }

    /// <summary>Byte offset of the first line not yet consumed.</summary>
    public long Offset { get; private set; }

    /// <summary>Returns the events of every complete line appended since the previous call.</summary>
    public IReadOnlyList<HookEvent> ReadNew()
    {
        var events = new List<HookEvent>();
        if (!File.Exists(_path))
        {
            Offset = 0;
            return events;
        }

        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < Offset) Offset = 0; // truncated or rotated by someone else
        stream.Seek(Offset, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var text = reader.ReadToEnd();

        int consumed = 0;
        int lineStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var line = text.AsSpan(lineStart, i - lineStart).TrimEnd('\r').ToString();
            lineStart = i + 1;
            consumed = i + 1;
            if (HookEventParser.Parse(line) is { } ev) events.Add(ev);
        }

        Offset += Encoding.UTF8.GetByteCount(text.AsSpan(0, consumed));
        return events;
    }

    /// <summary>Reads the rotated file and the current file from the start (events at or after <paramref name="since"/>), leaving the reader positioned at the end.</summary>
    public IReadOnlyList<HookEvent> ReadAll(DateTimeOffset since)
    {
        var events = new List<HookEvent>();
        if (File.Exists(_rotatedPath))
        {
            foreach (var line in File.ReadLines(_rotatedPath))
                if (HookEventParser.Parse(line) is { } ev && ev.Ts >= since) events.Add(ev);
        }
        Offset = 0;
        events.AddRange(ReadNew().Where(ev => ev.Ts >= since));
        return events;
    }

    /// <summary>Moves the file aside once it grows past the threshold. Call after ReadNew so nothing is lost.</summary>
    public bool RotateIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length < RotateThresholdBytes) return false;
        try
        {
            File.Move(_path, _rotatedPath, overwrite: true);
            Offset = 0;
            return true;
        }
        catch (IOException)
        {
            return false; // the hook is appending right now; try again next time
        }
    }
}
