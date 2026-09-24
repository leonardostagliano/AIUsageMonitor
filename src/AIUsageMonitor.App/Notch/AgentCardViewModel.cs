using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;
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
    private string? _costTooltip;
    private bool _hookWarning;
    private Brush _aggregateBrush = PhaseVisuals.Brush(null);
    private UsageWindow? _hero;
    private UsageStatus _status;
    private SessionPhase? _phase;
    private CostDisplay? _cost;
    private double _heroPercent = double.NaN;
    private UsageTone _heroTone = UsageTone.Stale;
    private string _heroCaption = "";

    public AgentCardViewModel(AgentKind agent, AppServices services, Geometry icon)
    {
        Agent = agent;
        _services = services;
        Icon = icon;
        BrandBrush = ThemeBrush(agent == AgentKind.Claude ? "BrandClaude" : "BrandCodex");
        BrandGlow = ThemeBrush(agent == AgentKind.Claude ? "ClaudeGlow" : "CodexGlow");
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
    public ObservableCollection<SessionRowViewModel> Sessions { get; } = new();
    public ICommand InstallHooksCommand { get; }
    public ICommand RefreshCommand { get; }

    public string? PlanLabel { get => _planLabel; private set { if (Set(ref _planLabel, value)) Raise(nameof(PlanVisibility)); } }
    public Visibility PlanVisibility => string.IsNullOrEmpty(PlanLabel) ? Visibility.Collapsed : Visibility.Visible;
    public string? ExtraUsage { get => _extraUsage; private set { if (Set(ref _extraUsage, value)) Raise(nameof(ExtraVisibility)); } }
    public Visibility ExtraVisibility => string.IsNullOrEmpty(ExtraUsage) ? Visibility.Collapsed : Visibility.Visible;

    public string? StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (!Set(ref _statusMessage, value)) return;
            Raise(nameof(NoDataCaption)); Raise(nameof(StatusPillVisibility));
        }
    }

    public bool HookWarning { get => _hookWarning; private set { if (Set(ref _hookWarning, value)) Raise(nameof(HookWarningVisibility)); } }
    public Visibility HookWarningVisibility => HookWarning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoSessionsVisibility => Sessions.Count == 0 && !HookWarning ? Visibility.Visible : Visibility.Collapsed;
    public Brush AggregateBrush { get => _aggregateBrush; private set => Set(ref _aggregateBrush, value); }

    /// <summary>Colore dell'agente: cerchio dell'icona nella card e nella linguetta.</summary>
    public Brush BrandBrush { get; }

    /// <summary>Alone radiale del colore dell'agente nell'angolo della card (spec 6.3, punto 6).</summary>
    public Brush BrandGlow { get; }

    /// <summary>Le finestre dopo la prima, come barre (la prima e' il numero grande).</summary>
    public ObservableCollection<WindowRowViewModel> OtherWindows { get; } = new();

    public double HeroPercent
    {
        get => _heroPercent;
        private set { if (Set(ref _heroPercent, value)) Raise(nameof(TabPercent)); }
    }

    public UsageTone HeroTone
    {
        get => _heroTone;
        private set { if (Set(ref _heroTone, value)) Raise(nameof(TabTone)); }
    }

    public string HeroCaption { get => _heroCaption; private set => Set(ref _heroCaption, value); }

    public bool HasWindows => _hero is not null;
    public Visibility HeroVisibility => HasWindows ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoDataVisibility => HasWindows ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Senza finestre il messaggio di stato sta sotto il "—" invece che nella pillola; uno snapshot fresco senza
    /// finestre non ha messaggio e mostra "Nessuna finestra di quota" (in TextMuted, vedi NotchTemplates.xaml).
    /// </summary>
    public string NoDataCaption => NotchPresentation.NoDataCaption(StatusMessage);

    public Visibility StatusPillVisibility => HasWindows && !string.IsNullOrEmpty(StatusMessage) ? Visibility.Visible : Visibility.Collapsed;

    // Linguetta: anello della prima finestra e pallino dello stato aggregato (spec 6.1).
    public double TabPercent => HeroPercent;
    public UsageTone TabTone => HeroTone;
    public Visibility BadgeVisibility => _phase is null ? Visibility.Collapsed : Visibility.Visible;
    public bool IsWorking => _phase == SessionPhase.Working;

    // Costo della card: lead, importo che scorre, oppure "costo n/d" (NotchPresentation.CostParts).
    public string CostLead => _cost?.Lead ?? "";
    public double CostValue => _cost?.Amount is { } amount ? (double)amount : double.NaN;
    public string? CostFallback => _cost?.Fallback;
    public string? CostTooltip { get => _costTooltip; private set => Set(ref _costTooltip, value); }
    public Visibility CostVisibility => _cost is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CostAmountVisibility => _cost?.Amount is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CostFallbackVisibility => _cost?.Fallback is null ? Visibility.Collapsed : Visibility.Visible;

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
        StatusMessage = NotchPresentation.StatusMessage(snapshot);

        _status = snapshot?.Status ?? UsageStatus.NoData;
        var (hero, others) = NotchPresentation.SplitWindows(snapshot?.Windows ?? Array.Empty<UsageWindow>());
        SetHero(hero, now);
        SyncWindows(others, _status, now);

        var pricing = _services.CurrentPricing();
        var sessions = _services.Sessions.Sessions.Where(s => s.Agent == Agent).ToList();
        SyncSessions(sessions, now, pricing);
        UpdateCost(sessions, pricing);

        _phase = _services.Sessions.AggregatePhase(Agent);
        AggregateBrush = PhaseVisuals.Brush(_phase);
        Raise(nameof(BadgeVisibility)); Raise(nameof(IsWorking));

        HookWarning = _services.HookStatus(Agent).Status != HookStatus.Installed;
        Raise(nameof(NoSessionsVisibility));
    }

    private void SetHero(UsageWindow? hero, DateTimeOffset now)
    {
        var had = HasWindows;
        _hero = hero;
        HeroPercent = hero?.Percent ?? double.NaN;
        HeroTone = hero is null ? UsageTone.Stale : NotchPresentation.ToneOf(hero, _status);
        HeroCaption = hero is null ? "" : NotchPresentation.HeroCaption(hero, now);
        if (had == HasWindows) return;
        Raise(nameof(HasWindows)); Raise(nameof(HeroVisibility)); Raise(nameof(NoDataVisibility)); Raise(nameof(StatusPillVisibility));
    }

    public void TickClocks()
    {
        var now = _services.Clock.UtcNow;
        if (_hero is not null) HeroCaption = NotchPresentation.HeroCaption(_hero, now);
        foreach (var w in OtherWindows) w.Tick(now);
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
        CostDisplay? display = null;
        string? tooltip = null;
        if (pricing is not null)
        {
            var ledger = sessions.Aggregate(UsageLedger.Empty, (acc, s) => acc + (s.Ledger ?? UsageLedger.Empty) + s.SubagentLedger);
            var cost = pricing.Cost(ledger);
            display = NotchPresentation.CostParts(cost);
            if (display is not null) tooltip = CostTooltips.Card(cost, pricing);
        }
        CostTooltip = tooltip;
        if (display == _cost) return;
        _cost = display;
        Raise(nameof(CostLead)); Raise(nameof(CostValue)); Raise(nameof(CostFallback));
        Raise(nameof(CostVisibility)); Raise(nameof(CostAmountVisibility)); Raise(nameof(CostFallbackVisibility));
    }

    private static Brush ThemeBrush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private void SyncWindows(IReadOnlyList<UsageWindow> windows, UsageStatus status, DateTimeOffset now)
    {
        for (var i = 0; i < windows.Count; i++)
        {
            if (i < OtherWindows.Count) OtherWindows[i].Update(windows[i], status, now);
            else OtherWindows.Add(new WindowRowViewModel(windows[i], status, now));
        }
        while (OtherWindows.Count > windows.Count) OtherWindows.RemoveAt(OtherWindows.Count - 1);
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
