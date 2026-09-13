using System.Text.Json;

namespace AIUsageMonitor.Core.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public event Action<AppSettings>? Changed;

    public AppSettings Current { get; private set; }

    public SettingsStore(string path)
    {
        _path = path;
        Current = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), Options) ?? new AppSettings()).Normalized();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // fall through to defaults
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        var normalized = settings.Clone().Normalized();
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(normalized, Options));
        File.Move(tmp, _path, overwrite: true);
        Current = normalized;
        Changed?.Invoke(normalized);
    }
}
