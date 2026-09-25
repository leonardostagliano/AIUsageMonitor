using System.ComponentModel;
using System.Diagnostics;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Sessions;

namespace AIUsageMonitor.App.Terminal;

/// <summary>
/// Apre una sessione cloud o l'esecuzione di una routine (click sul nome). Se l'app desktop di Claude e' gia' in
/// esecuzione (anche con la finestra nascosta) la sessione le viene passata con il link
/// <c>claude://claude.ai/epitaxy/session_&lt;id&gt;</c>; altrimenti, o se l'app non da' segno di averlo ricevuto, si
/// apre la pagina su claude.ai nel browser. Il percorso non e' documentato (provato a mano sull'app dello Store): se una
/// versione dell'app lo ignora o mostra la sua home, da fuori non si puo' distinguere. L'app non viene mai avviata per
/// un link: prima si guarda se c'e', poi si apre il link.
/// Gli indirizzi li costruisce Core da prefissi fissi, con l'id validato per l'app e codificato per il browser. Non
/// solleva mai: false solo quando non si e' aperto nulla, e allora il chiamante mostra il toast.
/// </summary>
public sealed class CloudSessionOpener
{
    /// <summary>
    /// Attesa massima di un segno dell'app dopo il link: il link passa da un secondo processo dell'app che lo inoltra a
    /// quello gia' aperto, e solo allora l'app puo' reagire.
    /// </summary>
    private static readonly TimeSpan ReactionWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReactionPoll = TimeSpan.FromMilliseconds(150);

    private readonly Action<string> _log;
    private readonly Action<string, Exception> _error;

    public CloudSessionOpener(Action<string> log, Action<string, Exception> error)
    {
        _log = log;
        _error = error;
    }

    public async Task<bool> OpenAsync(SessionState session)
    {
        var id = session.SessionId;
        var appPids = FindAppProcesses(id);
        var links = ClaudeDesktopApp.LinksFor(id, appPids.Count > 0);
        if (links.App is { } appUrl)
        {
            if (await TryAppAsync(id, appUrl, appPids).ConfigureAwait(false)) return true;
        }
        else if (appPids.Count > 0)
        {
            _log($"Sessione cloud {id}: app Claude in esecuzione ma id non adatto a un link dell'app");
        }
        return Launch(id, links.Web, "aperta nel browser");
    }

    /// <summary>
    /// Pid dei processi dell'app che possiedono finestre sue, anche nascoste (l'app nella tray), nel desktop di questa
    /// sessione di Windows: di solito il solo processo principale di Electron. Un processo dello stesso eseguibile
    /// senza finestre (un processo di servizio rimasto, un'istanza che si sta chiudendo, l'app di un altro utente)
    /// non riceverebbe il link, che avvierebbe una nuova istanza. Vuoto quando l'app non gira o quando la scansione
    /// non riesce, che vuol dire browser.
    /// </summary>
    private IReadOnlyList<int> FindAppProcesses(string id)
    {
        try
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var pids = new List<int>();
            foreach (var node in ProcessTree.Snapshot().Values)
            {
                if (ClaudeDesktopApp.IsCandidate(node.Name) && ClaudeDesktopApp.IsAppImage(WindowsProcessProbe.ImagePath(node.Pid), localAppData))
                    pids.Add(node.Pid);
            }
            if (pids.Count == 0)
            {
                _log($"Sessione cloud {id}: app Claude non in esecuzione");
                return pids;
            }
            var owners = WindowActivator.TopLevelWindows(pids).Where(ClaudeDesktopApp.IsAppWindow).Select(w => w.Pid).Distinct().ToList();
            if (owners.Count == 0) _log($"Sessione cloud {id}: processi dell'app Claude senza finestre (pid {string.Join(", ", pids)}), app non considerata aperta");
            return owners;
        }
        catch (Exception ex)
        {
            _error($"Sessione cloud {id}: ricerca dell'app Claude non riuscita", ex);
            return [];
        }
    }

    /// <summary>
    /// Link all'app, poi un segno che l'app l'abbia ricevuto entro <see cref="ReactionWait"/>: una sua finestra che
    /// viene in primo piano, compare o cambia titolo (<see cref="ClaudeDesktopApp.ReactedWindow"/>). Che il link non
    /// abbia sollevato dice solo che la shell l'ha preso in carico, e una finestra gia' aperta non prova nulla: senza
    /// un segno si ripiega sul browser. Prima del link l'app riceve il diritto al primo piano che il click ha dato a
    /// questo processo (il notch non si attiva), cosi' puo' portarsi avanti da se'; se reagisce senza farlo la si
    /// attiva da qui, e se neanche cosi' viene in primo piano si apre il browser come per <c>TerminalFocuser</c>.
    /// </summary>
    private async Task<bool> TryAppAsync(string id, string url, IReadOnlyList<int> appPids)
    {
        try
        {
            _log($"Sessione cloud {id}: app Claude in esecuzione (pid {string.Join(", ", appPids)})");
            var before = WindowActivator.TopLevelWindows(appPids);
            var foregroundBefore = WindowActivator.ForegroundWindow();
            foreach (var pid in appPids) WindowActivator.AllowForeground(pid);
            if (!Launch(id, url, "link passato all'app")) return false;
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < ReactionWait)
            {
                await Task.Delay(ReactionPoll).ConfigureAwait(false);
                var foreground = WindowActivator.ForegroundWindow();
                var hwnd = ClaudeDesktopApp.ReactedWindow(before, foregroundBefore, WindowActivator.TopLevelWindows(appPids), foreground);
                if (hwnd == IntPtr.Zero) continue;
                if (hwnd == foreground)
                {
                    _log($"Sessione cloud {id}: l'app Claude ha reagito al link ed e' in primo piano (finestra 0x{hwnd:X})");
                    return true;
                }
                var activated = WindowActivator.Activate(hwnd);
                _log(activated
                    ? $"Sessione cloud {id}: l'app Claude ha reagito al link, finestra 0x{hwnd:X} attivata"
                    : $"Sessione cloud {id}: l'app Claude ha reagito al link ma la finestra 0x{hwnd:X} non e' venuta in primo piano, ripiego sul browser");
                return activated;
            }
            _log($"Sessione cloud {id}: nessun segno dall'app Claude entro {ReactionWait.TotalSeconds:0} s dal link, ripiego sul browser");
            return false;
        }
        catch (Exception ex)
        {
            _error($"Sessione cloud {id}: apertura nell'app Claude non riuscita", ex);
            return false;
        }
    }

    /// <summary>
    /// Apre il link con la shell: l'app registrata per claude://, il browser per https://. Senza un'app registrata per
    /// claude:// (installazione Squirrel o pacchetto MSIX, che la registrano in modi diversi) la shell rifiuta il link
    /// con una Win32Exception e si passa al browser: e' questo il controllo della registrazione.
    /// </summary>
    private bool Launch(string id, string url, string outcome)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            _log($"Sessione cloud {id}: {outcome}, {url}");
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            _error($"Sessione cloud {id}: {url} non aperto", ex);
            return false;
        }
    }
}
