using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Terminal;

/// <summary>Dove vive una sessione: pane di Herdr, processo dell'agente, processo che possiede la finestra, indizi d'ambiente.</summary>
public sealed record TerminalTarget(string? HerdrPane, int? AgentPid, int? WindowPid, string? WtSession, int? VscodePid, DateTimeOffset ResolvedAt);

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
    /// </summary>
    public void Observe(SessionState session)
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
        _ = Task.Run(() => Resolve(key, host));
    }

    public void Forget(AgentKind agent, string sessionId)
    {
        lock (_gate)
        {
            _targets.Remove((agent, sessionId));
            _resolved.Remove((agent, sessionId));
        }
    }

    private void Resolve((AgentKind Agent, string SessionId) key, HostInfo host)
    {
        try
        {
            int? agentPid = null;
            int? windowPid = null;
            if (host.Ppid is { } ppid && ppid > 0)
            {
                var chain = ProcessTree.Ancestors(ppid);
                foreach (var node in chain)
                {
                    agentPid ??= IsAgentProcess(node.Name) ? node.Pid : null;
                    if (windowPid is null && WindowActivator.FindTopLevelWindow(node.Pid) != IntPtr.Zero) windowPid = node.Pid;
                    if (agentPid is not null && windowPid is not null) break;
                }
                OnLog?.Invoke($"Terminal resolve {key.Agent} {key.SessionId}: ppid {ppid} → {chain.Count} antenati, agent {agentPid?.ToString() ?? "-"}, finestra {windowPid?.ToString() ?? "-"}, pane {host.HerdrPane ?? "-"}");
            }
            else
            {
                OnLog?.Invoke($"Terminal resolve {key.Agent} {key.SessionId}: nessun ppid, pane {host.HerdrPane ?? "-"}");
            }

            var target = new TerminalTarget(host.HerdrPane, agentPid, windowPid, host.WtSession, host.VscodePid, _clock.UtcNow);
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

    private static bool IsAgentProcess(string name) =>
        AgentProcessNames.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
