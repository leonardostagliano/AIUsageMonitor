namespace AIUsageMonitor.Core.Models;

public enum SessionPhase { Working, NeedsInput, Idle, Error }

public sealed record SessionState(
    AgentKind Agent,
    string SessionId,
    string DisplayName,
    string? Cwd,
    SessionPhase Phase,
    string? Message,
    DateTimeOffset LastEventAt,
    DateTimeOffset StartedAt)
{
    /// <summary>Italian label shown in the UI. Idle is "pronto" before the first completed turn, "finito" after.</summary>
    public string PhaseLabel => Phase switch
    {
        SessionPhase.Working => "al lavoro",
        SessionPhase.NeedsInput => "attende input",
        SessionPhase.Idle => Message is null ? "pronto" : "finito",
        SessionPhase.Error => "errore",
        _ => Phase.ToString()
    };
}

/// <summary>One line of ~/.aiusagemonitor/events.jsonl as written by hook.cjs.</summary>
public sealed record HookEvent(
    DateTimeOffset Ts,
    AgentKind Agent,
    string Event,
    string SessionId,
    string? Cwd,
    string? NotificationType,
    string? Message,
    string? Source);
