using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AIUsageMonitor.App.Tray;

/// <summary>Draws the tray glyph (a usage gauge on a dark plate) plus an optional colored status dot, at the requested pixel size.</summary>
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
            DrawGauge(g, size);
            if (dot is { } color)
            {
                var s = size / 16f;
                using var ring = new SolidBrush(Color.FromArgb(235, 27, 27, 31));
                using var fill = new SolidBrush(color);
                g.FillEllipse(ring, 8f * s, 8f * s, 8f * s, 8f * s);
                g.FillEllipse(fill, 9.2f * s, 9.2f * s, 5.6f * s, 5.6f * s);
            }
        }
        return new RenderedIcon(bitmap.GetHicon());
    }

    /// <summary>Piastra scura arrotondata + arco bianco a 270 gradi con lancetta: il "tachimetro" dell'app.</summary>
    private static void DrawGauge(Graphics g, int size)
    {
        var f = size;
        var plateRadius = 0.22f * f;

        using (var plate = new SolidBrush(Color.FromArgb(255, 27, 27, 31)))
        using (var path = RoundedRect(0f, 0f, f, f, plateRadius))
            g.FillPath(plate, path);

        if (size >= 32)
        {
            using var border = new Pen(Color.FromArgb(0x33, 255, 255, 255), 1f);
            using var path = RoundedRect(0.5f, 0.5f, f - 1f, f - 1f, plateRadius - 0.5f);
            g.DrawPath(border, path);
        }

        var cx = 0.50f * f;
        var cy = 0.54f * f;
        var radius = 0.30f * f;
        var arc = new RectangleF(cx - radius, cy - radius, radius * 2f, radius * 2f);
        var stroke = size <= 16 ? 3f : 0.12f * f;

        if (size >= 48)
        {
            // Il binario tenue copre tutti i 270 gradi, l'arco bianco solo 200: il gauge si legge "in parte usato".
            using var track = RoundPen(Color.FromArgb(0x33, 255, 255, 255), stroke);
            g.DrawArc(track, arc, 135f, 270f);
            using var ring = RoundPen(Color.White, stroke);
            g.DrawArc(ring, arc, 135f, 200f);
        }
        else
        {
            using var ring = RoundPen(Color.White, stroke);
            g.DrawArc(ring, arc, 135f, 270f);
        }

        // 22% del lato, accorciata quanto basta perche' la punta non tocchi l'arco (conta solo alle taglie piccole).
        var needleLength = MathF.Min(0.22f * f, radius - stroke / 2f - MathF.Max(1f, 0.02f * f));
        var angle = 200f * MathF.PI / 180f;
        using (var needle = RoundPen(Color.White, MathF.Max(1.5f, 0.08f * f)))
            g.DrawLine(needle, cx, cy, cx + needleLength * MathF.Cos(angle), cy + needleLength * MathF.Sin(angle));

        var hub = 0.06f * f;
        using (var hubBrush = new SolidBrush(Color.White))
            g.FillEllipse(hubBrush, cx - hub, cy - hub, hub * 2f, hub * 2f);
    }

    private static Pen RoundPen(Color color, float width) =>
        new(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        r = MathF.Max(0.01f, MathF.Min(r, MathF.Min(w, h) / 2f));
        var d = r * 2f;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
