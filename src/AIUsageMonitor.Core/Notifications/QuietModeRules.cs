using System.Buffers.Binary;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>
/// How the raw values read by the App's quiet-mode probe (spec 2026-09-27 §7) turn into "hold the cards". Pure, so the
/// interpretation is tested here while the P/Invoke calls stay in <c>QuietModeProbe</c>.
/// </summary>
public static class QuietModeRules
{
    // QUERY_USER_NOTIFICATION_STATE values (shellapi.h).
    public const int QunsNotPresent = 1;
    public const int QunsBusy = 2;
    public const int QunsRunningD3DFullScreen = 3;
    public const int QunsPresentationMode = 4;
    public const int QunsAcceptsNotifications = 5;
    public const int QunsQuietTime = 6;
    public const int QunsApp = 7;

    /// <summary>
    /// True for a full-screen app or game, presentation mode and a full-screen Store app. A locked screen
    /// (<see cref="QunsNotPresent"/>) and the first hour after setup (<see cref="QunsQuietTime"/>) are not quiet.
    /// </summary>
    public static bool IsQuietShellState(int state) =>
        state is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode or QunsApp;

    /// <summary>
    /// Data of <c>WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED</c>: the active Do not disturb profile as a little-endian
    /// 32-bit value, 0 when it is off. Fewer than 4 bytes (nothing published yet) means off; extra bytes are ignored.
    /// </summary>
    public static bool IsDoNotDisturb(ReadOnlySpan<byte> data) =>
        data.Length >= sizeof(int) && BinaryPrimitives.ReadInt32LittleEndian(data) != 0;
}
