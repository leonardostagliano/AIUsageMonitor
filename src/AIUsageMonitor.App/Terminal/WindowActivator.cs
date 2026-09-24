using System.Runtime.InteropServices;
using System.Text;
using AIUsageMonitor.Core.Sessions;

namespace AIUsageMonitor.App.Terminal;

/// <summary>
/// Trova la finestra top-level di un processo e la porta in primo piano. Windows concede il foreground solo al
/// processo che ha ricevuto l'ultimo input dell'utente: <c>SetForegroundWindow</c> puo' quindi limitarsi a far
/// lampeggiare l'icona nella barra. Il ripiego standard e' agganciarsi alla coda di input del thread della finestra
/// di destinazione (<c>AttachThreadInput</c>) per il tempo della chiamata. L'esito viene comunque verificato con
/// <c>GetForegroundWindow</c>: qui non si mente mai sul risultato, perche' il chiamante deve poter passare alla
/// strategia successiva o mostrare il toast.
/// </summary>
public static class WindowActivator
{
    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;
    private const int MaxClassName = 256;

    /// <summary>
    /// Prima finestra top-level visibile e senza owner del processo. Preferisce quella con un titolo non vuoto (la
    /// finestra vera del terminale) rispetto alle finestre di servizio senza titolo; 0 se il processo non ne ha.
    /// </summary>
    public static IntPtr FindTopLevelWindow(int pid)
    {
        if (pid <= 0) return IntPtr.Zero;
        var best = IntPtr.Zero;
        var fallback = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != (uint)pid) return true;
            if (GetWindowTextLengthW(hwnd) > 0) { best = hwnd; return false; }
            if (fallback == IntPtr.Zero) fallback = hwnd;
            return true;
        }, IntPtr.Zero);
        return best != IntPtr.Zero ? best : fallback;
    }

    /// <summary>
    /// Finestre top-level senza owner dei processi indicati, visibili o nascoste, con classe e titolo. EnumWindows vede
    /// solo il desktop di questa sessione di Windows: le finestre degli altri utenti collegati non ci sono.
    /// </summary>
    public static IReadOnlyList<TopLevelWindow> TopLevelWindows(IReadOnlyCollection<int> pids)
    {
        var owners = new HashSet<int>(pids);
        var windows = new List<TopLevelWindow>();
        if (owners.Count == 0) return windows;
        EnumWindows((hwnd, _) =>
        {
            if (GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return true;
            GetWindowThreadProcessId(hwnd, out var owner);
            if (!owners.Contains((int)owner)) return true;
            windows.Add(new TopLevelWindow(hwnd, (int)owner, ClassName(hwnd), IsWindowVisible(hwnd), WindowTitle(hwnd)));
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    /// <summary>La finestra in primo piano adesso, 0 se nessuna.</summary>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    /// <summary>
    /// Lascia al processo indicato il diritto di portarsi in primo piano da se'. Riesce solo mentre questo processo
    /// ha quel diritto, cioe' subito dopo un click dell'utente su una sua finestra.
    /// </summary>
    public static bool AllowForeground(int pid) => pid > 0 && AllowSetForegroundWindow((uint)pid);

    /// <summary>Titolo della finestra, per il log. Stringa vuota se non ne ha uno.</summary>
    public static string WindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "";
        var length = GetWindowTextLengthW(hwnd);
        if (length <= 0) return "";
        var buffer = new StringBuilder(length + 1);
        var copied = GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString(0, copied) : "";
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(MaxClassName);
        var copied = GetClassNameW(hwnd, buffer, buffer.Capacity);
        return copied > 0 ? buffer.ToString(0, copied) : "";
    }

    /// <summary>
    /// Ripristina la finestra se e' ridotta a icona e la porta in primo piano. Torna true solo se alla fine e'
    /// davvero lei la finestra in foreground.
    /// </summary>
    public static bool Activate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return true;

        // Foreground rifiutato: ci si aggancia alla coda di input del thread della finestra di destinazione, cosi'
        // Windows tratta la richiesta come se venisse da lei. L'aggancio va sempre sciolto, anche in caso di errore.
        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var currentThread = GetCurrentThreadId();
        if (targetThread == 0 || targetThread == currentThread) return GetForegroundWindow() == hwnd;
        var attached = AttachThreadInput(currentThread, targetThread, true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, targetThread, false);
        }
        return GetForegroundWindow() == hwnd;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextLengthW")]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachTo, uint attachFrom, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
