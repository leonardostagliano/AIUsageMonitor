using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Terminal;

/// <summary>
/// Dove vive una sessione: pane di Herdr, pty di wmux, processo dell'agente, processo che possiede la finestra,
/// indizi d'ambiente.
/// Accanto a ogni pid si tiene il nome dell'immagine vista al momento della risoluzione: chiudere un terminale non
/// emette <c>SessionEnd</c>, quindi il target sopravvive fino allo sweep (12 ore) e nel frattempo Windows puo' aver
/// riciclato quel pid per un altro processo. Il nome e' il modo piu' economico per accorgersene prima di attivare
/// una finestra che non c'entra nulla.
/// </summary>
public sealed record TerminalTarget(string? HerdrPane, int? AgentPid, string? AgentPidName, int? WindowPid, string? WindowPidName, string? WtSession, int? VscodePid, DateTimeOffset ResolvedAt, string? WmuxPty = null, string? TermProgram = null);

/// <summary>
/// Tiene, per sessione, il terminale che la ospita. La risoluzione va fatta quando l'evento arriva, non al click:
/// l'hook gira dentro un wrapper effimero (<c>HostInfo.Ppid</c> muore in pochi secondi) mentre la finestra del
/// terminale resta viva, quindi la catena dei processi si puo' risalire solo finche' il ppid esiste ancora.
/// La risalita e' IO di sistema e viene fatta su un thread di background; il dizionario e' protetto da un lock.
/// </summary>
public sealed class TerminalRegistry
{
    private static readonly string[] AgentProcessNames = ["claude", "codex", "node"];

    private readonly IClock _clock;
    private readonly Dictionary<(AgentKind Agent, string SessionId), TerminalTarget> _targets = new();
    private readonly Dictionary<(AgentKind Agent, string SessionId), HostInfo> _resolved = new();
    private readonly HashSet<(AgentKind Agent, string SessionId)> _pending = [];
    private readonly object _gate = new();

    /// <summary>Chiamato con una riga di log a ogni risoluzione; lo imposta AppServices sul FileLogger.</summary>
    public Action<string>? OnLog { get; init; }

    public TerminalRegistry(IClock clock) => _clock = clock;

    public TerminalTarget? Get(AgentKind agent, string sessionId)
    {
        lock (_gate) return _targets.TryGetValue((agent, sessionId), out var target) ? target : null;
    }

    /// <summary>
    /// Da chiamare a ogni cambiamento di sessione. Non fa nulla se la sessione non porta un host o se quell'host e'
    /// gia' stato risolto: la risalita parte solo quando l'informazione cambia davvero (nuova sessione, nuovo pane).
    /// <paramref name="ppidIsFresh"/> va messo a false quando l'host non arriva da un evento appena ricevuto ma dal
    /// replay dello storico (seed all'avvio): in quel caso il ppid puo' essere stato riciclato da Windows e la catena
    /// dei processi viene accettata solo se contiene davvero un processo agente (vedi <see cref="Resolve"/>).
    /// </summary>
    public void Observe(SessionState session, bool ppidIsFresh = true)
    {
        if (session.Host is not { } host) return;
        var key = (session.Agent, session.SessionId);
        lock (_gate)
        {
            if (_resolved.TryGetValue(key, out var previous) && previous == host) return;
            if (!_pending.Add(key)) return;
            _resolved[key] = host;
        }
        // Fire-and-forget: la risalita non deve rallentare la pump degli eventi ne' il thread della UI.
        _ = Task.Run(() => Resolve(key, host, ppidIsFresh));
    }

    public void Forget(AgentKind agent, string sessionId)
    {
        lock (_gate)
        {
            _targets.Remove((agent, sessionId));
            _resolved.Remove((agent, sessionId));
        }
    }

    private void Resolve((AgentKind Agent, string SessionId) key, HostInfo host, bool ppidIsFresh)
    {
        try
        {
            int? agentPid = null;
            string? agentPidName = null;
            int? windowPid = null;
            string? windowPidName = null;
            if (host.Ppid is { } ppid && ppid > 0)
            {
                var chain = ProcessTree.Ancestors(ppid);
                foreach (var node in chain)
                {
                    // La shell e i processi di sistema chiudono la catena: explorer.exe possiede la finestra del
                    // desktop e la scambieremmo per il terminale della sessione (vedi ProcessTree.IsShellOrSystem).
                    if (ProcessTree.IsShellOrSystem(node)) break;
                    if (agentPid is null && IsAgentProcess(node.Name)) (agentPid, agentPidName) = (node.Pid, node.Name);
                    if (windowPid is null && WindowActivator.FindTopLevelWindow(node.Pid) != IntPtr.Zero) (windowPid, windowPidName) = (node.Pid, node.Name);
                    if (agentPid is not null && windowPid is not null) break;
                }
                // Seed dopo il replay: quel ppid e' stato registrato fino a 24 ore fa. Se il processo non c'e' piu'
                // la catena e' vuota e non si perde nulla; se invece Windows ha riciclato il pid la catena e' quella
                // di un processo estraneo e attiveremmo la finestra sbagliata. Sopra un vero ppid dell'hook c'e'
                // sempre l'agente: senza claude/codex/node la risalita non e' attendibile e si tengono solo gli
                // indizi d'ambiente (pane di Herdr, WT_SESSION, VSCODE_PID), che non dipendono da un pid vivo.
                if (!ppidIsFresh && agentPid is null && windowPid is not null)
                {
                    OnLog?.Invoke($"Terminal resolve {key.Agent} {key.SessionId}: ppid {ppid} non porta a un agente (pid riciclato dopo il replay), finestra {windowPid} ({windowPidName}) ignorata");
                    (windowPid, windowPidName) = (null, null);
                }
                OnLog?.Invoke($"Terminal resolve {key.Agent} {key.SessionId}: ppid {ppid} → {chain.Count} antenati, agent {agentPid?.ToString() ?? "-"}, finestra {windowPid?.ToString() ?? "-"} ({windowPidName ?? "-"}), pane {host.HerdrPane ?? "-"}, wmux {host.WmuxPty ?? "-"}");
            }
            else
            {
                OnLog?.Invoke($"Terminal resolve {key.Agent} {key.SessionId}: nessun ppid, pane {host.HerdrPane ?? "-"}, wmux {host.WmuxPty ?? "-"}");
            }

            var target = new TerminalTarget(host.HerdrPane, agentPid, agentPidName, windowPid, windowPidName, host.WtSession, host.VscodePid, _clock.UtcNow, host.WmuxPty, host.TermProgram);
            lock (_gate) _targets[key] = target;
        }
        catch (Exception ex)
        {
            // La risoluzione e' opportunistica: un errore qui non deve mai buttare giu' il thread pool.
            OnLog?.Invoke($"Terminal resolve {key.Agent} {key.SessionId} fallita: {ex.Message}");
            lock (_gate) _resolved.Remove(key);
        }
        finally
        {
            lock (_gate) _pending.Remove(key);
        }
    }

    /// <summary>Nome di un processo agente (claude/codex/node). Lo riusa <see cref="TerminalFocuser"/> per riconoscere un pid riciclato.</summary>
    internal static bool IsAgentProcess(string name) =>
        AgentProcessNames.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
