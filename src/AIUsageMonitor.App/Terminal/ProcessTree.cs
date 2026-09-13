using System.Runtime.InteropServices;

namespace AIUsageMonitor.App.Terminal;

/// <summary>Un processo nello snapshot di ToolHelp: pid, pid del padre e nome dell'eseguibile.</summary>
public sealed record ProcessNode(int Pid, int ParentPid, string Name);

/// <summary>
/// Catena dei processi letta con ToolHelp32. Serve a risalire dal processo che ha eseguito l'hook (un wrapper
/// effimero) fino alla finestra che ospita davvero la sessione: su questa macchina la catena e'
/// <c>pwsh.exe ← claude.exe ← powershell.exe ← herdr.exe ← herdr.exe ← cmd.exe ← WindowsTerminal.exe</c> e l'unico
/// antenato con una finestra top-level e' <c>WindowsTerminal.exe</c>. Lo snapshot e' una foto: i pid possono essere
/// gia' morti quando lo si usa, quindi chi legge deve tollerare i buchi.
/// </summary>
public static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>Mappa pid → nodo di tutti i processi visibili. Vuota se lo snapshot fallisce.</summary>
    public static IReadOnlyDictionary<int, ProcessNode> Snapshot()
    {
        var map = new Dictionary<int, ProcessNode>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == InvalidHandle) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snapshot, ref entry)) return map;
            do
            {
                // I pid sono unici nello snapshot, ma un indice duplicato non deve far saltare tutta la scansione.
                map[(int)entry.th32ProcessID] = new ProcessNode((int)entry.th32ProcessID, (int)entry.th32ParentProcessID, entry.szExeFile ?? "");
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    /// <summary>
    /// Il processo stesso seguito dai suoi antenati, dal piu' vicino al piu' lontano. Si ferma quando il padre non
    /// esiste piu' (pid riciclato o processo orfano), quando il pid si ripete (ciclo) o dopo <paramref name="maxDepth"/>.
    /// </summary>
    public static IReadOnlyList<ProcessNode> Ancestors(int pid, int maxDepth = 12) => Ancestors(Snapshot(), pid, maxDepth);

    /// <summary>Come <see cref="Ancestors(int, int)"/> ma su uno snapshot gia' preso, per non rileggerlo a ogni catena.</summary>
    public static IReadOnlyList<ProcessNode> Ancestors(IReadOnlyDictionary<int, ProcessNode> snapshot, int pid, int maxDepth = 12)
    {
        var chain = new List<ProcessNode>();
        var seen = new HashSet<int>();
        var current = pid;
        while (chain.Count < maxDepth && current > 0 && seen.Add(current) && snapshot.TryGetValue(current, out var node))
        {
            chain.Add(node);
            current = node.ParentPid;
        }
        return chain;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public int dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
