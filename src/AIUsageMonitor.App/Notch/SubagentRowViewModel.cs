using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Pricing;

namespace AIUsageMonitor.App.Notch;

/// <summary>
/// One background subagent under a session row (Agent tool, workflow agent, or — for Codex — a child thread).
/// Indented under its session, with a name/status row, the actual model and separate input/output counts, followed
/// by the API-equivalent cost when costs are shown.
/// </summary>
public sealed class SubagentRowViewModel : ObservableObject
{
    private SubagentState _state;
    private PricingSnapshot? _pricing;
    private string _elapsedText = "";

    public SubagentRowViewModel(SubagentState state, DateTimeOffset now, PricingSnapshot? pricing)
    {
        _state = state;
        _pricing = pricing;
        Tick(now);
    }

    public string AgentId => _state.AgentId;

    /// <summary>The agent type when the hook carried one ("workflow-subagent", "codex-thread", …), else a short id.</summary>
    public string Name => string.IsNullOrWhiteSpace(_state.AgentType)
        ? (_state.AgentId.Length <= 8 ? _state.AgentId : _state.AgentId[..8])
        : _state.AgentType!;

    public string PhaseLabel => _state.Phase == SubagentPhase.Running ? "in corso" : "finito";

    public Brush DotBrush => PhaseVisuals.Brush(_state.Phase == SubagentPhase.Running ? SessionPhase.Working : SessionPhase.Idle);

    public bool IsPulsing => _state.Phase == SubagentPhase.Running;

    /// <summary>Cost of this agent's tokens; null while costs are hidden.</summary>
    private CostResult? Cost => _pricing?.Cost(_state.Ledger);

    /// <summary>Separate input and output (plus the cost), or an explicit pending state before the first usage report.</summary>
    public string TokensText
    {
        get
        {
            if (_state.Tokens.Total <= 0) return "Token in attesa";
            var text = TokenFormatter.InputOutput(_state.Tokens);
            return Cost is { } cost && CostFormatter.Short(cost) is { } costText ? $"{text} · {costText}" : text;
        }
    }

    public string ModelText => string.IsNullOrWhiteSpace(_state.Model) ? "Modello in attesa" : _state.Model;

    /// <summary>Null — not "" — when there is no total: WPF shows no tooltip at all for null.</summary>
    public string? TokensTooltip
    {
        get
        {
            if (_state.Tokens.Total <= 0) return null;
            var breakdown = TokenFormatter.Breakdown(_state.Tokens);
            return _pricing is not null && Cost is { } cost && CostTooltips.Row(cost, null, _pricing) is { } block
                ? $"{breakdown}\n\n{block}"
                : breakdown;
        }
    }

    /// <summary>Time running for an agent still at work, total duration for one that has finished.</summary>
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    public void Update(SubagentState state, DateTimeOffset now, PricingSnapshot? pricing)
    {
        _state = state;
        _pricing = pricing;
        Raise(nameof(Name)); Raise(nameof(PhaseLabel)); Raise(nameof(DotBrush)); Raise(nameof(IsPulsing));
        Raise(nameof(TokensText)); Raise(nameof(TokensTooltip)); Raise(nameof(ModelText));
        Tick(now);
    }

    public void Tick(DateTimeOffset now) =>
        ElapsedText = CountdownFormatter.Since(_state.StartedAt, _state.EndedAt ?? now);
}
