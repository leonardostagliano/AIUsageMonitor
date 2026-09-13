using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Notch;

public sealed class AgentCardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string? _planLabel;
    private string? _extraUsage;
    private string? _statusMessage;
    private bool _hookWarning;
    private Brush _aggregateBrush = PhaseVisuals.Brush(null);
    private Brush _aggregateStroke = PhaseVisuals.Brush(SessionPhase.Idle);

    public AgentCardViewModel(AgentKind agent, AppServices services, Geometry icon)
    {
        Agent = agent;
        _services = services;
        Icon = icon;
        InstallHooksCommand = new RelayCommand(InstallHooks);
        Refresh();
    }

    public AgentKind Agent { get; }
    public string Name => Agent.DisplayName();
    public Geometry Icon { get; }
    public ObservableCollection<WindowRowViewModel> Windows { get; } = new();
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();
    public ICommand InstallHooksCommand { get; }

    public string? PlanLabel { get => _planLabel; private set { if (Set(ref _planLabel, value)) Raise(nameof(PlanVisibility)); } }
    public Visibility PlanVisibility => string.IsNullOrEmpty(PlanLabel) ? Visibility.Collapsed : Visibility.Visible;
    public string? ExtraUsage { get => _extraUsage; private set { if (Set(ref _extraUsage, value)) Raise(nameof(ExtraVisibility)); } }
    public Visibility ExtraVisibility => string.IsNullOrEmpty(ExtraUsage) ? Visibility.Collapsed : Visibility.Visible;
    public string? StatusMessage { get => _statusMessage; private set { if (Set(ref _statusMessage, value)) Raise(nameof(StatusVisibility)); } }
    public Visibility StatusVisibility => string.IsNullOrEmpty(StatusMessage) ? Visibility.Collapsed : Visibility.Visible;
    public bool HookWarning { get => _hookWarning; private set { if (Set(ref _hookWarning, value)) Raise(nameof(HookWarningVisibility)); } }
    public Visibility HookWarningVisibility => HookWarning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSessionsVisibility => Sessions.Count == 0 && !HookWarning ? Visibility.Visible : Visibility.Collapsed;
    public Brush AggregateBrush { get => _aggregateBrush; private set => Set(ref _aggregateBrush, value); }
    public Brush AggregateStroke { get => _aggregateStroke; private set => Set(ref _aggregateStroke, value); }

    public void Refresh()
    {
        var now = _services.Clock.UtcNow;
        _services.Usage.Current.TryGetValue(Agent, out var snapshot);

        PlanLabel = snapshot?.PlanLabel;
        ExtraUsage = snapshot?.ExtraUsage;
        StatusMessage = snapshot is null ? "In attesa del primo aggiornamento"
            : snapshot.Status == UsageStatus.Ok ? null
            : snapshot.StatusMessage;

        SyncWindows(snapshot?.Windows ?? Array.Empty<UsageWindow>(), snapshot?.Status ?? UsageStatus.NoData, now);
        SyncSessions(_services.Sessions.Sessions.Where(s => s.Agent == Agent).ToList(), now);

        var phase = _services.Sessions.AggregatePhase(Agent);
        AggregateBrush = PhaseVisuals.Brush(phase);
        AggregateStroke = phase is null ? PhaseVisuals.Brush(SessionPhase.Idle) : Brushes.Transparent;

        HookWarning = _services.HookStatus(Agent).Status != HookStatus.Installed;
        Raise(nameof(NoSessionsVisibility));
    }

    public void TickClocks()
    {
        var now = _services.Clock.UtcNow;
        foreach (var w in Windows) w.Tick(now);
        foreach (var s in Sessions) s.Tick(now);
    }

    private void SyncWindows(IReadOnlyList<UsageWindow> windows, UsageStatus status, DateTimeOffset now)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (i < Windows.Count) Windows[i].Update(windows[i], status, now);
            else Windows.Add(new WindowRowViewModel(windows[i], status, now));
        }
        while (Windows.Count > windows.Count) Windows.RemoveAt(Windows.Count - 1);
    }

    private void SyncSessions(IReadOnlyList<SessionState> sessions, DateTimeOffset now)
    {
        var byId = Sessions.ToDictionary(s => s.SessionId);
        for (var i = 0; i < sessions.Count; i++)
        {
            if (byId.TryGetValue(sessions[i].SessionId, out var existing))
            {
                existing.Update(sessions[i], now);
                var currentIndex = Sessions.IndexOf(existing);
                if (currentIndex != i) Sessions.Move(currentIndex, i);
            }
            else
            {
                Sessions.Insert(i, new SessionRowViewModel(sessions[i], now));
            }
        }
        while (Sessions.Count > sessions.Count) Sessions.RemoveAt(Sessions.Count - 1);
    }

    // Stesso percorso del menu tray: AppServices logga, invalida lo stato in cache e mostra il messaggio
    // (Detail per Codex e per ogni esito diverso da Installed, incluso ConfigInvalid con il percorso del file).
    private void InstallHooks() => _services.InstallHooks(Agent);
}
