using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;
using AIUsageMonitor.Core.Pricing;

namespace AIUsageMonitor.Tests;

public class NotchPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 24, 10, 0, 0, TimeSpan.Zero);

    private static UsageWindow Window(string label, double percent, Severity severity = Severity.Normal, DateTimeOffset? reset = null) =>
        new(label, percent, reset, severity);

    private static SessionState Session(string id, SessionPhase phase, string name = "demo", DateTimeOffset? last = null) =>
        new(AgentKind.Claude, id, name, null, phase, phase == SessionPhase.Idle ? "Turno completato" : null, last ?? Now, Now);

    [Theory]
    [InlineData(Severity.Normal, UsageStatus.Ok, UsageTone.Normal)]
    [InlineData(Severity.Warning, UsageStatus.Ok, UsageTone.Warning)]
    [InlineData(Severity.Critical, UsageStatus.Ok, UsageTone.Critical)]
    [InlineData(Severity.Critical, UsageStatus.Stale, UsageTone.Stale)]
    [InlineData(Severity.Normal, UsageStatus.TokenExpired, UsageTone.Stale)]
    [InlineData(Severity.Normal, UsageStatus.Error, UsageTone.Stale)]
    public void A_window_takes_the_tone_of_its_severity_only_while_the_data_is_fresh(Severity severity, UsageStatus status, UsageTone tone) =>
        Assert.Equal(tone, NotchPresentation.ToneOf(Window("5h", 50, severity), status));

    [Fact]
    public void The_first_window_is_the_hero_and_the_rest_keep_their_order()
    {
        var windows = new[] { Window("5h", 13), Window("7g", 30), Window("7g Fable", 22) };

        var (hero, others) = NotchPresentation.SplitWindows(windows);

        Assert.Equal("5h", hero!.Label);
        Assert.Equal(["7g", "7g Fable"], others.Select(w => w.Label).ToList());
        Assert.Null(NotchPresentation.SplitWindows([]).Hero);
        Assert.Empty(NotchPresentation.SplitWindows([Window("7g", 5)]).Others);
    }

    [Fact]
    public void Captions_name_the_window_and_the_time_to_its_reset()
    {
        var withReset = Window("5h", 13, reset: Now.AddHours(3).AddMinutes(5));

        Assert.Equal("reset tra 3h 5m", NotchPresentation.ResetCaption(withReset, Now));
        Assert.Equal("finestra 5h · reset tra 3h 5m", NotchPresentation.HeroCaption(withReset, Now));
        Assert.Equal("reset adesso", NotchPresentation.ResetCaption(Window("7g", 1, reset: Now.AddMinutes(-1)), Now));
        Assert.Null(NotchPresentation.ResetCaption(Window("7g", 1), Now));
        Assert.Equal("finestra 7g", NotchPresentation.HeroCaption(Window("7g", 1), Now));
    }

    [Theory]
    [InlineData("AIUsageMonitor", "A")]
    [InlineData("hooks", "H")]
    [InlineData("  àncora", "À")]
    [InlineData("3d-viewer", "3")]
    [InlineData("_tmp", "•")]
    [InlineData("🚀 launch", "•")]
    [InlineData("", "•")]
    [InlineData(null, "•")]
    public void The_avatar_shows_the_first_letter_or_digit(string? name, string initial) =>
        Assert.Equal(initial, NotchPresentation.Initial(name));

    [Theory]
    [InlineData(SessionPhase.Working, PhaseTone.Working)]
    [InlineData(SessionPhase.NeedsInput, PhaseTone.NeedsInput)]
    [InlineData(SessionPhase.Error, PhaseTone.Error)]
    [InlineData(SessionPhase.Idle, PhaseTone.Idle)]
    public void Every_session_phase_has_a_tone(SessionPhase phase, PhaseTone tone) =>
        Assert.Equal(tone, NotchPresentation.ToneOf(phase));

    [Fact]
    public void Subagents_are_working_while_running_and_idle_when_done()
    {
        Assert.Equal(PhaseTone.Working, NotchPresentation.ToneOf(SubagentPhase.Running));
        Assert.Equal(PhaseTone.Idle, NotchPresentation.ToneOf(SubagentPhase.Done));
    }

    [Fact]
    public void The_subtitle_is_the_phase_label_and_the_time_since_the_last_event()
    {
        Assert.Equal("al lavoro · 1m", NotchPresentation.SessionSubtitle(Session("a", SessionPhase.Working, last: Now.AddMinutes(-1)), Now));
        Assert.Equal("attende input · 4m", NotchPresentation.SessionSubtitle(Session("b", SessionPhase.NeedsInput, last: Now.AddMinutes(-4)), Now));
        Assert.Equal("finito · 12m", NotchPresentation.SessionSubtitle(Session("c", SessionPhase.Idle, last: Now.AddMinutes(-12)), Now));

        var withAgents = Session("d", SessionPhase.Working, last: Now.AddMinutes(-1)) with
        {
            Subagents = [new("x", "workflow", SubagentPhase.Running, Now, null, null, TokenUsage.Zero), new("y", "workflow", SubagentPhase.Running, Now, null, null, TokenUsage.Zero)]
        };
        Assert.Equal("al lavoro · 2 agenti · 1m", NotchPresentation.SessionSubtitle(withAgents, Now));
    }

    [Fact]
    public void The_summary_pill_shows_the_most_urgent_state()
    {
        Assert.Null(NotchPresentation.Summary([]));
        Assert.Null(NotchPresentation.Summary([Session("a", SessionPhase.Idle)]));
        Assert.Equal(new SummaryPill(PhaseTone.Working, "2 al lavoro"),
            NotchPresentation.Summary([Session("a", SessionPhase.Working), Session("b", SessionPhase.Working), Session("c", SessionPhase.Idle)]));
        Assert.Equal(new SummaryPill(PhaseTone.Error, "1 in errore"),
            NotchPresentation.Summary([Session("a", SessionPhase.Working), Session("b", SessionPhase.Error)]));
        Assert.Equal(new SummaryPill(PhaseTone.NeedsInput, "1 attende input"),
            NotchPresentation.Summary([Session("a", SessionPhase.Error), Session("b", SessionPhase.NeedsInput)]));
        Assert.Equal(new SummaryPill(PhaseTone.NeedsInput, "2 attendono input"),
            NotchPresentation.Summary([Session("a", SessionPhase.NeedsInput), Session("b", SessionPhase.NeedsInput)]));
        Assert.Equal(new SummaryPill(PhaseTone.Error, "3 in errore"),
            NotchPresentation.Summary([Session("a", SessionPhase.Error), Session("b", SessionPhase.Error), Session("c", SessionPhase.Error)]));
    }

    [Fact]
    public void Cost_parts_match_the_short_cost_text()
    {
        var priced = new CostResult(3.21m, [new ModelCost("m", 3.21m, true)]);
        var partial = new CostResult(3.21m, [new ModelCost("m", 3.21m, true), new ModelCost("codex-auto-review", 0m, false)]);
        var none = new CostResult(0m, [new ModelCost("codex-auto-review", 0m, false)]);
        var tiny = new CostResult(0.004m, [new ModelCost("m", 0.004m, true)]);
        var tinyPartial = new CostResult(0.004m, [new ModelCost("m", 0.004m, true), new ModelCost("x", 0m, false)]);

        Assert.Null(NotchPresentation.CostParts(CostResult.None));
        Assert.Equal(new CostDisplay("≈", 3.21m, null), NotchPresentation.CostParts(priced));
        Assert.Equal(new CostDisplay("≥", 3.21m, null), NotchPresentation.CostParts(partial));
        Assert.Equal(new CostDisplay("", null, "costo n/d"), NotchPresentation.CostParts(none));
        Assert.Equal(new CostDisplay("", 0.004m, null), NotchPresentation.CostParts(tiny));
        Assert.Equal(new CostDisplay("", null, "costo n/d"), NotchPresentation.CostParts(tinyPartial));

        foreach (var cost in new[] { priced, partial, none, tiny, tinyPartial })
        {
            var parts = NotchPresentation.CostParts(cost)!;
            var composed = parts.Fallback ?? (parts.Lead.Length > 0 ? parts.Lead + " " : "") + CostFormatter.Amount(parts.Amount!.Value);
            Assert.Equal(CostFormatter.Short(cost), composed);
        }
    }
}
