using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AIUsageMonitor.App.Tray;

/// <summary>Draws the tray glyph (three white bars) plus an optional colored status dot, at the requested pixel size.</summary>
public static class TrayIconRenderer
{
    public sealed class RenderedIcon : IDisposable
    {
        private IntPtr _handle;
        public Icon Icon { get; }
        internal RenderedIcon(IntPtr handle) { _handle = handle; Icon = Icon.FromHandle(handle); }
        public void Dispose()
        {
            Icon.Dispose();
            if (_handle != IntPtr.Zero) { DestroyIcon(_handle); _handle = IntPtr.Zero; }
        }
    }

    public static RenderedIcon Render(Color? dot, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var s = size / 16f;
            using var white = new SolidBrush(Color.White);
            FillRounded(g, white, 1.5f * s, 9f * s, 3.2f * s, 5.5f * s, 1f * s);
            FillRounded(g, white, 6.4f * s, 5f * s, 3.2f * s, 9.5f * s, 1f * s);
            FillRounded(g, white, 11.3f * s, 1.5f * s, 3.2f * s, 13f * s, 1f * s);
            if (dot is { } color)
            {
                using var ring = new SolidBrush(Color.FromArgb(235, 27, 27, 31));
                using var fill = new SolidBrush(color);
                g.FillEllipse(ring, 8f * s, 8f * s, 8f * s, 8f * s);
                g.FillEllipse(fill, 9.2f * s, 9.2f * s, 5.6f * s, 5.6f * s);
            }
        }
        return new RenderedIcon(bitmap.GetHicon());
    }

    private static void FillRounded(Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        using var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
