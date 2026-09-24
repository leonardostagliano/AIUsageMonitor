using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AIUsageMonitor.App.Controls;

/// <summary>
/// Slides the knob of a switch-styled <see cref="ToggleButton"/> (the CheckBox style in Controls.xaml) in 150 ms. The
/// knob is the template element named <c>Knob</c>, and the template's triggers alone decide where it rests: left when
/// off, right when on. On a toggle this only plays a <see cref="FillBehavior.Stop"/> translation from where the knob
/// was on screen to that place. Nothing plays on first draw, before the control is loaded, or while animations are
/// off, and turning animations off in Windows mid-slide cannot leave the knob out of place.
/// </summary>
public static class SwitchKnob
{
    /// <summary>Distance in DIP between the knob's off and on places; 0 (the default) turns the slide off.</summary>
    public static readonly DependencyProperty TravelProperty = DependencyProperty.RegisterAttached(
        "Travel", typeof(double), typeof(SwitchKnob), new PropertyMetadata(0d, OnTravelChanged));

    /// <summary>Which side the knob last rested on. Unchecked can follow an indeterminate state, where it did not move.</summary>
    private static readonly DependencyProperty IsOnProperty = DependencyProperty.RegisterAttached(
        "IsOn", typeof(bool), typeof(SwitchKnob), new PropertyMetadata(false));

    public static double GetTravel(DependencyObject element) => (double)element.GetValue(TravelProperty);

    public static void SetTravel(DependencyObject element, double value) => element.SetValue(TravelProperty, value);

    private static void OnTravelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ToggleButton toggle) return;
        toggle.Checked -= OnToggled;
        toggle.Unchecked -= OnToggled;
        toggle.Indeterminate -= OnToggled;
        if ((double)e.NewValue == 0) return;
        toggle.SetValue(IsOnProperty, toggle.IsChecked == true);
        toggle.Checked += OnToggled;
        toggle.Unchecked += OnToggled;
        toggle.Indeterminate += OnToggled;
    }

    private static void OnToggled(object sender, RoutedEventArgs e)
    {
        // These events bubble: a toggle inside this one's content must not slide this knob.
        if (!ReferenceEquals(sender, e.OriginalSource)) return;
        var toggle = (ToggleButton)sender;
        var on = toggle.IsChecked == true;
        var wasOn = (bool)toggle.GetValue(IsOnProperty);
        toggle.SetValue(IsOnProperty, on);
        if (on == wasOn || !toggle.IsLoaded || !MotionSettings.IsEnabled) return;
        if (toggle.Template?.FindName("Knob", toggle) is not UIElement knob) return;

        // A transform of our own: one declared in the template can come back frozen.
        if (knob.RenderTransform is not TranslateTransform shift || shift.IsFrozen)
            knob.RenderTransform = shift = new TranslateTransform();

        // The triggers have already moved the knob's resting place by the travel. Starting one travel back, plus
        // whatever offset an interrupted slide still shows, keeps the knob where it is on screen and eases it home.
        var travel = GetTravel(toggle);
        var from = shift.X + (on ? -travel : travel);
        shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, MotionSettings.KnobDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        });
    }
}
