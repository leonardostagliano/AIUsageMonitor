using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Notch;

public sealed class SessionRowViewModel : ObservableObject
{
    private SessionState _session;
    private string _elapsedText = "";

    public SessionRowViewModel(SessionState session, DateTimeOffset now)
    {
        _session = session;
        Tick(now);
    }

    public string SessionId => _session.SessionId;
    public string Name => _session.DisplayName;
    public string PhaseLabel => _session.PhaseLabel;
    public Brush DotBrush => PhaseVisuals.Brush(_session.Phase);
    public bool IsPulsing => _session.Phase == SessionPhase.Working;
    public string Tooltip => string.Join("\n", new[] { _session.Cwd, _session.Message }.Where(s => !string.IsNullOrEmpty(s)));
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    public void Update(SessionState session, DateTimeOffset now)
    {
        _session = session;
        Raise(nameof(Name)); Raise(nameof(PhaseLabel)); Raise(nameof(DotBrush)); Raise(nameof(IsPulsing)); Raise(nameof(Tooltip));
        Tick(now);
    }

    public void Tick(DateTimeOffset now) => ElapsedText = CountdownFormatter.Since(_session.LastEventAt, now);
}
