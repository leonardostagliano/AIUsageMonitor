using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Pricing;
using AIUsageMonitor.Core.Usage;

namespace AIUsageMonitor.App.Notch;

public sealed class AgentCardViewModel : ObservableObject
{
    /// <summary>Refresh a comando: al massimo 15 s di attesa, poi 10 s prima del click successivo.</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(10);

    private readonly AppServices _services;
    private readonly ManualRefreshGate _refresh = new(TimeProvider.System, RefreshCooldown, RefreshTimeout);
    private bool _wasCoolingDown;
    private string? _planLabel;
    private string? _extraUsage;
    private string? _statusMessage;
    private string? _costText;
    private string? _costTooltip;
    private bool _hookWarning;
    private Brush _aggregateBrush = PhaseVisuals.Brush(null);
    private Brush _aggregateStroke = PhaseVisuals.Brush(SessionPhase.Idle);

    public AgentCardViewModel(AgentKind agent, AppServices services, Geometry icon)
    {
        Agent = agent;
        _services = services;
        Icon = icon;
        InstallHooksCommand = new RelayCommand(InstallHooks);
        // Sempre abilitato: un Button disabilitato non riceve il click, che finirebbe sul pannello e fisserebbe o
        // sbloccherebbe il notch. Durante refresh e cooldown il click viene ignorato da ManualRefreshGate.
        RefreshCommand = new RelayCommand(StartRefresh);
        _refresh.StateChanged += () => UiDispatcher.Post(RaiseRefreshState);
        Refresh();
    }

    public AgentKind Agent { get; }
    public string Name => Agent.DisplayName();
    public Geometry Icon { get; }
    public ObservableCollection<WindowRowViewModel> Windows { get; } = new();
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();
    public ICommand InstallHooksCommand { get; }
    public ICommand RefreshCommand { get; }

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

    /// <summary>"Costo API equivalente ≈ 12,40 €": sessioni della card piu' tutti i loro agenti; null con i costi nascosti.</summary>
    public string? CostText { get => _costText; private set { if (Set(ref _costText, value)) Raise(nameof(CostVisibility)); } }
    public string? CostTooltip { get => _costTooltip; private set => Set(ref _costTooltip, value); }
    public Visibility CostVisibility => CostText is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Guida la rotazione dell'icona ⟳.</summary>
    public bool IsRefreshing => _refresh.IsRefreshing;

    /// <summary>Icona attenuata durante il cooldown, piena quando il click e' accettato o mentre gira.</summary>
    public double RefreshOpacity => _refresh.IsRefreshing || _refresh.CanStart ? 1.0 : 0.4;

    public string RefreshTooltip
    {
        get
        {
            var lines = new List<string> { $"Aggiorna {Name}" };
            if (_refresh.IsRefreshing)
            {
                lines.Add("Aggiornamento in corso…");
            }
            else
            {
                if (_refresh.LastCompletedAt is { } at) lines.Add($"Aggiornato alle {at.ToLocalTime():HH:mm:ss}");
                var left = _refresh.CooldownRemaining;
                if (left > TimeSpan.Zero) lines.Add($"Di nuovo tra {Math.Ceiling(left.TotalSeconds):0} s");
            }
            return string.Join("\n", lines);
        }
    }

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
        var pricing = _services.CurrentPricing();
        var sessions = _services.Sessions.Sessions.Where(s => s.Agent == Agent).ToList();
        SyncSessions(sessions, now, pricing);
        UpdateCost(sessions, pricing);

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
        // Il cooldown finisce senza alcun evento: il tick di 1 s del notch aggiorna icona e tooltip fino alla fine.
        var coolingDown = _refresh.CooldownRemaining > TimeSpan.Zero;
        if (coolingDown || _wasCoolingDown) RaiseRefreshState();
        _wasCoolingDown = coolingDown;
    }

    private void StartRefresh() =>
        _ = _refresh.TryRunAsync(() => _services.RefreshAgentAsync(Agent), ex => _services.Log.Error($"Refresh {Agent}", ex));

    private void RaiseRefreshState()
    {
        Raise(nameof(IsRefreshing));
        Raise(nameof(RefreshOpacity));
        Raise(nameof(RefreshTooltip));
    }

    private void UpdateCost(IReadOnlyList<SessionState> sessions, PricingSnapshot? pricing)
    {
        if (pricing is null)
        {
            CostText = null;
            CostTooltip = null;
            return;
        }
        var ledger = sessions.Aggregate(UsageLedger.Empty, (acc, s) => acc + (s.Ledger ?? UsageLedger.Empty) + s.SubagentLedger);
        var cost = pricing.Cost(ledger);
        var text = CostFormatter.Short(cost);
        CostText = text is null ? null : $"Costo API equivalente {text}";
        CostTooltip = text is null ? null : CostTooltips.Card(cost, pricing);
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

    private void SyncSessions(IReadOnlyList<SessionState> sessions, DateTimeOffset now, PricingSnapshot? pricing)
    {
        var byId = Sessions.ToDictionary(s => s.SessionId);
        for (var i = 0; i < sessions.Count; i++)
        {
            if (byId.TryGetValue(sessions[i].SessionId, out var existing))
            {
                existing.Update(sessions[i], now, pricing);
                var currentIndex = Sessions.IndexOf(existing);
                if (currentIndex != i) Sessions.Move(currentIndex, i);
            }
            else
            {
                Sessions.Insert(i, new SessionRowViewModel(sessions[i], _services, now, pricing));
            }
        }
        while (Sessions.Count > sessions.Count) Sessions.RemoveAt(Sessions.Count - 1);
    }

    // Stesso percorso del menu tray: AppServices logga, invalida lo stato in cache e mostra il messaggio
    // (Detail per Codex e per ogni esito diverso da Installed, incluso ConfigInvalid con il percorso del file).
    private void InstallHooks() => _services.InstallHooks(Agent);
}
