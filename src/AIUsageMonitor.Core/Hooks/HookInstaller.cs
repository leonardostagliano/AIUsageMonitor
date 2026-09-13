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

    /// <summary>Codex runs a hook group only after the user approves it (trusted_hash in config.toml), so the report says so.</summary>
    public const string CodexTrustHint = "da approvare in Codex con /hooks";

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
        catch (Exception ex) when (IsBadJson(ex)) { return new(HookStatus.ConfigInvalid, $"JSON non valido in {file}: {ex.Message}"); }

        var hooks = root["hooks"] as JsonObject;
        var expected = Registrations[agent];
        var present = expected.Count(r => HasOurHook(hooks?[r.Event] as JsonArray));
        var detail = $"{present}/{expected.Count} eventi";
        if (agent == AgentKind.Codex && CodexHooksDisabled())
            detail += " (config.toml ha hooks = false: gli hook Codex sono disattivati)";
        if (agent == AgentKind.Codex && present > 0 && !CodexGroupsTrusted(hooks))
            detail += $" ({CodexTrustHint})";

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
            catch (Exception ex) when (IsBadJson(ex)) { return new(HookStatus.ConfigInvalid, $"Nessuna modifica: JSON non valido in {file} ({ex.Message})"); }
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

        var report = GetStatus(agent);
        // Codex ignores a hook group until the user approves it in the native /hooks UI, so the installer always says it.
        if (agent == AgentKind.Codex && !report.Detail.Contains(CodexTrustHint, StringComparison.OrdinalIgnoreCase))
            report = report with { Detail = $"{report.Detail} ({CodexTrustHint})" };
        return report;
    }

    /// <summary>Removes only our own entries. Caveat (Codex): its trust keys are positional
    /// (<c>hooks.json:&lt;event&gt;:&lt;group&gt;:&lt;index&gt;</c>), so removing our group shifts the index of any group the user added
    /// after ours and invalidates that group's trusted_hash; Install always appends ours last to keep existing keys valid.</summary>
    public HookStatusReport Remove(AgentKind agent)
    {
        var file = ConfigFileFor(agent);
        if (!File.Exists(file)) return new(HookStatus.ConfigMissing, $"File non trovato: {file}");

        JsonObject root;
        try { root = ParseRoot(file); }
        catch (Exception ex) when (IsBadJson(ex)) { return new(HookStatus.ConfigInvalid, $"Nessuna modifica: JSON non valido in {file} ({ex.Message})"); }

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

    private static JsonObject ParseRoot(string file)
    {
        var root = JsonNode.Parse(File.ReadAllText(file), documentOptions: ReadOptions) as JsonObject
            ?? throw new JsonException("root is not an object");
        // JsonNode materializes objects lazily: a duplicated key only throws at the first indexer access, which may be
        // half way through an Install. Force it here so a hand-edited file is rejected before anything is written.
        Materialize(root);
        return root;
    }

    private static void Materialize(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (_, value) in obj) Materialize(value);
                break;
            case JsonArray array:
                foreach (var item in array) Materialize(item);
                break;
        }
    }

    /// <summary>A duplicated key surfaces as ArgumentException, a wrong node shape as InvalidOperationException.</summary>
    private static bool IsBadJson(Exception ex) => ex is JsonException or ArgumentException or InvalidOperationException;

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

    /// <summary>True when every group of ours already carries a trusted_hash in config.toml (Codex only runs approved hooks).</summary>
    private bool CodexGroupsTrusted(JsonObject? hooks)
    {
        var trusted = CodexTrustedStateKeys();
        var file = NormalizeSeparators(_paths.CodexHooksFile);
        foreach (var registration in Registrations[AgentKind.Codex])
        {
            if (hooks?[registration.Event] is not JsonArray groups) continue;
            for (var g = 0; g < groups.Count; g++)
            {
                if (groups[g] is not JsonObject group || group["hooks"] is not JsonArray commands || !commands.Any(IsOurs)) continue;
                var prefix = $"{file}:{registration.Event}:{g}:";
                if (!trusted.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) return false;
            }
        }
        return true;
    }

    /// <summary>Keys of the <c>[hooks.state.'&lt;file&gt;:&lt;event&gt;:&lt;group&gt;:&lt;index&gt;']</c> sections of config.toml that have a trusted_hash.</summary>
    private HashSet<string> CodexTrustedStateKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_paths.CodexConfigFile)) return keys;
            string? current = null;
            foreach (var raw in File.ReadLines(_paths.CodexConfigFile))
            {
                var line = raw.Trim();
                if (line.StartsWith('['))
                {
                    var match = Regex.Match(line, @"^\[\s*hooks\s*\.\s*state\s*\.\s*(?<q>['""])(?<key>.*)\k<q>\s*\]$");
                    current = match.Success ? NormalizeSeparators(match.Groups["key"].Value) : null;
                    continue;
                }
                if (current is not null && Regex.IsMatch(line, @"^trusted_hash\s*=")) keys.Add(current);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable config.toml: treat the hooks as not yet approved
        }
        return keys;
    }

    private static string NormalizeSeparators(string value) => value.Replace('\\', '/');

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
