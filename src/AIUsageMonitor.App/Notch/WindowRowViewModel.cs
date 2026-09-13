using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Notch;

public sealed class WindowRowViewModel : ObservableObject
{
    private UsageWindow _window;
    private UsageStatus _status;
    private string _resetText = "";

    public WindowRowViewModel(UsageWindow window, UsageStatus status, DateTimeOffset now)
    {
        _window = window;
        _status = status;
        Tick(now);
    }

    public string Label => _window.Label;
    public string PercentText => $"{_window.Percent:0}%";

    /// <summary>
    /// Spec 11: quando lo snapshot non e' aggiornato (token scaduto, Stale, nessun dato) le barre mostrano gli ultimi
    /// valori in grigio, cosi' i numeri fermi si distinguono a colpo d'occhio da quelli live; <c>UsageService.Merge</c>
    /// conserva finestre e severita' precedenti proprio in questi casi, quindi il colore e' l'unico segnale rimasto
    /// oltre alla riga di stato.
    /// </summary>
    public Brush BarBrush => _status == UsageStatus.Ok
        ? PhaseVisuals.SeverityBrush(_window.Severity)
        : PhaseVisuals.Brush(SessionPhase.Idle);

    public GridLength FilledStar => new(Math.Max(0.001, _window.Percent), GridUnitType.Star);
    public GridLength EmptyStar => new(Math.Max(0.001, 100 - _window.Percent), GridUnitType.Star);
    public string ResetText { get => _resetText; private set => Set(ref _resetText, value); }

    public void Update(UsageWindow window, UsageStatus status, DateTimeOffset now)
    {
        _window = window;
        _status = status;
        Raise(nameof(Label)); Raise(nameof(PercentText)); Raise(nameof(BarBrush)); Raise(nameof(FilledStar)); Raise(nameof(EmptyStar));
        Tick(now);
    }

    public void Tick(DateTimeOffset now) =>
        ResetText = _window.ResetsAt is { } reset ? CountdownFormatter.Until(reset, now) : "";
}
