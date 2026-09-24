using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using AIUsageMonitor.Core.Presentation;

namespace AIUsageMonitor.App.Controls;

/// <summary>
/// Rounded usage bar drawn in <see cref="OnRender"/>: the track, the filled part in the gradient of its
/// <see cref="Tone"/>, and an optional sheen that sweeps the filled part every 2.8 s. A new value fills in 300 ms from
/// the previous one; the first value, and every value while animations are off, is drawn at once.
/// </summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0d, (d, e) => ((UsageBar)d).OnValueChanged((double)e.NewValue)));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(UsageTone), typeof(UsageBar),
        new FrameworkPropertyMetadata(UsageTone.Normal, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowSheenProperty = DependencyProperty.Register(
        nameof(ShowSheen), typeof(bool), typeof(UsageBar),
        new FrameworkPropertyMetadata(false, (d, _) => ((UsageBar)d).UpdateSheen()));

    private static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        "Fraction", typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SheenPhaseProperty = DependencyProperty.Register(
        "SheenPhase", typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private bool _hasValue;
    private bool _sheenRunning;

    public UsageBar()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) =>
        {
            MotionSettings.Instance.PropertyChanged += OnMotionChanged;
            UpdateSheen();
        };
        Unloaded += (_, _) =>
        {
            MotionSettings.Instance.PropertyChanged -= OnMotionChanged;
            StopSheen();
        };
        IsVisibleChanged += (_, _) => UpdateSheen();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public UsageTone Tone { get => (UsageTone)GetValue(ToneProperty); set => SetValue(ToneProperty, value); }
    public bool ShowSheen { get => (bool)GetValue(ShowSheenProperty); set => SetValue(ShowSheenProperty, value); }

    private void OnMotionChanged(object? sender, PropertyChangedEventArgs e) => UpdateSheen();

    private void OnValueChanged(double value)
    {
        var target = Math.Clamp(double.IsFinite(value) ? value : 0, 0, 100) / 100;
        if (!_hasValue || !IsLoaded || !MotionSettings.IsEnabled)
        {
            _hasValue = true;
            BeginAnimation(FractionProperty, null);
            SetValue(FractionProperty, target);
            return;
        }
        // No From: the fill starts from what is on screen, even halfway through a previous animation.
        BeginAnimation(FractionProperty, new DoubleAnimation(target, MotionSettings.FillDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void UpdateSheen()
    {
        if (!(ShowSheen && IsLoaded && IsVisible && MotionSettings.IsEnabled))
        {
            StopSheen();
            return;
        }
        if (_sheenRunning) return;
        var loop = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromSeconds(2.8))) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(loop, MotionSettings.LoopFrameRate);
        _sheenRunning = true;
        BeginAnimation(SheenPhaseProperty, loop);
    }

    private void StopSheen()
    {
        if (!_sheenRunning) return;
        _sheenRunning = false;
        BeginAnimation(SheenPhaseProperty, null);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var radius = height / 2;
        dc.DrawRoundedRectangle(Resource("BarTrack", Brushes.DimGray), null, new Rect(0, 0, width, height), radius, radius);

        var fraction = (double)GetValue(FractionProperty);
        if (fraction <= 0) return;
        // A sliver still reads as a rounded dot: the fill is never narrower than the bar is tall.
        var fill = new Rect(0, 0, Math.Min(width, Math.Max(height, width * fraction)), height);
        dc.DrawRoundedRectangle(ToneBrush(Tone), null, fill, radius, radius);

        if (!_sheenRunning) return;
        var phase = (double)GetValue(SheenPhaseProperty);
        if (phase >= 0.6) return; // the band crosses in the first 60 % of the cycle, then rests
        var band = fill.Width * 0.4;
        var x = -band + (fill.Width + band) * (phase / 0.6);
        dc.PushClip(new RectangleGeometry(fill, radius, radius));
        dc.DrawRectangle(Resource("BarSheen", Brushes.Transparent), null, new Rect(x, 0, band, height));
        dc.Pop();
    }

    private Brush ToneBrush(UsageTone tone) => tone switch
    {
        UsageTone.Warning => Resource("ToneWarningBrush", Brushes.Orange),
        UsageTone.Critical => Resource("ToneCriticalBrush", Brushes.IndianRed),
        UsageTone.Stale => Resource("ToneStaleBrush", Brushes.Gray),
        _ => Resource("ToneNormalBrush", Brushes.MediumSeaGreen)
    };

    private Brush Resource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;
}
