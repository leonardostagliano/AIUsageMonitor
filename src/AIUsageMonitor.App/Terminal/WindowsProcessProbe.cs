using System.Runtime.InteropServices;
using System.Text;
using AIUsageMonitor.Core.Sessions;

namespace AIUsageMonitor.App.Terminal;

/// <summary>
/// Vivo, morto o sconosciuto per un pid, con l'ora di creazione del processo (FILETIME UTC) che distingue un pid
/// riciclato da Windows dal processo originale. Basta PROCESS_QUERY_LIMITED_INFORMATION, concesso anche sui processi
/// elevati dello stesso utente; un accesso negato e' "sconosciuto" e non chiude mai una sessione.
/// </summary>
public sealed class WindowsProcessProbe : IProcessProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;
    private const uint StillActive = 259;
    private const int MaxImagePath = 1024;

    public ProcessSnapshot Query(int pid)
    {
        // 0 e' il processo inattivo e 4 il kernel: nessun agente vive li'.
        if (pid <= 4) return ProcessSnapshot.Dead;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == ErrorInvalidParameter ? ProcessSnapshot.Dead : ProcessSnapshot.Unknown;
        try
        {
            // Un handle ancora aperto da qualcuno tiene in vita l'oggetto di un processo gia' uscito.
            if (GetExitCodeProcess(handle, out var code) && code != StillActive) return ProcessSnapshot.Dead;
            return GetProcessTimes(handle, out var creation, out _, out _, out _)
                ? ProcessSnapshot.Alive(creation)
                : ProcessSnapshot.Alive(null);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Percorso completo dell'eseguibile del processo, null se il processo non c'e' piu' o l'accesso e' negato. Serve
    /// quando il nome non basta: l'app desktop di Claude e la CLI di Claude Code sono entrambe <c>claude.exe</c>.
    /// </summary>
    public static string? ImagePath(int pid)
    {
        if (pid <= 4) return null;
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(MaxImagePath);
            var size = buffer.Capacity;
            return QueryFullProcessImageNameW(handle, 0, buffer, ref size) && size > 0 ? buffer.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
