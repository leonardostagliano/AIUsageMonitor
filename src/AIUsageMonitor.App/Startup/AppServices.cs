using System.IO;
using System.Net.Http;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Settings;
using AIUsageMonitor.Core.Usage;

namespace AIUsageMonitor.App.Startup;

/// <summary>Severity of a user-facing notice; the tray maps it to the balloon icon.</summary>
public enum NoticeKind { Info, Warning, Error }

/// <summary>Composition root: owns every Core service and republishes their events as one StateChanged.</summary>
public sealed class AppServices : IDisposable
{
    private static readonly TimeSpan HookStatusTtl = TimeSpan.FromSeconds(30);

    public AppPaths Paths { get; }
    public IClock Clock { get; }
    public FileLogger Log { get; }
    public SettingsStore Settings { get; }
    public UsageService Usage { get; }
    public UsageScheduler Scheduler { get; }
    public SessionTracker Sessions { get; }
    public HookInstaller Hooks { get; }
    public HookEventPump Pump { get; }

    /// <summary>Raised on a background thread whenever usage or sessions change. Marshal with UiDispatcher.</summary>
    public event Action? StateChanged;

    /// <summary>Raised on the calling thread with a message to surface to the user (rendered as a tray balloon).</summary>
    public event Action<string, string, NoticeKind>? Notice;

    private readonly Dictionary<AgentKind, (HookStatusReport Report, DateTimeOffset At)> _hookStatus = new();
    private readonly object _gate = new();
    private FileSystemWatcher? _codexWatcher;
    private Timer? _codexDebounce;

