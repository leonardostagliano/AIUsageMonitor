using System.Windows;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;

namespace AIUsageMonitor.App.Notch;

/// <summary>
/// A secondary quota window of a card (the first one is the hero, see <see cref="AgentCardViewModel"/>): label,
/// percent, a bar in the tone of the window and the time to its reset. Spec 11: while the snapshot is not fresh the
/// bar turns to the "stale" tone, the only signal left besides the status pill.
/// </summary>
public sealed class WindowRowViewModel : ObservableObject
{
    private UsageWindow _window;
    private UsageStatus _status;
    private string? _resetCaption;

    public WindowRowViewModel(UsageWindow window, UsageStatus status, DateTimeOffset now)
    {
        _window = window;
        _status = status;
        Tick(now);
    }

    public string Label => _window.Label;
    public double Percent => _window.Percent;
    public string PercentText => $"{_window.Percent:0}%";
    public UsageTone Tone => NotchPresentation.ToneOf(_window, _status);

    public string? ResetCaption
    {
        get => _resetCaption;
        private set { if (Set(ref _resetCaption, value)) Raise(nameof(ResetVisibility)); }
    }

    public Visibility ResetVisibility => ResetCaption is null ? Visibility.Collapsed : Visibility.Visible;

    public void Update(UsageWindow window, UsageStatus status, DateTimeOffset now)
    {
        _window = window;
        _status = status;
        Raise(nameof(Label)); Raise(nameof(Percent)); Raise(nameof(PercentText)); Raise(nameof(Tone));
        Tick(now);
    }

    public void Tick(DateTimeOffset now) => ResetCaption = NotchPresentation.ResetCaption(_window, now);
}
