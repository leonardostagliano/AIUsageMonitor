using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AIUsageMonitor.App.Controls;

/// <summary>
/// Integer field as "− value +": round buttons that repeat while held, the value itself editable. The value is always
/// within [<see cref="Minimum"/>, <see cref="Maximum"/>]; text that is not a number reverts to the current value.
/// </summary>
public partial class NumberStepper : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberStepper),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((NumberStepper)d).SyncText(), (d, v) => ((NumberStepper)d).Clamp((int)v)));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberStepper),
        new FrameworkPropertyMetadata(0, OnLimitChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberStepper),
        new FrameworkPropertyMetadata(100, OnLimitChanged));

    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(int), typeof(NumberStepper), new FrameworkPropertyMetadata(1));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(NumberStepper), new FrameworkPropertyMetadata(""));

    public NumberStepper()
    {
        InitializeComponent();
        SyncText();
    }

    public int Value { get => (int)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Minimum { get => (int)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public int Maximum { get => (int)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Step { get => (int)GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    private static void OnLimitChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        d.CoerceValue(ValueProperty);
        // The buttons depend on the limits too: a value of 0 that stays 0 when Minimum becomes -5000 can go down again.
        ((NumberStepper)d).SyncText();
    }

    private object Clamp(int value) => Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));

    private void SyncText()
    {
        Input.Text = Value.ToString(CultureInfo.CurrentCulture);
        Minus.IsEnabled = Value > Minimum;
        Plus.IsEnabled = Value < Maximum;
    }

    private void Minus_Click(object sender, RoutedEventArgs e) => StepBy(-Step);

    private void Plus_Click(object sender, RoutedEventArgs e) => StepBy(Step);

    /// <summary>
    /// Steps from what the field shows: a number typed but not committed yet (the buttons never take the focus, so
    /// nothing committed it) is adopted first, instead of being overwritten by a step from the old value.
    /// </summary>
    private void StepBy(int delta)
    {
        AdoptTyped();
        Value = Math.Clamp(Value + delta, Minimum, Math.Max(Minimum, Maximum));
        SyncText(); // also when the value did not change (at a limit), so out-of-range typed text does not linger
    }

    /// <summary>Takes a typed integer as the value (clamped); text that is not a number is left for Commit to revert.</summary>
    private void AdoptTyped()
    {
        if (int.TryParse(Input.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var typed)) Value = typed;
    }

    private void Commit()
    {
        AdoptTyped();
        SyncText(); // shows the clamped value, or reverts text that was not a number
    }

    private void Input_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: Commit(); break; // not Handled: Enter still reaches the window's default button (Salva)
            case Key.Up: StepBy(Step); e.Handled = true; break;
            case Key.Down: StepBy(-Step); e.Handled = true; break;
        }
    }
}
