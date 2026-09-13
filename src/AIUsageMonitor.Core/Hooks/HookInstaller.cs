using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

public enum HookStatus { Installed, Partial, NotInstalled, ConfigMissing, ConfigInvalid }

public sealed record HookStatusReport(HookStatus Status, string Detail);

public sealed record HookRegistration(string Event, string? Matcher);

/// <summary>Idempotently adds/removes our hook entries in ~/.claude/settings.json and ~/.codex/hooks.json.</summary>
public sealed class HookInstaller
{
    public const string Marker = "aiusagemonitor/hook.cjs";
    public const int HookTimeoutSeconds = 5;

    public static readonly IReadOnlyDictionary<AgentKind, IReadOnlyList<HookRegistration>> Registrations =
        new Dictionary<AgentKind, IReadOnlyList<HookRegistration>>
        {
            [AgentKind.Claude] =
            [
                new("SessionStart", null), new("UserPromptSubmit", null), new("Notification", null),
                new("PostToolUse", "AskUserQuestion"), new("Stop", null), new("StopFailure", null), new("SessionEnd", null)
            ],
            [AgentKind.Codex] =
            [
                new("SessionStart", null), new("UserPromptSubmit", null), new("Stop", null), new("SessionEnd", null)
            ]
        };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly AppPaths _paths;
    private readonly IClock _clock;
    private readonly string _nodeCommand;

    public HookInstaller(AppPaths paths, IClock clock, string nodeCommand = "node")
    {
        _paths = paths;
        _clock = clock;
        _nodeCommand = nodeCommand;
    }

    public string ConfigFileFor(AgentKind agent) => agent == AgentKind.Claude ? _paths.ClaudeSettingsFile : _paths.CodexHooksFile;

    public string HookCommandFor(AgentKind agent) => $"{_nodeCommand} \"{_paths.HookScriptFile}\" {agent.Key()}";

    /// <summary>Writes the embedded hook.cjs next to the events file (only when missing or different).</summary>
    public void EnsureHookScript()
    {
        Directory.CreateDirectory(_paths.MonitorDir);
        var path = _paths.HookScriptFile;
        if (!File.Exists(path) || File.ReadAllText(path) != HookScript.Content)
            File.WriteAllText(path, HookScript.Content);
    }

    public HookStatusReport GetStatus(AgentKind agent)
    {
        var file = ConfigFileFor(agent);
        if (!File.Exists(file)) return new(HookStatus.ConfigMissing, $"File non trovato: {file}");

        JsonObject root;
        try { root = ParseRoot(file); }
        catch (JsonException ex) { return new(HookStatus.ConfigInvalid, $"JSON non valido in {file}: {ex.Message}"); }

        var hooks = root["hooks"] as JsonObject;
        var expected = Registrations[agent];
        var present = expected.Count(r => HasOurHook(hooks?[r.Event] as JsonArray));
        var detail = $"{present}/{expected.Count} eventi";
        if (agent == AgentKind.Codex && CodexHooksDisabled())
            detail += " (config.toml ha hooks = false: gli hook Codex sono disattivati)";

        var status = present == 0 ? HookStatus.NotInstalled : present == expected.Count ? HookStatus.Installed : HookStatus.Partial;
        return new(status, detail);
    }

    public HookStatusReport Install(AgentKind agent)
    {
        EnsureHookScript();
        var file = ConfigFileFor(agent);

        JsonObject root;
        if (File.Exists(file))
        {
            try { root = ParseRoot(file); }
            catch (JsonException ex) { return new(HookStatus.ConfigInvalid, $"Nessuna modifica: JSON non valido in {file} ({ex.Message})"); }
        }
        else
        {
            root = new JsonObject();
        }

        if (root["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        var command = HookCommandFor(agent);
        var changed = false;
        foreach (var registration in Registrations[agent])
        {
            if (hooks[registration.Event] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[registration.Event] = groups;
            }
            if (HasOurHook(groups)) continue;

            var group = new JsonObject();
            if (registration.Matcher is not null) group["matcher"] = registration.Matcher;
            group["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["timeout"] = HookTimeoutSeconds
            });
            groups.Add(group);
            changed = true;
        }

        if (changed) WriteWithBackup(file, root);
        return GetStatus(agent);
    }

    public HookStatusReport Remove(AgentKind agent)
    {
        var file = ConfigFileFor(agent);
        if (!File.Exists(file)) return new(HookStatus.ConfigMissing, $"File non trovato: {file}");

        JsonObject root;
        try { root = ParseRoot(file); }
        catch (JsonException ex) { return new(HookStatus.ConfigInvalid, $"Nessuna modifica: JSON non valido in {file} ({ex.Message})"); }

        if (root["hooks"] is not JsonObject hooks) return GetStatus(agent);

        var changed = false;
        foreach (var eventName in hooks.Select(kv => kv.Key).ToList())
        {
            if (hooks[eventName] is not JsonArray groups) continue;
            var removedFromEvent = false;
            for (var g = groups.Count - 1; g >= 0; g--)
            {
                if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray commands) continue;
                var removedFromGroup = false;
                for (var c = commands.Count - 1; c >= 0; c--)
                {
                    if (!IsOurs(commands[c])) continue;
                    commands.RemoveAt(c);
                    removedFromGroup = true;
                    removedFromEvent = true;
                    changed = true;
                }
                // Only prune what this call emptied: never touch groups that were already empty.
                if (removedFromGroup && commands.Count == 0)
                {
                    groups.RemoveAt(g);
                    changed = true;
                }
            }
            // Same for the event key: drop it only if we emptied it ourselves.
            if (removedFromEvent && groups.Count == 0)
            {
                hooks.Remove(eventName);
                changed = true;
            }
        }

        if (changed) WriteWithBackup(file, root);
        return GetStatus(agent);
    }

    private static JsonObject ParseRoot(string file) =>
        JsonNode.Parse(File.ReadAllText(file), documentOptions: ReadOptions) as JsonObject
        ?? throw new JsonException("root is not an object");

    private static bool HasOurHook(JsonArray? groups) =>
        groups is not null && groups.OfType<JsonObject>().Any(g => g["hooks"] is JsonArray commands && commands.Any(IsOurs));

    private static bool IsOurs(JsonNode? command) =>
        command is JsonObject obj
        && obj["command"] is JsonValue value
        && value.TryGetValue<string>(out var text)
        && text.Replace('\\', '/').Contains(Marker, StringComparison.OrdinalIgnoreCase);

    private void WriteWithBackup(string file, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        if (File.Exists(file))
        {
            Directory.CreateDirectory(_paths.BackupsDir);
            var stamp = _clock.UtcNow.ToLocalTime().ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(_paths.BackupsDir, $"{Path.GetFileName(file)}.{stamp}.bak");
            for (var n = 1; File.Exists(backup); n++)
                backup = Path.Combine(_paths.BackupsDir, $"{Path.GetFileName(file)}.{stamp}-{n}.bak");
            File.Copy(file, backup);
        }
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(tmp, file, overwrite: true);
    }

    private bool CodexHooksDisabled()
    {
        try
        {
            return File.Exists(_paths.CodexConfigFile)
                && File.ReadLines(_paths.CodexConfigFile).Any(l => Regex.IsMatch(l, @"^\s*hooks\s*=\s*false\s*(#.*)?$"));
        }
        catch (IOException)
        {
            return false;
        }
    }
}
