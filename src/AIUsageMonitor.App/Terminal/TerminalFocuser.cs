using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Terminal;

/// <summary>
/// Porta in primo piano il terminale che ospita una sessione, provando le strategie in ordine di affidabilita':
/// prima Herdr (che sa spostare il focus sul pane giusto dentro la finestra), poi la finestra risolta quando
/// l'evento e' arrivato, infine gli indizi d'ambiente. Si ferma alla prima che riesce e non solleva mai eccezioni:
/// chi chiama distingue solo fra "fatto" e "terminale non trovato". Tutto il lavoro gira fuori dal thread della UI.
/// </summary>
public sealed class TerminalFocuser
{
    private readonly HerdrClient _herdr;
    private readonly TerminalRegistry _registry;
    private readonly Action<string> _log;

    public TerminalFocuser(HerdrClient herdr, TerminalRegistry registry, Action<string> log)
    {
        _herdr = herdr;
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
        var pane = target?.HerdrPane ?? _herdr.FindPaneBySession(session.SessionId);
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

    /// <summary>Finestra risolta all'arrivo dell'evento; se il processo non c'e' piu' si ritenta dall'agente.</summary>
    private bool TryKnownWindow(TerminalTarget? target)
    {
        if (target is null) return false;
        if (target.WindowPid is { } windowPid)
        {
            var hwnd = WindowActivator.FindTopLevelWindow(windowPid);
            if (hwnd != IntPtr.Zero && WindowActivator.Activate(hwnd))
            {
                _log($"Focus: attivata la finestra del processo {windowPid} \"{WindowActivator.WindowTitle(hwnd)}\"");
                return true;
            }
        }
        if (target.AgentPid is { } agentPid && ActivateAncestorWindow(agentPid)) return true;
        return false;
    }

    /// <summary>
    /// Ultimo tentativo con i soli indizi d'ambiente: il pid di VS Code identifica la finestra senza ambiguita',
    /// mentre <c>WT_SESSION</c> non dice quale finestra di Windows Terminal sia: con piu' di una si rinuncia.
    /// </summary>
    private bool TryHints(TerminalTarget? target)
    {
        if (target is null) return false;
        if (target.VscodePid is { } vscodePid)
        {
            var hwnd = WindowActivator.FindTopLevelWindow(vscodePid);
            if (hwnd != IntPtr.Zero && WindowActivator.Activate(hwnd))
            {
                _log($"Focus: attivata la finestra di VS Code (pid {vscodePid})");
                return true;
            }
        }
        if (target.WtSession is null) return false;

        var windows = new List<(int Pid, IntPtr Hwnd)>();
        foreach (var node in ProcessTree.Snapshot().Values)
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

    /// <summary>Risale da un pid fino al primo antenato con una finestra top-level e la attiva.</summary>
    private bool ActivateAncestorWindow(int pid)
    {
        var snapshot = ProcessTree.Snapshot();
        foreach (var node in ProcessTree.Ancestors(snapshot, pid))
        {
            var hwnd = WindowActivator.FindTopLevelWindow(node.Pid);
            if (hwnd == IntPtr.Zero) continue;
            var activated = WindowActivator.Activate(hwnd);
            _log($"Focus: finestra \"{WindowActivator.WindowTitle(hwnd)}\" di {node.Name} ({node.Pid}) {(activated ? "attivata" : "non attivata")}");
            return activated;
        }
        return false;
    }

    private static string Describe(TerminalTarget? target) => target is null
        ? "sconosciuto"
        : $"pane {target.HerdrPane ?? "-"}, agent {target.AgentPid?.ToString() ?? "-"}, finestra {target.WindowPid?.ToString() ?? "-"}, wt {(target.WtSession is null ? "-" : "si")}, vscode {target.VscodePid?.ToString() ?? "-"}";
}
