using System.IO;
using System.Net.Http;
using AIUsageMonitor.App.Terminal;
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
    public TerminalRegistry Terminals { get; }

    /// <summary>Raised on a background thread whenever usage or sessions change. Marshal with UiDispatcher.</summary>
    public event Action? StateChanged;

    /// <summary>Raised on the calling thread with a message to surface to the user (rendered as a tray balloon).</summary>
    public event Action<string, string, NoticeKind>? Notice;

    private readonly Dictionary<AgentKind, (HookStatusReport Report, DateTimeOffset At)> _hookStatus = new();
    private readonly object _gate = new();
    private readonly TerminalFocuser _focuser;
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
        var tokens = new AppTokenSource(paths, Clock);
        Pump = new HookEventPump(new HookEventReader(paths.EventsFile, paths.RotatedEventsFile), Sessions, paths, Clock)
        {
            OnError = ex => Log.Error("HookEventPump", ex),
            // Fallback per i thread figli di Codex finche' i suoi hook SubagentStart/SubagentStop non sono attivi
            // (i gruppi vanno approvati in Codex con /hooks): lo scanner legge i rollout e la pump li trasforma
            // negli stessi eventi del bridge. La pump lo spegne da sola per le sessioni Codex i cui hook riportano
            // i subagenti - altrimenti lo stesso figlio verrebbe contato due volte - e l'opzione lo disattiva del
            // tutto; il predicato viene riletto a ogni scansione, quindi il cambio non richiede un riavvio.
            CodexSubagents = new CodexSubagentScanner(paths.CodexSessionsDir, Clock),
            CodexSubagentsEnabled = () => Settings.Current.CodexSubagentFallback,
            // Totali dei token di sessione e subagenti: tutta la sua IO gira sul thread della pump.
            TokenSource = tokens
        };

        // "Vai al terminale": il pane di Herdr e la catena dei processi vanno risolti quando l'evento arriva, perche'
        // il processo che ha eseguito l'hook vive pochi secondi mentre la finestra del terminale resta.
        var herdr = new HerdrClient { OnLog = Log.Info };
        Terminals = new TerminalRegistry(Clock) { OnLog = Log.Info };
        _focuser = new TerminalFocuser(herdr, Terminals, Log.Info);

        Usage.UsageUpdated += _ => StateChanged?.Invoke();
        // Removed arriva dallo sweep della pump, cioe' dallo stesso thread che chiama il token source: e' il punto
        // giusto per liberare l'offset e i requestId del transcript di una sessione che non esiste piu'.
        Sessions.Changed += change =>
        {
            if (change.Kind == SessionChangeKind.Removed)
            {
                tokens.Forget(change.Session);
                Terminals.Forget(change.Session.Agent, change.Session.SessionId);
            }
            else Terminals.Observe(change.Session);
            StateChanged?.Invoke();
        };
        Settings.Changed += _ => { RefreshAll(); StateChanged?.Invoke(); };
    }

    public static AppServices Create() => new(AppPaths.Default);

    public void Start()
    {
        // "Vai al terminale" ha bisogno del campo host che l'hook scrive in SessionStart/UserPromptSubmit: se gli hook
        // sono gia' installati per almeno un agente, riallinea lo script imbarcato a ogni avvio (EnsureHookScript
        // riscrive hook.cjs solo quando il contenuto e' cambiato), cosi' un aggiornamento dell'app arriva anche a chi
        // aveva gia' installato gli hook con una versione precedente, senza far comparire hook.cjs dal nulla a chi non
        // li ha mai installati.
        // Come per il replay della pump qui sotto, un file bloccato o non scrivibile non deve mai abortire l'avvio:
        // il riallineamento e' manutenzione opzionale, quindi un IOException (antivirus, profilo in sync, attributo
        // di sola lettura) degrada a "hook.cjs resta alla versione precedente" e viene ritentato al prossimo avvio.
        try
        {
            if (new[] { AgentKind.Claude, AgentKind.Codex }.Any(a => HookStatus(a).Status is Core.Hooks.HookStatus.Installed or Core.Hooks.HookStatus.Partial)
                && Hooks.EnsureHookScript())
                Log.Info("hook.cjs aggiornato all'avvio (nuova versione dell'app)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("hook.cjs non aggiornabile all'avvio", ex);
        }

        Pump.Start(); // silent replay first, so listeners attached later never see history
        // Il replay ricostruisce le sessioni con ApplySilently, che per definizione non alza Changed: senza questo
        // giro il registro resterebbe vuoto a ogni avvio (autostart compreso) per tutte le sessioni gia' esistenti,
        // e il click su quelle righe perderebbe pane, WT_SESSION e VSCODE_PID finche' la sessione non emette un nuovo
        // evento. Observe torna subito quando Host e' null e fa la risalita su Task.Run, quindi non rallenta Start().
        // ppidIsFresh: false perche' quegli host arrivano dallo storico, non da un evento appena letto.
        foreach (var session in Sessions.Sessions) Terminals.Observe(session, ppidIsFresh: false);
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

    /// <summary>
    /// Porta in primo piano il terminale della sessione. Gira tutta su un thread di background (CLI di Herdr e
    /// scansione dei processi) e non solleva mai: torna false quando nessuna strategia ha funzionato, e in quel caso
    /// il chiamante mostra il toast "Terminale non trovato".
    /// </summary>
    public Task<bool> FocusTerminalAsync(SessionState session) => _focuser.FocusAsync(session);

    /// <summary>
    /// Alza un <see cref="Notice"/> per conto di chi non possiede l'icona della tray (i ViewModel del notch): il
    /// renderer resta uno solo, <c>TrayIconController</c>, che lo mostra come balloon con l'icona della severita'.
    /// </summary>
    public void Notify(string title, string text, NoticeKind kind) => Notice?.Invoke(title, text, kind);

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
