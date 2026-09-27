using System.Runtime.InteropServices;
using AIUsageMonitor.Core.Notifications;

namespace AIUsageMonitor.App.Notifications;

/// <summary>
/// "Non disturbare" e schermo intero (spec 2026-09-27 §7): in silenzio le card aspettano. Due fonti, lette a ogni
/// chiamata (costano pochi microsecondi): <c>SHQueryUserNotificationState</c> per app e giochi a schermo intero e per
/// la modalita' presentazione, e lo stato WNF <c>WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED</c>, dove Windows pubblica
/// il profilo attivo di "Non disturbare" (0 = spento). Il secondo e' un'interfaccia non documentata, verificata su
/// Windows 11 26200 (4 byte, 0 con "Non disturbare" spento). Qualunque errore vale "non in silenzio": le card si
/// mostrano sempre e il motivo (solo il codice o il tipo dell'errore) va una volta sola a <c>logOnce</c>, per fonte e
/// per processo. Non solleva mai; l'interpretazione dei valori e' in <see cref="QuietModeRules"/>.
/// </summary>
public static class QuietModeProbe
{
    // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
    private const ulong QuietHoursProfileChanged = 0x0D83063EA3BF1C75UL;
    private const int WnfBufferSize = 16;

    private static int _shellFailureReported;
    private static int _wnfFailureReported;

    public static bool IsQuiet(Action<string>? logOnce = null) => IsShellBusy(logOnce) || IsDoNotDisturbOn(logOnce);

    private static bool IsShellBusy(Action<string>? logOnce)
    {
        try
        {
            var hr = SHQueryUserNotificationState(out var state);
            if (hr == 0) return QuietModeRules.IsQuietShellState(state);
            ReportOnce(ref _shellFailureReported, logOnce, $"SHQueryUserNotificationState: HRESULT 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            ReportOnce(ref _shellFailureReported, logOnce, $"SHQueryUserNotificationState: {ex.GetType().Name}");
        }
        return false;
    }

    private static bool IsDoNotDisturbOn(Action<string>? logOnce)
    {
        try
        {
            var stateName = QuietHoursProfileChanged;
            var buffer = new byte[WnfBufferSize];
            var size = (uint)buffer.Length;
            var status = NtQueryWnfStateData(ref stateName, IntPtr.Zero, IntPtr.Zero, out _, buffer, ref size);
            if (status != 0)
            {
                ReportOnce(ref _wnfFailureReported, logOnce, $"NtQueryWnfStateData: NTSTATUS 0x{status:X8}");
                return false;
            }
            var length = (int)Math.Min(size, (uint)buffer.Length);
            // Zero byte: stato mai pubblicato, cioe' spento. Da 1 a 3 byte il formato non e' quello verificato.
            if (length is > 0 and < sizeof(int))
                ReportOnce(ref _wnfFailureReported, logOnce, $"NtQueryWnfStateData: {length} byte invece di 4");
            return QuietModeRules.IsDoNotDisturb(buffer.AsSpan(0, length));
        }
        catch (Exception ex)
        {
            ReportOnce(ref _wnfFailureReported, logOnce, $"NtQueryWnfStateData: {ex.GetType().Name}");
            return false;
        }
    }

    private static void ReportOnce(ref int reported, Action<string>? logOnce, string what)
    {
        if (logOnce is null || Interlocked.Exchange(ref reported, 1) != 0) return;
        try { logOnce(what); }
        catch (Exception) { /* un log che fallisce non deve togliere le card */ }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr explicitScope,
        out uint changeStamp, byte[] buffer, ref uint bufferSize);
}
