using System.Globalization;

namespace AIUsageMonitor.Core.Infrastructure;

/// <summary>Tiny file logger: app-yyyyMMdd.log in the logs dir, older files pruned at startup. Never logs secrets.</summary>
public sealed class FileLogger
{
    private readonly string _dir;
    private readonly IClock _clock;
    private readonly object _gate = new();

    public FileLogger(string dir, IClock clock, int retainDays = 7)
    {
        _dir = dir;
        _clock = clock;
        Directory.CreateDirectory(dir);
        Prune(retainDays);
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message, Exception? exception = null) => Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private void Write(string level, string message)
    {
        var now = _clock.UtcNow.ToLocalTime();
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(Path.Combine(_dir, $"app-{now:yyyyMMdd}.log"), line); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void Prune(int retainDays)
    {
        var cutoff = _clock.UtcNow.ToLocalTime().Date.AddDays(-retainDays);
        foreach (var file in Directory.EnumerateFiles(_dir, "app-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["app-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && day < cutoff)
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
