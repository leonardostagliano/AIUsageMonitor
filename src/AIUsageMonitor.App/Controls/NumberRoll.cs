using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.App.Controls;

public enum NumberRollKind { Integer, Euro }

/// <summary>
/// Numbers that roll to their new value on a <see cref="TextBlock"/>: <c>c:NumberRoll.Value="{Binding HeroPercent}"</c>
/// with <c>c:NumberRoll.Kind</c>. The text changes over 400 ms only when the shown digits change and animations are on;
/// otherwise it is set at once. NaN shows nothing.
/// </summary>
public static class NumberRoll
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(NumberRoll), new PropertyMetadata(double.NaN, OnValueChanged));

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(NumberRollKind), typeof(NumberRoll), new PropertyMetadata(NumberRollKind.Integer, (d, _) => Render(d)));

    private static readonly DependencyProperty CurrentProperty = DependencyProperty.RegisterAttached(
        "Current", typeof(double), typeof(NumberRoll), new PropertyMetadata(double.NaN, (d, _) => Render(d)));

    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);
    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);
    public static NumberRollKind GetKind(DependencyObject element) => (NumberRollKind)element.GetValue(KindProperty);
    public static void SetKind(DependencyObject element, NumberRollKind value) => element.SetValue(KindProperty, value);

    public static string Format(double value, NumberRollKind kind) =>
        !double.IsFinite(value) ? ""
        : kind == NumberRollKind.Euro ? CostFormatter.Amount((decimal)value)
        : Math.Round(value).ToString("0", CultureInfo.InvariantCulture);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock text) return;
        var from = (double)text.GetValue(CurrentProperty); // what is on screen, even mid-animation
        var to = (double)e.NewValue;
        var kind = GetKind(text);
        if (!double.IsFinite(from) || !double.IsFinite(to) || !text.IsLoaded || !MotionSettings.IsEnabled
            || Format(from, kind) == Format(to, kind))
        {
            text.BeginAnimation(CurrentProperty, null);
            text.SetValue(CurrentProperty, to);
            return;
        }
        text.BeginAnimation(CurrentProperty, new DoubleAnimation(from, to, MotionSettings.CountDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private static void Render(DependencyObject d)
    {
        if (d is TextBlock text) text.Text = Format((double)text.GetValue(CurrentProperty), GetKind(text));
    }
}
