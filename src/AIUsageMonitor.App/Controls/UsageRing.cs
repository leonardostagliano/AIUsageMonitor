using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.Core.Presentation;

namespace AIUsageMonitor.App.Controls;

/// <summary>
/// Thin ring around an agent's icon in the notch tab: a full track (<c>RingTrack</c>, Idle at 30 %) and, from twelve
/// o'clock clockwise, an arc as long as <see cref="Value"/> percent in the solid colour of its <see cref="Tone"/>. NaN
/// draws the track only, which is the "off" ring of spec 6.1.
/// </summary>
public sealed class UsageRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(UsageTone), typeof(UsageRing),
        new FrameworkPropertyMetadata(UsageTone.Stale, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(UsageRing),
        new FrameworkPropertyMetadata(2d, FrameworkPropertyMetadataOptions.AffectsRender));

    public UsageRing() => IsHitTestVisible = false;

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public UsageTone Tone { get => (UsageTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= Thickness) return;
        var radius = (size - Thickness) / 2;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        dc.DrawEllipse(null, new Pen(Resource("RingTrack", Brushes.DimGray), Thickness), centre, radius, radius);

        if (!double.IsFinite(Value) || Value <= 0) return;
        var pen = new Pen(ToneBrush(Tone), Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var fraction = Math.Min(1, Value / 100);
        if (fraction >= 0.999)
        {
            dc.DrawEllipse(null, pen, centre, radius, radius);
            return;
        }
        var angle = fraction * 2 * Math.PI;
        var start = new Point(centre.X, centre.Y - radius);
        var end = new Point(centre.X + radius * Math.Sin(angle), centre.Y - radius * Math.Cos(angle));
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(start, isFilled: false, isClosed: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, angle > Math.PI, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }

    private Brush ToneBrush(UsageTone tone) => tone switch
    {
        UsageTone.Warning => Resource("Warning", Brushes.Orange),
        UsageTone.Critical => Resource("Danger", Brushes.IndianRed),
        UsageTone.Stale => Resource("Idle", Brushes.Gray),
        _ => Resource("Success", Brushes.MediumSeaGreen)
    };

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
