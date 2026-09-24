using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Terminal;

namespace AIUsageMonitor.App.Terminal;

/// <summary>
/// Porta in primo piano il terminale che ospita una sessione, provando le strategie in ordine di affidabilita':
/// prima i multiplexer che sanno spostare il focus sul pane giusto dentro la finestra (Herdr, wmux), poi la finestra
/// risolta quando l'evento e' arrivato, infine gli indizi d'ambiente. Si ferma alla prima che riesce e non solleva mai eccezioni:
/// chi chiama distingue solo fra "fatto" e "terminale non trovato". Tutto il lavoro gira fuori dal thread della UI.
/// </summary>
public sealed class TerminalFocuser
{
    private readonly HerdrClient _herdr;
    private readonly WmuxClient _wmux;
    private readonly TerminalRegistry _registry;
    private readonly Action<string> _log;

    public TerminalFocuser(HerdrClient herdr, WmuxClient wmux, TerminalRegistry registry, Action<string> log)
    {
        _herdr = herdr;
        _wmux = wmux;
        _registry = registry;
        _log = log;
    }

    /// <summary>True se il terminale e' stato portato in primo piano (o se Herdr ha spostato il focus sul pane).</summary>
    public Task<bool> FocusAsync(SessionState session) => Task.Run(() =>
    {
        try
        {
            return Focus(session);
        }
        catch (Exception ex)
        {
            _log($"Focus {session.Agent} {session.SessionId}: errore {ex}");
            return false;
        }
    });

    private bool Focus(SessionState session)
    {
        var target = _registry.Get(session.Agent, session.SessionId);
        _log($"Focus {session.Agent} {session.SessionId}: target {Describe(target)}");

        if (TryHerdr(session, target)) return true;
        if (TryWmux(target)) return true;
        if (TryKnownWindow(target)) return true;
        if (TryHints(target)) return true;

        _log($"Focus {session.Agent} {session.SessionId}: nessuna strategia ha funzionato");
        return false;
    }

    /// <summary>
    /// Herdr conosce il pane della sessione anche quando l'evento non portava un host (<c>api snapshot</c>), e sa
    /// spostare il focus dentro la finestra: e' l'unica strategia che arriva al pane e non solo alla finestra.
    /// Il focus del pane basta a considerare l'operazione riuscita, perche' l'attivazione della finestra puo' essere
    /// rifiutata da Windows mentre il pane e' comunque diventato quello attivo.
    /// </summary>
    private bool TryHerdr(SessionState session, TerminalTarget? target)
    {
        if (!_herdr.IsAvailable) return false;
        // Con un pty di wmux la sessione sta in wmux: lo snapshot di Herdr costerebbe un paio di secondi al click per
        // nulla. Herdr annidato dentro wmux resta coperto, perche' in quel caso l'hook registra anche il suo pane.
        var pane = target?.HerdrPane ?? (target?.WmuxPty is null ? _herdr.FindPaneBySession(session.SessionId) : null);
        if (pane is null) { _log("Focus: nessun pane Herdr per questa sessione"); return false; }

        var focused = _herdr.FocusAgent(pane);
        if (!focused && _herdr.GetPane(pane) is { TabId: { } tabId }) focused = _herdr.FocusTab(tabId);
        if (!focused) { _log($"Focus: Herdr non ha portato il focus sul pane {pane}"); return false; }
        _log($"Focus: Herdr ha messo a fuoco il pane {pane}");

        // La finestra che ospita il pane si trova risalendo dalla shell del pane: il terminale e' uno degli antenati.
        if (_herdr.ShellPid(pane) is { } shellPid && ActivateAncestorWindow(shellPid)) return true;
        _log("Focus: pane a fuoco ma finestra non attivata (esito comunque positivo)");
        return true;
    }

