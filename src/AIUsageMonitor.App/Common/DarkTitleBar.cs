using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AIUsageMonitor.App.Common;

/// <summary>
/// Chiede a DWM la barra del titolo scura (immersive dark mode) su Windows 10 20H1+ / 11. Senza questa chiamata la
/// barra del titolo resta chiara anche con una finestra tutta scura, perche' la disegna il sistema e non WPF.
/// L'attributo ha cambiato numero durante le build di Windows 10: 20 e' quello definitivo, 19 quello delle build
/// 18985-19041; su versioni piu' vecchie <c>DwmSetWindowAttribute</c> ritorna un HRESULT di errore e non succede nulla.
/// </summary>
public static class DarkTitleBar
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY = 19;

    /// <summary>Va chiamata quando la finestra ha gia' un HWND (tipicamente in <c>OnSourceInitialized</c>).</summary>
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var on = 1;
        if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_LEGACY, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
