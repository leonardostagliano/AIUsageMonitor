using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Notch;

/// <summary>
/// One background subagent under a session row (Agent tool, workflow agent, or — for Codex — a child thread).
/// Deliberately icon-less: the row is already indented under its session, so a dot, a name, a phase, its own token
/// total and how long it has been running are all the panel can afford at 320 px.
/// </summary>
public sealed class SubagentRowViewModel : ObservableObject
{
    private SubagentState _state;
    private string _elapsedText = "";

    public SubagentRowViewModel(SubagentState state, DateTimeOffset now)
    {
        _state = state;
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

    /// <summary>Compact total, empty while nothing has been counted yet (a transcript that has not been read).</summary>
    public string TokensText => _state.Tokens.Total > 0 ? TokenFormatter.Compact(_state.Tokens.Total) : "";

    /// <summary>Null — not "" — when there is no total: WPF shows no tooltip at all for null.</summary>
    public string? TokensTooltip => _state.Tokens.Total > 0 ? TokenFormatter.Breakdown(_state.Tokens) : null;

    /// <summary>Time running for an agent still at work, total duration for one that has finished.</summary>
    public string ElapsedText { get => _elapsedText; private set => Set(ref _elapsedText, value); }

    public void Update(SubagentState state, DateTimeOffset now)
    {
        _state = state;
        Raise(nameof(Name)); Raise(nameof(PhaseLabel)); Raise(nameof(DotBrush)); Raise(nameof(IsPulsing));
        Raise(nameof(TokensText)); Raise(nameof(TokensTooltip));
        Tick(now);
    }

    public void Tick(DateTimeOffset now) =>
        ElapsedText = CountdownFormatter.Since(_state.StartedAt, _state.EndedAt ?? now);
}
