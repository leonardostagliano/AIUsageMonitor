using System.Globalization;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Pricing;

namespace AIUsageMonitor.Core.Presentation;

/// <summary>Colour family of a usage bar or ring.</summary>
public enum UsageTone { Normal, Warning, Critical, Stale }

/// <summary>Colour family of a session or subagent: avatar ring, dot and subtitle.</summary>
public enum PhaseTone { Working, NeedsInput, Error, Idle }

/// <summary>The pill in the notch header: the most urgent state across the sessions, and its text.</summary>
public sealed record SummaryPill(PhaseTone Tone, string Text);

/// <summary>
/// A cost split for display: <see cref="Lead"/> ("≈", "≥" or ""), the amount to format with
/// <see cref="CostFormatter.Amount"/>, or a <see cref="Fallback"/> text when there is no amount to show.
/// </summary>
public sealed record CostDisplay(string Lead, decimal? Amount, string? Fallback);

/// <summary>Presentation rules of the notch, kept out of the view models so they can be tested without WPF.</summary>
public static class NotchPresentation
{
    /// <summary>The tone of a window: its severity while the snapshot is fresh, "stale" otherwise.</summary>
    public static UsageTone ToneOf(UsageWindow window, UsageStatus status) =>
        status != UsageStatus.Ok
            ? UsageTone.Stale
            : window.Severity switch
            {
                Severity.Critical => UsageTone.Critical,
                Severity.Warning => UsageTone.Warning,
                _ => UsageTone.Normal
            };

    public static PhaseTone ToneOf(SessionPhase phase) => phase switch
    {
        SessionPhase.Working => PhaseTone.Working,
        SessionPhase.NeedsInput => PhaseTone.NeedsInput,
        SessionPhase.Error => PhaseTone.Error,
        _ => PhaseTone.Idle
    };

    public static PhaseTone ToneOf(SubagentPhase phase) => phase == SubagentPhase.Running ? PhaseTone.Working : PhaseTone.Idle;

    /// <summary>The first window of the snapshot is shown large; the others become bars, in their order.</summary>
    public static (UsageWindow? Hero, IReadOnlyList<UsageWindow> Others) SplitWindows(IReadOnlyList<UsageWindow> windows) =>
        windows.Count == 0 ? (null, []) : (windows[0], windows.Skip(1).ToList());

    /// <summary>"reset tra 3h 5m", "reset adesso" once the reset has passed, null without a reset time.</summary>
    public static string? ResetCaption(UsageWindow window, DateTimeOffset now)
    {
        if (window.ResetsAt is not { } reset) return null;
        return reset <= now ? "reset adesso" : $"reset tra {CountdownFormatter.Until(reset, now)}";
    }

    /// <summary>
    /// A percent as the whole number every surface shows (hero number, bar rows, tray tooltip): the same
    /// <c>"0"</c> format as <c>{Percent:0}</c>, so a half rounds away from zero everywhere (12.5 reads "13", not the
    /// "12" of <see cref="Math.Round(double)"/>).
    /// </summary>
    public static string WholePercent(double percent) => percent.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>
    /// The status text of a card: a wait message before the first snapshot, the snapshot's own message while it is
    /// not fresh, nothing while it is.
    /// </summary>
    public static string? StatusMessage(UsageSnapshot? snapshot) =>
        snapshot is null ? "In attesa del primo aggiornamento"
        : snapshot.Status == UsageStatus.Ok ? null
        : snapshot.StatusMessage;

    /// <summary>
    /// The caption under the "—" of a card without quota windows: the status text when there is one, otherwise a
    /// fresh snapshot that simply has no windows.
    /// </summary>
    public static string NoDataCaption(string? statusMessage) => statusMessage ?? "Nessuna finestra di quota";

    /// <summary>"finestra 5h · reset tra 3h 5m", or just "finestra 5h" without a reset time.</summary>
    public static string HeroCaption(UsageWindow window, DateTimeOffset now) =>
        ResetCaption(window, now) is { } reset ? $"finestra {window.Label} · {reset}" : $"finestra {window.Label}";

    /// <summary>The avatar letter: the first letter or digit of the name, upper-cased; "•" for anything else.</summary>
    public static string Initial(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return "•";
        var first = StringInfo.GetNextTextElement(displayName.TrimStart());
        return first.Length == 1 && char.IsLetterOrDigit(first[0]) ? first.ToUpper(CultureInfo.GetCultureInfo("it-IT")) : "•";
    }

    /// <summary>"al lavoro · 2 agenti · 1m": the phase label of the session and the time since its last event.</summary>
    public static string SessionSubtitle(SessionState session, DateTimeOffset now) =>
        $"{session.PhaseLabel} · {CountdownFormatter.Since(session.LastEventAt, now)}";

    /// <summary>Waiting for input beats errors, errors beat work; nothing when no session is busy or in trouble.</summary>
    public static SummaryPill? Summary(IEnumerable<SessionState> sessions)
    {
        var list = sessions.ToList();
        var needsInput = list.Count(s => s.Phase == SessionPhase.NeedsInput);
        if (needsInput > 0) return new SummaryPill(PhaseTone.NeedsInput, needsInput == 1 ? "1 attende input" : $"{needsInput} attendono input");
        var errors = list.Count(s => s.Phase == SessionPhase.Error);
        if (errors > 0) return new SummaryPill(PhaseTone.Error, $"{errors} in errore");
        var working = list.Count(s => s.Phase == SessionPhase.Working);
        return working > 0 ? new SummaryPill(PhaseTone.Working, $"{working} al lavoro") : null;
    }

    /// <summary>The same rules as <see cref="CostFormatter.Short"/>, split so the amount can roll on its own.</summary>
    public static CostDisplay? CostParts(CostResult cost)
    {
        if (!cost.HasUsage) return null;
        if (!cost.AnyPriced || (cost.AnyUnpriced && cost.Eur < 0.01m)) return new CostDisplay("", null, "costo n/d");
        if (cost.Eur < 0.01m) return new CostDisplay("", cost.Eur, null);
        return new CostDisplay(cost.AnyUnpriced ? "≥" : "≈", cost.Eur, null);
    }
}
