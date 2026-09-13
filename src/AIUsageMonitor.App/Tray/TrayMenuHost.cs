using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;

namespace AIUsageMonitor.App.Tray;

/// <summary>
/// Finestra nascosta che fa da proprietaria del menu del tray. Serve per la regola classica dei menu di notifica:
/// il menu di una NotifyIcon resta aperto finche' la finestra che lo possiede e' in primo piano, quindi prima di
/// aprirlo si chiama <c>SetForegroundWindow</c> sull'HWND di questa finestra; senza, il primo click fuori dal menu
/// non lo chiuderebbe. La finestra e' larga 1x1, trasparente, fuori schermo e marcata WS_EX_TOOLWINDOW per non
/// comparire in Alt-Tab.
/// </summary>
public sealed class TrayMenuHost : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly Window _window;

    public TrayMenuHost()
    {
        _window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Opacity = 0,
            ShowInTaskbar = false,
            ShowActivated = false,
            Width = 1,
            Height = 1,
            Left = -32000,
            Top = -32000
        };
        _window.SourceInitialized += (_, _) => HideFromAltTab();
        _window.Show();
    }

    /// <summary>Apre il menu sul puntatore (click destro sull'icona del tray).</summary>
    public void Open(ContextMenu menu)
    {
        menu.Placement = PlacementMode.MousePoint;
        Show(menu);
    }

    /// <summary>Apre il menu in un punto dello schermo, in unita' indipendenti dal dispositivo (usato da --tray-menu).</summary>
    public void OpenAt(ContextMenu menu, double x, double y)
    {
        menu.Placement = PlacementMode.Absolute;
        menu.HorizontalOffset = x;
        menu.VerticalOffset = y;
        Show(menu);
    }

    private void Show(ContextMenu menu)
    {
        menu.PlacementTarget = _window;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        menu.IsOpen = true;
    }

    private void HideFromAltTab()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
    }

    public void Dispose() => _window.Close();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
