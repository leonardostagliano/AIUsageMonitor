using System.Windows.Media;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Notch;

public static class PhaseVisuals
{
    public static readonly Color Green = Color.FromRgb(0x3F, 0xB9, 0x50);
    public static readonly Color Amber = Color.FromRgb(0xD2, 0x99, 0x22);
    public static readonly Color Red = Color.FromRgb(0xF8, 0x51, 0x49);
    public static readonly Color Grey = Color.FromRgb(0x8B, 0x8B, 0x93);

    public static string Label(SessionPhase? phase) => phase switch
    {
        SessionPhase.Working => "al lavoro",
        SessionPhase.NeedsInput => "attende input",
        SessionPhase.Idle => "finito",
        SessionPhase.Error => "errore",
        _ => "nessuna sessione"
    };

    public static Color MediaColor(SessionPhase? phase) => phase switch
    {
        SessionPhase.Working => Green,
        SessionPhase.NeedsInput => Amber,
        SessionPhase.Error => Red,
        SessionPhase.Idle => Grey,
        _ => Colors.Transparent
    };

    public static System.Drawing.Color? DrawingColor(SessionPhase? phase)
    {
        if (phase is null) return null;
        var c = MediaColor(phase);
        return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    public static Brush Brush(SessionPhase? phase) => Frozen(MediaColor(phase));

    public static Brush SeverityBrush(Severity severity) => severity switch
    {
        Severity.Critical => Frozen(Red),
        Severity.Warning => Frozen(Amber),
        _ => Frozen(Green)
    };

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
