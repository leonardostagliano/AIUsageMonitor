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
/// The sheen is a loop, so it is kept cheap: its phase holds still while the band rests (no change, no render), it
/// never runs on an empty bar, and the brushes and the clip are not looked up or rebuilt on every frame.
/// </summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0d, (d, e) => ((UsageBar)d).OnValueChanged((double)e.NewValue)));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone), typeof(UsageTone), typeof(UsageBar),
        new FrameworkPropertyMetadata(UsageTone.Normal, FrameworkPropertyMetadataOptions.AffectsRender,
            (d, _) => ((UsageBar)d)._toneBrush = null));

    public static readonly DependencyProperty ShowSheenProperty = DependencyProperty.Register(
        nameof(ShowSheen), typeof(bool), typeof(UsageBar),
        new FrameworkPropertyMetadata(false, (d, _) => ((UsageBar)d).UpdateSheen()));

    private static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        "Fraction", typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty SheenPhaseProperty = DependencyProperty.Register(
        "SheenPhase", typeof(double), typeof(UsageBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The band crosses the fill in the first 1.68 s (60 %) of each 2.8 s cycle, then rests.</summary>
    private static readonly TimeSpan SheenCross = TimeSpan.FromSeconds(1.68);
    private static readonly TimeSpan SheenCycle = TimeSpan.FromSeconds(2.8);

    private bool _hasValue;
    private bool _sheenRunning;
    private double _target;
    private Brush? _trackBrush;
    private Brush? _toneBrush;
    private Brush? _sheenBrush;
    private RectangleGeometry? _clip;

    public UsageBar()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        Loaded += (_, _) =>
        {
            // Resources resolve through the tree: look them up again once the bar is (re)attached.
            _trackBrush = _toneBrush = _sheenBrush = null;
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
        _target = target;
        UpdateSheen(); // an empty bar has nothing to shine on
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
        if (!(ShowSheen && _target > 0 && IsLoaded && IsVisible && MotionSettings.IsEnabled))
        {
            StopSheen();
            return;
        }
        if (_sheenRunning) return;
        // Phase 0..1 is the crossing; after it the last key frame holds 1 until the cycle ends. A value that does not
        // change raises no property change, so the 40 % rest of every cycle costs no render.
        var loop = new DoubleAnimationUsingKeyFrames
        {
            Duration = new Duration(SheenCycle),
            RepeatBehavior = RepeatBehavior.Forever,
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(SheenCross)),
            }
        };
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
        dc.DrawRoundedRectangle(Cached(ref _trackBrush, "BarTrack", Brushes.DimGray), null, new Rect(0, 0, width, height), radius, radius);

        var fraction = (double)GetValue(FractionProperty);
        if (fraction <= 0) return;
        // A sliver still reads as a rounded dot: the fill is never narrower than the bar is tall.
        var fill = new Rect(0, 0, Math.Min(width, Math.Max(height, width * fraction)), height);
        dc.DrawRoundedRectangle(ToneBrush(), null, fill, radius, radius);

        if (!_sheenRunning) return;
        var phase = (double)GetValue(SheenPhaseProperty);
        if (phase <= 0 || phase >= 1) return; // the band is off the fill at both ends of the crossing
        var band = fill.Width * 0.4;
        var x = -band + (fill.Width + band) * phase;
        // The fill does not move while the band crosses it: one frozen clip serves every frame of the crossing.
        if (_clip is null || _clip.Rect != fill || _clip.RadiusX != radius)
        {
            _clip = new RectangleGeometry(fill, radius, radius);
            _clip.Freeze();
        }
        dc.PushClip(_clip);
        dc.DrawRectangle(Cached(ref _sheenBrush, "BarSheen", Brushes.Transparent), null, new Rect(x, 0, band, height));
        dc.Pop();
    }

    private Brush ToneBrush() => Tone switch
    {
        UsageTone.Warning => Cached(ref _toneBrush, "ToneWarningBrush", Brushes.Orange),
        UsageTone.Critical => Cached(ref _toneBrush, "ToneCriticalBrush", Brushes.IndianRed),
        UsageTone.Stale => Cached(ref _toneBrush, "ToneStaleBrush", Brushes.Gray),
        _ => Cached(ref _toneBrush, "ToneNormalBrush", Brushes.MediumSeaGreen)
    };

    /// <summary>
    /// The theme brush for <paramref name="key"/>, remembered in <paramref name="slot"/> once found. A miss (not in a
    /// tree with the resources yet) returns the fallback without remembering it, so the next render tries again.
    /// </summary>
    private Brush Cached(ref Brush? slot, string key, Brush fallback)
    {
        if (slot is not null) return slot;
        if (TryFindResource(key) is not Brush found) return fallback;
        slot = found;
        return found;
    }
}
