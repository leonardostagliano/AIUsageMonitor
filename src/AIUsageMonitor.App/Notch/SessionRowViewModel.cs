using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Notch;

public sealed class SessionRowViewModel : ObservableObject
{
    /// <summary>
    /// Which session rows are expanded, in memory only and keyed by session id: a row is rebuilt whenever the
    /// session list shrinks and grows again, so the flag cannot live on the instance alone. Only the expanded ids
    /// are kept (collapsing removes the entry), so the set stays as small as what the user has open.
    /// </summary>
    private static readonly HashSet<string> ExpandedSessionIds = new(StringComparer.Ordinal);

    private SessionState _session;
    private string _elapsedText = "";

    public SessionRowViewModel(SessionState session, DateTimeOffset now)
    {
        _session = session;
        ToggleCommand = new RelayCommand(Toggle);
        SyncSubagents(now);
        Tick(now);
    }

    public string SessionId => _session.SessionId;
    public string Name => _session.DisplayName;
    public string PhaseLabel => _session.PhaseLabel;
    public Brush DotBrush => PhaseVisuals.Brush(_session.Phase);
    public bool IsPulsing => _session.Phase == SessionPhase.Working;
    public string Tooltip => string.Join("\n", new[] { _session.Cwd, _session.Message }.Where(s => !string.IsNullOrEmpty(s)));
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    /// <summary>Compact total of the session itself, empty until something has been counted.</summary>
    public string TokensText => _session.Tokens is { Total: > 0 } tokens ? TokenFormatter.Compact(tokens.Total) : "";

    /// <summary>Breakdown for the tooltip of the token column; null (no tooltip) when there is no total yet.</summary>
    public string? TokensTooltip => _session.Tokens is { Total: > 0 } tokens ? TokenFormatter.Breakdown(tokens) : null;

    /// <summary>Subagents of this session, running ones first, then the most recently started.</summary>
    public ObservableCollection<SubagentRowViewModel> Subagents { get; } = new();

    public bool HasSubagents => _session.Subagents is { Count: > 0 };

    /// <summary>"3 agenti · 4,1M tok" — the agents known for this session and the sum of their totals.</summary>
    public string SubagentSummary
    {
        get
        {
            var count = _session.Subagents?.Count ?? 0;
            if (count == 0) return "";
            var label = count == 1 ? "1 agente" : $"{count} agenti";
            return $"{label} · {TokenFormatter.Compact(_session.SubagentTokens.Total)} tok";
        }
    }

    public ICommand ToggleCommand { get; }

    public bool IsExpanded => ExpandedSessionIds.Contains(SessionId);

    public string ChevronGlyph => IsExpanded ? "▾" : "▸";

    public Visibility SummaryVisibility => HasSubagents ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SubagentListVisibility => HasSubagents && IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    public void Update(SessionState session, DateTimeOffset now)
    {
        _session = session;
        Raise(nameof(Name)); Raise(nameof(PhaseLabel)); Raise(nameof(DotBrush)); Raise(nameof(IsPulsing)); Raise(nameof(Tooltip));
        Raise(nameof(TokensText)); Raise(nameof(TokensTooltip));
        RaiseSubagentState();
        SyncSubagents(now);
        Tick(now);
    }

    public void Tick(DateTimeOffset now)
    {
        ElapsedText = CountdownFormatter.Since(_session.LastEventAt, now);
        foreach (var subagent in Subagents) subagent.Tick(now);
    }

    private void Toggle()
    {
        if (!ExpandedSessionIds.Remove(SessionId)) ExpandedSessionIds.Add(SessionId);
        Raise(nameof(IsExpanded)); Raise(nameof(ChevronGlyph)); Raise(nameof(SubagentListVisibility));
    }

    private void RaiseSubagentState()
    {
        Raise(nameof(HasSubagents)); Raise(nameof(SubagentSummary));
        Raise(nameof(SummaryVisibility)); Raise(nameof(SubagentListVisibility));
    }

    /// <summary>
    /// Reconciles the rows by agent id (same shape as AgentCardViewModel.SyncSessions): an agent that is still
    /// there keeps its row — and with it its elapsed clock — instead of being rebuilt on every pump refresh.
    /// </summary>
    private void SyncSubagents(DateTimeOffset now)
    {
        var ordered = (_session.Subagents ?? [])
            .OrderBy(s => s.Phase == SubagentPhase.Running ? 0 : 1)
            .ThenByDescending(s => s.StartedAt)
            .ToList();

        var byId = new Dictionary<string, SubagentRowViewModel>(StringComparer.Ordinal);
        foreach (var row in Subagents) byId.TryAdd(row.AgentId, row);

        for (var i = 0; i < ordered.Count; i++)
        {
            if (byId.TryGetValue(ordered[i].AgentId, out var existing))
            {
                existing.Update(ordered[i], now);
                var currentIndex = Subagents.IndexOf(existing);
                if (currentIndex != i) Subagents.Move(currentIndex, i);
            }
            else
            {
                Subagents.Insert(i, new SubagentRowViewModel(ordered[i], now));
            }
        }
        while (Subagents.Count > ordered.Count) Subagents.RemoveAt(Subagents.Count - 1);
    }
}