    /// <summary>
    /// wmux fa girare le shell sotto un daemon senza finestra, e il processo che ha eseguito l'hook e' gia' morto
    /// quando l'evento arriva: dalla catena dei processi alla finestra non si arriva quasi mai. Il pty registrato
    /// dall'hook (<c>WMUX_PTY_ID</c>) porta invece dritto al pane. La finestra e' quella dell'unico <c>wmux.exe</c>
    /// che ne ha una, il processo principale di Electron. Senza pty (eventi di un hook precedente) basta
    /// <c>TERM_PROGRAM=wmux</c> per portare almeno la finestra in primo piano. Come per Herdr, il pane a fuoco basta
    /// a considerare riuscita l'operazione anche se Windows rifiuta l'attivazione della finestra.
    /// </summary>
    private bool TryWmux(TerminalTarget? target)
    {
        if (target is null) return false;
        if (target.WmuxPty is null && !string.Equals(target.TermProgram, "wmux", StringComparison.OrdinalIgnoreCase)) return false;

        var paneFocused = false;
        if (target.WmuxPty is { } pty)
        {
            paneFocused = _wmux.FocusPtyAsync(pty).GetAwaiter().GetResult();
            _log(paneFocused ? $"Focus: wmux ha messo a fuoco il pane del pty {pty}" : $"Focus: wmux non ha messo a fuoco il pty {pty}");
        }
        if (ActivateWmuxWindow()) return true;
        if (paneFocused) _log("Focus: pane wmux a fuoco ma finestra non attivata (esito comunque positivo)");
        return paneFocused;
    }

    private bool ActivateWmuxWindow()
    {
        foreach (var node in ProcessTree.Snapshot().Values)
        {
            if (!node.Name.Equals("wmux.exe", StringComparison.OrdinalIgnoreCase)) continue;
            var hwnd = WindowActivator.FindTopLevelWindow(node.Pid);
            if (hwnd == IntPtr.Zero) continue;
            var activated = WindowActivator.Activate(hwnd);
            _log($"Focus: finestra \"{WindowActivator.WindowTitle(hwnd)}\" di wmux.exe ({node.Pid}) {(activated ? "attivata" : "non attivata")}");
            return activated;
        }
        _log("Focus: nessuna finestra di wmux.exe");
        return false;
    }

    /// <summary>
    /// Finestra risolta all'arrivo dell'evento; se il processo non c'e' piu' si ritenta dall'agente.
    /// Ogni pid memorizzato viene riconfrontato con il nome dell'immagine vista al momento della risoluzione: la
    /// chiusura di un terminale non emette <c>SessionEnd</c>, quindi il target resta valido fino allo sweep (12 ore)
    /// e in quella finestra Windows puo' aver assegnato lo stesso pid a tutt'altro processo. Senza il controllo, un
    /// click su una sessione morta porterebbe in primo piano un'applicazione a caso e sopprimerebbe pure il toast.
    /// </summary>
    private bool TryKnownWindow(TerminalTarget? target)
    {
        if (target is null) return false;
        var snapshot = ProcessTree.Snapshot();
        if (target.WindowPid is { } windowPid)
        {
            if (!IsStillProcess(snapshot, windowPid, target.WindowPidName))
            {
                _log($"Focus: il processo {windowPid} non e' piu' {target.WindowPidName ?? "-"}, pid riciclato: finestra ignorata");
            }
            else
            {
                var hwnd = WindowActivator.FindTopLevelWindow(windowPid);
                if (hwnd != IntPtr.Zero && WindowActivator.Activate(hwnd))
                {
                    _log($"Focus: attivata la finestra del processo {windowPid} \"{WindowActivator.WindowTitle(hwnd)}\"");
                    return true;
                }
            }
        }
        if (target.AgentPid is { } agentPid)
        {
            // Il pid dell'agente serve solo come punto di partenza della risalita, ma un pid riciclato farebbe
            // risalire una catena estranea: si riparte solo se e' ancora lo stesso processo (o almeno un agente).
            var name = snapshot.TryGetValue(agentPid, out var node) ? node.Name : null;
            var same = name is not null && (name.Equals(target.AgentPidName, StringComparison.OrdinalIgnoreCase)
                || (target.AgentPidName is null && TerminalRegistry.IsAgentProcess(name)));
            if (!same) _log($"Focus: il processo {agentPid} non e' piu' {target.AgentPidName ?? "un agente"}, pid riciclato: risalita saltata");
            else if (ActivateAncestorWindow(agentPid, snapshot)) return true;
        }
        return false;
    }

