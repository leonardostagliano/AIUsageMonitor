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
/// non lo chiuderebbe. La finestra e' larga 1x1, trasparente (Opacity 0, quindi anche click-through perche' e' una
/// layered window ad alpha zero) e marcata WS_EX_TOOLWINDOW per non comparire in Alt-Tab.
///
/// La finestra NON viene parcheggiata fuori da tutti i monitor: il popup del ContextMenu eredita il contesto DPI
/// dal proprio PlacementTarget, cioe' da questa finestra, e una finestra fuori da ogni monitor riceve il DPI di
/// sistema (96) invece di quello del monitor su cui il menu verra' disegnato. Con un monitor al 125% il menu
/// usciva quindi disegnato al 100% (200x217 px invece di 250x271) mentre il suo sottomenu, creato a partire dal
/// popup gia' a schermo, usciva al 125%. Prima di ogni apertura la finestra viene spostata sul puntatore
/// (<see cref="MoveTo"/>): Windows le manda WM_DPICHANGED, WPF aggiorna la scala e il popup nasce con il DPI giusto.
/// </summary>
public sealed class TrayMenuHost : IDisposable
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

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
            // Senza NoResize la finestra terrebbe il bordo di ridimensionamento e Windows la allargherebbe alla
            // minima dimensione tracciabile (16x16 px): visto che ora sta sotto il puntatore, la teniamo davvero 1x1.
            ResizeMode = ResizeMode.NoResize,
            Width = 1,
            Height = 1,
            // Origine del monitor primario: un punto reale, cosi' la finestra nasce gia' con un contesto DPI valido.
            // E' 1x1, completamente trasparente e non riceve click, quindi non da' fastidio a nessuno.
            Left = 0,
            Top = 0
        };
        _window.SourceInitialized += (_, _) => HideFromAltTab();
        _window.Show();
    }

    /// <summary>Apre il menu sul puntatore (click destro sull'icona del tray).</summary>
    public void Open(ContextMenu menu)
    {
        // Gli offset vanno azzerati, non solo la Placement: il ContextMenu e' uno solo per tutta la vita del processo
        // (lo costruisce TrayIconController) e WPF somma HorizontalOffset/VerticalOffset in ogni modalita', MousePoint
        // compresa. Senza questo, dopo un avvio con --tray-menu (che usa OpenAt) ogni click destro successivo aprirebbe
        // il menu a puntatore + meta' schermo, cioe' incollato a un bordo, fino al riavvio dell'app.
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
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
        // Prima il DPI, poi il primo piano, poi l'apertura: spostare la finestra dopo IsOpen non servirebbe a niente
        // perche' il popup ha gia' fissato la propria scala. Il puntatore e' il punto giusto per entrambe le vie di
        // apertura: quella vera (MousePoint) disegna esattamente li', e --tray-menu (Absolute, schermo primario)
        // resta corretto finche' il puntatore e' sullo stesso monitor.
        MoveToCursor();
        menu.PlacementTarget = _window;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        menu.IsOpen = true;
    }

    /// <summary>
    /// Sposta la finestra host sul puntatore in pixel fisici. Si usa SetWindowPos e non Window.Left/Top perche'
    /// quelle sono unita' indipendenti dal dispositivo convertite con la scala corrente della finestra, che e'
    /// proprio quella che stiamo cercando di correggere: in pixel fisici il punto di arrivo non e' ambiguo.
    /// </summary>
    private void MoveToCursor()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        if (!GetCursorPos(out var cursor)) return;
        SetWindowPos(handle, IntPtr.Zero, cursor.X, cursor.Y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private void HideFromAltTab()
    {
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
    }

    public void Dispose() => _window.Close();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
