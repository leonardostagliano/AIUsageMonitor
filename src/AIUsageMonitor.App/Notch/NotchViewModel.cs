using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;

namespace AIUsageMonitor.App.Notch;

public sealed class NotchViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _tick;
    private bool _compact;
    private bool _isPanelOpen;
    private SummaryPill? _summary;

    public ObservableCollection<AgentCardViewModel> Agents { get; } = new();

    public NotchViewModel(AppServices services)
    {
        _services = services;
        Rebuild();
        services.StateChanged += () => UiDispatcher.Post(Refresh);
        services.Settings.Changed += _ => UiDispatcher.Post(Rebuild);
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => { foreach (var agent in Agents) agent.TickClocks(); };
        _tick.Start();
    }

    /// <summary>
    /// Vero mentre il pannello e' aperto (lo imposta <see cref="NotchWindow"/>): riflessi e pulsazioni del pannello
    /// girano solo allora, cosi' a pannello chiuso non costano nulla.
    /// </summary>
    public bool IsPanelOpen { get => _isPanelOpen; set => Set(ref _isPanelOpen, value); }

    // Misure della modalita' compatta (spec 6.1 e 6.5).
    public bool Compact { get => _compact; private set { if (Set(ref _compact, value)) RaiseMetrics(); } }
    public double TabWidth => Compact ? 34 : 42;
    public CornerRadius TabCornerRadius => Compact ? new CornerRadius(17, 0, 0, 17) : new CornerRadius(21, 0, 0, 21);
    public double TabRowHeight => Compact ? 36 : 44;
    public double TabRingSize => Compact ? 28 : 34;
    public double TabIconSize => Compact ? 11 : 14;
    public double HeroFontSize => Compact ? 30 : 38;
    public double AvatarSize => Compact ? 24 : 30;
    public Thickness CardPadding => Compact ? new Thickness(12) : new Thickness(16);
    public Thickness TilePadding => Compact ? new Thickness(8) : new Thickness(10);

    // Pillola di sintesi dell'intestazione (spec 6.2).
    public string? SummaryText => _summary?.Text;
    public Brush? SummaryDot => _summary is { } pill ? PhaseVisuals.ToneBrush(pill.Tone) : null;
    public Brush? SummaryForeground => _summary is { } pill ? PhaseVisuals.ToneText(pill.Tone) : null;
    public Brush? SummaryFill => _summary is { } pill ? PhaseVisuals.ToneFill(pill.Tone) : null;
    public Visibility SummaryVisibility => _summary is null ? Visibility.Collapsed : Visibility.Visible;

    private void RaiseMetrics()
    {
        foreach (var name in new[] { nameof(TabWidth), nameof(TabCornerRadius), nameof(TabRowHeight), nameof(TabRingSize), nameof(TabIconSize),
                     nameof(HeroFontSize), nameof(AvatarSize), nameof(CardPadding), nameof(TilePadding) })
            Raise(name);
    }

    private void Rebuild()
    {
        var enabled = _services.EnabledAgents().ToList();
        Compact = _services.Settings.Current.Compact;
        if (Agents.Select(a => a.Agent).SequenceEqual(enabled)) { Refresh(); return; }
        Agents.Clear();
        foreach (var agent in enabled)
            Agents.Add(new AgentCardViewModel(agent, _services, IconFor(agent)));
        RefreshSummary();
    }

    private void Refresh()
    {
        foreach (var agent in Agents) agent.Refresh();
        RefreshSummary();
    }

    private void RefreshSummary()
    {
        var enabled = _services.EnabledAgents().ToHashSet();
        var pill = NotchPresentation.Summary(_services.Sessions.Sessions.Where(s => enabled.Contains(s.Agent)));
        if (pill == _summary) return;
        _summary = pill;
        Raise(nameof(SummaryText)); Raise(nameof(SummaryDot)); Raise(nameof(SummaryForeground)); Raise(nameof(SummaryFill)); Raise(nameof(SummaryVisibility));
    }

    private static Geometry IconFor(AgentKind agent) =>
        (Geometry)Application.Current.FindResource(agent == AgentKind.Claude ? "ClaudeIcon" : "CodexIcon");
}
