using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Notch;

public sealed class WindowRowViewModel : ObservableObject
{
    private UsageWindow _window;
    private string _resetText = "";

    public WindowRowViewModel(UsageWindow window, DateTimeOffset now)
    {
        _window = window;
        Tick(now);
    }

    public string Label => _window.Label;
    public string PercentText => $"{_window.Percent:0}%";
    public Brush BarBrush => PhaseVisuals.SeverityBrush(_window.Severity);
    public GridLength FilledStar => new(Math.Max(0.001, _window.Percent), GridUnitType.Star);
    public GridLength EmptyStar => new(Math.Max(0.001, 100 - _window.Percent), GridUnitType.Star);
    public string ResetText { get => _resetText; private set => Set(ref _resetText, value); }

    public void Update(UsageWindow window, DateTimeOffset now)
    {
        _window = window;
        Raise(nameof(Label)); Raise(nameof(PercentText)); Raise(nameof(BarBrush)); Raise(nameof(FilledStar)); Raise(nameof(EmptyStar));
        Tick(now);
    }

    public void Tick(DateTimeOffset now) =>
        ResetText = _window.ResetsAt is { } reset ? CountdownFormatter.Until(reset, now) : "";
}
