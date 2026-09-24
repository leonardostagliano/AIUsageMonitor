using System.ComponentModel;
using System.Windows;

namespace AIUsageMonitor.App.Controls;

/// <summary>
/// Whether the UI may animate: Windows' "Animation effects" (<see cref="SystemParameters.ClientAreaAnimation"/>),
/// re-read when the user changes it. XAML binds <c>{Binding Enabled, Source={x:Static c:MotionSettings.Instance}}</c>;
/// code reads <see cref="IsEnabled"/>. With it off nothing loops and values change at once.
/// </summary>
public sealed class MotionSettings : INotifyPropertyChanged
{
    /// <summary>Loops (sheen, pulses) are decoration: 30 fps keeps them cheap in an always-on-top window.</summary>
    public const int LoopFrameRate = 30;

    public static readonly Duration FillDuration = new(TimeSpan.FromMilliseconds(300));
    public static readonly Duration CountDuration = new(TimeSpan.FromMilliseconds(400));
    public static readonly Duration KnobDuration = new(TimeSpan.FromMilliseconds(150));

    public static MotionSettings Instance { get; } = new();

    private MotionSettings()
    {
        SystemParameters.StaticPropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
        };
    }

    public bool Enabled => SystemParameters.ClientAreaAnimation;

    public static bool IsEnabled => Instance.Enabled;

    public event PropertyChangedEventHandler? PropertyChanged;
}