    private AppServices(AppPaths paths)
    {
        Paths = paths;
        Clock = new SystemClock();
        Log = new FileLogger(paths.LogsDir, Clock);
        Settings = new SettingsStore(paths.SettingsFile);

        var http = new HttpClient();
        Usage = new UsageService(
            [new ClaudeUsageProvider(paths, http, Clock), new CodexUsageProvider(paths, Clock)],
            new UsageCache(paths.UsageCacheFile), Clock) { OnError = ex => Log.Error("UsageService", ex) };
        Scheduler = new UsageScheduler(Usage, agent =>
        {
            var s = Settings.Current;
            return agent switch
            {
                AgentKind.Claude => s.ClaudeEnabled ? TimeSpan.FromSeconds(s.ClaudeRefreshSeconds) : null,
                AgentKind.Codex => s.CodexEnabled ? TimeSpan.FromSeconds(s.CodexRefreshSeconds) : null,
                _ => null
            };
        }) { OnError = ex => Log.Error("UsageScheduler", ex) };

        // Un solo resolver per tutta la vita dell'app: mantiene la cache degli hit e dei miss (60 s).
        var resolver = new CodexSessionResolver(paths.CodexSessionsDir, Clock);
        Sessions = new SessionTracker(Clock, (agent, id) => agent == AgentKind.Codex ? resolver.ResolveCwd(id) : null) { OnError = ex => Log.Error("SessionTracker", ex) };
        Hooks = new HookInstaller(paths, Clock);
        Pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), Sessions, paths, Clock)
        {
            OnError = ex => Log.Error("HookEventPump", ex),
            // Fallback per i thread figli di Codex, che potrebbero non emettere SubagentStart/SubagentStop:
            // lo scanner legge i rollout e la pump li trasforma negli stessi eventi del bridge.
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, Clock)
        };

        Usage.UsageUpdated += _ => StateChanged?.Invoke();
        Sessions.Changed += _ => StateChanged?.Invoke();
        Settings.Changed += _ => { RefreshAll(); StateChanged?.Invoke(); };
    }

    public static AppServices Create() => new(AppPaths.Default);

    public void Start()
    {
        Pump.Start(); // silent replay first, so listeners attached later never see history
        Scheduler.Start([AgentKind.Claude, AgentKind.Codex]);
        if (Directory.Exists(Paths.CodexSessionsDir))
        {
            _codexWatcher = new FileSystemWatcher(Paths.CodexSessionsDir, "*.jsonl") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName, EnableRaisingEvents = true };
            _codexDebounce = new Timer(_ => Scheduler.RefreshNow(AgentKind.Codex), null, Timeout.Infinite, Timeout.Infinite);
            _codexWatcher.Changed += (_, _) => _codexDebounce.Change(2000, Timeout.Infinite);
            _codexWatcher.Created += (_, _) => _codexDebounce.Change(2000, Timeout.Infinite);
        }
    }

    public void RefreshAll()
    {
        Scheduler.RefreshNow(AgentKind.Claude);
        Scheduler.RefreshNow(AgentKind.Codex);
    }

    public HookStatusReport HookStatus(AgentKind agent)
    {
        lock (_gate)
        {
            if (_hookStatus.TryGetValue(agent, out var cached) && Clock.UtcNow - cached.At < HookStatusTtl) return cached.Report;
            HookStatusReport report;
            try
            {
                report = Hooks.GetStatus(agent);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File in uso da Claude Code/Codex mentre riscrivono la config: errore transitorio, non va messo in cache.
                Log.Error($"HookStatus {agent}", ex);
                return new HookStatusReport(Core.Hooks.HookStatus.ConfigInvalid, $"Impossibile leggere la configurazione: {ex.Message}");
            }
            _hookStatus[agent] = (report, Clock.UtcNow);
            return report;
        }
    }

    /// <summary>
    /// Unico punto di installazione hook (menu tray e link nella card del notch): installa, invalida lo stato in cache
    /// e alza sempre un Notice, perche' ogni esito ha un messaggio che l'utente deve vedere. Codex esegue i gruppi solo
    /// dopo l'approvazione (`trusted_hash` in config.toml) e GetStatus torna comunque Installed quando i 4 eventi ci sono:
    /// il Detail contiene gia' "da approvare in Codex con /hooks" (HookInstaller.CodexTrustHint), quindi per Codex si
    /// mostra sempre il Detail. ConfigInvalid non scrive nulla e il Detail porta il percorso del file (spec 11).
    /// Torna null se l'installazione ha sollevato un'eccezione (gia' loggata e notificata).
    /// </summary>
    public HookStatusReport? InstallHooks(AgentKind agent)
    {
        var title = $"{agent.DisplayName()} · hook";
        HookStatusReport report;
        try
        {
            report = Hooks.Install(agent);
        }
        catch (Exception ex)
        {
            Log.Error($"Hook install {agent} failed", ex);
            Notice?.Invoke(title, ex.Message, NoticeKind.Error);
            return null;
        }

        InvalidateHookStatus();
        Log.Info($"Hook install {agent}: {report.Status} {report.Detail}");
        var installed = report.Status == Core.Hooks.HookStatus.Installed;
        var text = installed && agent != AgentKind.Codex ? "Hook installati" : report.Detail;
        Notice?.Invoke(title, text, installed ? NoticeKind.Info : NoticeKind.Warning);
        return report;
    }

    public void InvalidateHookStatus()
    {
        lock (_gate) _hookStatus.Clear();
        StateChanged?.Invoke();
    }

    public IEnumerable<AgentKind> EnabledAgents()
    {
        var s = Settings.Current;
        if (s.ClaudeEnabled) yield return AgentKind.Claude;
        if (s.CodexEnabled) yield return AgentKind.Codex;
    }

    /// <summary>Worst phase across enabled agents (Error > NeedsInput > Working > Idle), null when no session exists.</summary>
    public SessionPhase? WorstPhase()
    {
        SessionPhase? worst = null;
        foreach (var agent in EnabledAgents())
        {
            var phase = Sessions.AggregatePhase(agent);
            if (phase is null) continue;
            if (worst is null || Rank(phase.Value) > Rank(worst.Value)) worst = phase;
        }
        return worst;

        static int Rank(SessionPhase p) => p switch { SessionPhase.Error => 3, SessionPhase.NeedsInput => 2, SessionPhase.Working => 1, _ => 0 };
    }

    /// <summary>"Claude 5h 48% · Codex 7g 90%" for the tray tooltip.</summary>
    public string TooltipSummary()
    {
        var current = Usage.Current;
        var parts = new List<string>();
        foreach (var agent in EnabledAgents())
        {
            if (!current.TryGetValue(agent, out var snap) || snap.Windows.Count == 0) { parts.Add($"{ShortName(agent)} -"); continue; }
            var w = snap.Windows[0];
            parts.Add($"{ShortName(agent)} {w.Label} {w.Percent:0}%");
        }
        return parts.Count == 0 ? "AIUsageMonitor" : string.Join(" · ", parts);

        static string ShortName(AgentKind a) => a == AgentKind.Claude ? "Claude" : "Codex";
    }

    public void Dispose()
    {
        _codexWatcher?.Dispose();
        _codexDebounce?.Dispose();
        Scheduler.Dispose();
        Pump.Dispose();
    }
}