    /// <summary>
    /// Ultimo tentativo con i soli indizi d'ambiente: il pid di VS Code identifica la finestra senza ambiguita',
    /// mentre <c>WT_SESSION</c> non dice quale finestra di Windows Terminal sia: con piu' di una si rinuncia.
    /// </summary>
    private bool TryHints(TerminalTarget? target)
    {
        if (target is null) return false;
        var snapshot = ProcessTree.Snapshot();
        if (target.VscodePid is { } vscodePid)
        {
            // VSCODE_PID viene dall'ambiente dell'hook e non da una risalita: l'unico nome atteso e' Code*.exe.
            // Dopo un riavvio di VS Code quel pid puo' essere di chiunque, quindi si controlla prima di attivare.
            if (snapshot.TryGetValue(vscodePid, out var vscode) && vscode.Name.StartsWith("Code", StringComparison.OrdinalIgnoreCase))
            {
                var hwnd = WindowActivator.FindTopLevelWindow(vscodePid);
                if (hwnd != IntPtr.Zero && WindowActivator.Activate(hwnd))
                {
                    _log($"Focus: attivata la finestra di VS Code (pid {vscodePid})");
                    return true;
                }
            }
            else _log($"Focus: il processo {vscodePid} non e' VS Code, pid riciclato: indizio VSCODE_PID ignorato");
        }
        if (target.WtSession is null) return false;

        var windows = new List<(int Pid, IntPtr Hwnd)>();
        foreach (var node in snapshot.Values)
        {
            if (!node.Name.StartsWith("WindowsTerminal", StringComparison.OrdinalIgnoreCase)) continue;
            var hwnd = WindowActivator.FindTopLevelWindow(node.Pid);
            if (hwnd != IntPtr.Zero) windows.Add((node.Pid, hwnd));
        }
        if (windows.Count != 1)
        {
            _log($"Focus: {windows.Count} finestre di Windows Terminal, WT_SESSION non basta a sceglierne una");
            return false;
        }
        if (!WindowActivator.Activate(windows[0].Hwnd)) return false;
        _log($"Focus: attivata l'unica finestra di Windows Terminal (pid {windows[0].Pid})");
        return true;
    }

    /// <summary>
    /// Risale da un pid fino al primo antenato con una finestra top-level e la attiva. La risalita si ferma prima
    /// della shell e dei processi di sistema: <c>explorer.exe</c> possiede la finestra del desktop ("Program
    /// Manager") e supererebbe il filtro "visibile, senza owner, con titolo" di <see cref="WindowActivator"/>.
    /// Quando nella catena non c'e' nessun terminale - una console semplice, la cui finestra appartiene a un
    /// <c>conhost.exe</c> figlio e non a un antenato - la risposta giusta e' false: si passa alla strategia
    /// successiva e, se anche quella fallisce, l'utente vede il toast "Terminale non trovato" invece del desktop.
    /// </summary>
    private bool ActivateAncestorWindow(int pid, IReadOnlyDictionary<int, ProcessNode>? snapshot = null)
    {
        foreach (var node in ProcessTree.Ancestors(snapshot ?? ProcessTree.Snapshot(), pid))
        {
            if (ProcessTree.IsShellOrSystem(node))
            {
                _log($"Focus: risalita fermata su {node.Name} ({node.Pid}): nessun terminale in questa catena");
                return false;
            }
            var hwnd = WindowActivator.FindTopLevelWindow(node.Pid);
            if (hwnd == IntPtr.Zero) continue;
            var activated = WindowActivator.Activate(hwnd);
            _log($"Focus: finestra \"{WindowActivator.WindowTitle(hwnd)}\" di {node.Name} ({node.Pid}) {(activated ? "attivata" : "non attivata")}");
            return activated;
        }
        return false;
    }

    /// <summary>True se <paramref name="pid"/> e' ancora vivo e porta il nome visto al momento della risoluzione.</summary>
    private static bool IsStillProcess(IReadOnlyDictionary<int, ProcessNode> snapshot, int pid, string? expectedName) =>
        expectedName is not null
        && snapshot.TryGetValue(pid, out var node)
        && node.Name.Equals(expectedName, StringComparison.OrdinalIgnoreCase);

    private static string Describe(TerminalTarget? target) => target is null
        ? "sconosciuto"
        : $"pane {target.HerdrPane ?? "-"}, wmux {target.WmuxPty ?? (target.TermProgram == "wmux" ? "senza pty" : "-")}, agent {target.AgentPid?.ToString() ?? "-"} ({target.AgentPidName ?? "-"}), finestra {target.WindowPid?.ToString() ?? "-"} ({target.WindowPidName ?? "-"}), wt {(target.WtSession is null ? "-" : "si")}, vscode {target.VscodePid?.ToString() ?? "-"}";
}
