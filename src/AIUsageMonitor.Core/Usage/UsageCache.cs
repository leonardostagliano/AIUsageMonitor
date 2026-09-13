using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Usage;

/// <summary>Last good snapshot per agent, persisted so the UI has values right after startup.</summary>
public sealed class UsageCache
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;

    public UsageCache(string path) => _path = path;

    public Dictionary<AgentKind, UsageSnapshot> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new Dictionary<AgentKind, UsageSnapshot>();
            var list = JsonSerializer.Deserialize<List<UsageSnapshot>>(File.ReadAllText(_path), Options) ?? new List<UsageSnapshot>();
            return list.GroupBy(s => s.Agent).ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Dictionary<AgentKind, UsageSnapshot>();
        }
    }

    public void Save(IEnumerable<UsageSnapshot> snapshots)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshots.ToList(), Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
