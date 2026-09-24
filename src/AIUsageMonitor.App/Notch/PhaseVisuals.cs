using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;

namespace AIUsageMonitor.App.Notch;

public static class PhaseVisuals
{
    // Uguali a Success, Warning, Danger e Idle di Theme.xaml: pallini della tray e pennelli di fase costruiti in codice.
    public static readonly Color Green = Color.FromRgb(0x4A, 0xDE, 0x80);
    public static readonly Color Amber = Color.FromRgb(0xFB, 0xBF, 0x24);
    public static readonly Color Red = Color.FromRgb(0xF8, 0x71, 0x71);
    public static readonly Color Grey = Color.FromRgb(0x71, 0x71, 0x7A);

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

    /// <summary>Solid colour of a tone: avatar ring, dots, summary pill dot.</summary>
    public static Brush ToneBrush(PhaseTone tone) => Theme(tone switch
    {
        PhaseTone.Working => "Success",
        PhaseTone.NeedsInput => "Warning",
        PhaseTone.Error => "Danger",
        _ => "Idle"
    });

    /// <summary>Text colour of a tone (subtitle, pill text); 4.5:1 on cards and tiles per ColorContrastTests.</summary>
    public static Brush ToneText(PhaseTone tone) => Theme(tone switch
    {
        PhaseTone.Working => "SuccessText",
        PhaseTone.NeedsInput => "WarningText",
        PhaseTone.Error => "DangerText",
        _ => "TextMuted"
    });

    /// <summary>14 % fill of a tone: avatar background, summary pill background.</summary>
    public static Brush ToneFill(PhaseTone tone) => Theme(tone switch
    {
        PhaseTone.Working => "SuccessFill",
        PhaseTone.NeedsInput => "WarningFill",
        PhaseTone.Error => "DangerFill",
        _ => "IdleFill"
    });

    private static Brush Theme(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
