namespace AIUsageMonitor.Core.Infrastructure;

/// <summary>Every path the app reads or writes. Tests build one on a temp directory.</summary>
public sealed class AppPaths
{
    public AppPaths(string homeDir, string localAppDataDir)
    {
        HomeDir = homeDir;
        LocalAppDataDir = localAppDataDir;
    }

    public static AppPaths Default { get; } = new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUsageMonitor"));

    public string HomeDir { get; }
    public string LocalAppDataDir { get; }

    // Agents (read-only for us, except the hook config files)
    public string ClaudeCredentialsFile => Path.Combine(HomeDir, ".claude", ".credentials.json");
    public string ClaudeSettingsFile => Path.Combine(HomeDir, ".claude", "settings.json");
    /// <summary>Claude Code's registry of running sessions, one <c>&lt;pid&gt;.json</c> per process.</summary>
    public string ClaudeSessionsDir => Path.Combine(HomeDir, ".claude", "sessions");
    public string ClaudeProjectsDir => Path.Combine(HomeDir, ".claude", "projects");
    /// <summary>Claude Code's global config: the organization of the logged-in account, for the cloud sessions API.</summary>
    public string ClaudeGlobalConfigFile => Path.Combine(HomeDir, ".claude.json");
    public string CodexSessionsDir => Path.Combine(HomeDir, ".codex", "sessions");
    public string CodexHooksFile => Path.Combine(HomeDir, ".codex", "hooks.json");
    public string CodexConfigFile => Path.Combine(HomeDir, ".codex", "config.toml");

    // Hook bridge
    public string MonitorDir => Path.Combine(HomeDir, ".aiusagemonitor");
    public string EventsFile => Path.Combine(MonitorDir, "events.jsonl");
    public string RotatedEventsFile => Path.Combine(MonitorDir, "events.1.jsonl");
    public string HookScriptFile => Path.Combine(MonitorDir, "hook.cjs");
    public string BackupsDir => Path.Combine(MonitorDir, "backups");

    // App data
    public string SettingsFile => Path.Combine(LocalAppDataDir, "settings.json");
    public string UsageCacheFile => Path.Combine(LocalAppDataDir, "usage-cache.json");
    public string LogsDir => Path.Combine(LocalAppDataDir, "logs");

    /// <summary>Which process runs each session, to end the sessions whose terminal was closed (SessionProcessRegistry).</summary>
    public string SessionProcessesFile => Path.Combine(LocalAppDataDir, "session-processes.json");

    // Costi: listino prezzi scaricato, override manuale, tasso BCE
    public string PricesCacheFile => Path.Combine(LocalAppDataDir, "prices-cache.json");
    public string PricesOverrideFile => Path.Combine(LocalAppDataDir, "prices-override.json");
    public string ExchangeRateFile => Path.Combine(LocalAppDataDir, "exchange-rate.json");

    // Aggiornamenti
    public string UpdatesDir => Path.Combine(LocalAppDataDir, "updates");
    public string UpdateAuthFile => Path.Combine(LocalAppDataDir, "updates-auth.json");
}
