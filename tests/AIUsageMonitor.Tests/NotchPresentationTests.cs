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
        Assert.Equal("errore · 3m", NotchPresentation.SessionSubtitle(Session("e", SessionPhase.Error, last: Now.AddMinutes(-3)), Now));
        // Idle before the first completed turn: no message yet, so "pronto" instead of "finito".
        Assert.Equal("pronto · 2m", NotchPresentation.SessionSubtitle(Session("f", SessionPhase.Idle, last: Now.AddMinutes(-2)) with { Message = null }, Now));

        var withAgents = Session("d", SessionPhase.Working, last: Now.AddMinutes(-1)) with
        {
            Subagents = [new("x", "workflow", SubagentPhase.Running, Now, null, null, TokenUsage.Zero), new("y", "workflow", SubagentPhase.Running, Now, null, null, TokenUsage.Zero)]
        };
        Assert.Equal("al lavoro · 2 agenti · 1m", NotchPresentation.SessionSubtitle(withAgents, Now));
        Assert.Equal("cloud · al lavoro · 1m", NotchPresentation.SessionSubtitle(
            Session("g", SessionPhase.Working, last: Now.AddMinutes(-1)) with { Origin = SessionOrigin.Cloud }, Now));
        Assert.Equal("routine · finito · 12m", NotchPresentation.SessionSubtitle(
            Session("h", SessionPhase.Idle, last: Now.AddMinutes(-12)) with { Origin = SessionOrigin.Routine }, Now));
        Assert.Equal("app · attende input · 4m", NotchPresentation.SessionSubtitle(
            Session("i", SessionPhase.NeedsInput, last: Now.AddMinutes(-4)) with { Origin = SessionOrigin.App }, Now));
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
        Assert.Equal(new SummaryPill(PhaseTone.Working, "1 al lavoro"),
            NotchPresentation.Summary([Session("a", SessionPhase.Working)]));
        Assert.Equal(new SummaryPill(PhaseTone.NeedsInput, "1 attende input"),
            NotchPresentation.Summary([Session("a", SessionPhase.NeedsInput), Session("b", SessionPhase.Working)]));
        Assert.Equal(new SummaryPill(PhaseTone.NeedsInput, "1 attende input"),
            NotchPresentation.Summary([Session("a", SessionPhase.Idle), Session("b", SessionPhase.Working), Session("c", SessionPhase.Error), Session("d", SessionPhase.NeedsInput)]));
    }

    [Theory]
    [InlineData(12.5, "13")]
    [InlineData(0.5, "1")]
    [InlineData(13.5, "14")]
    [InlineData(12.4, "12")]
    [InlineData(99.6, "100")]
    [InlineData(0, "0")]
    public void The_whole_percent_rounds_halves_like_every_other_percent_text(double percent, string expected)
    {
        Assert.Equal(expected, NotchPresentation.WholePercent(percent));
        // The bar rows and the tray tooltip format with {Percent:0}: the hero number must read the same.
        Assert.Equal($"{percent:0}", NotchPresentation.WholePercent(percent));
    }

    [Fact]
    public void A_card_without_windows_explains_why()
    {
        var fresh = UsageSnapshot.Empty(AgentKind.Claude, UsageStatus.Ok, null, Now);
        var stale = UsageSnapshot.Empty(AgentKind.Claude, UsageStatus.Stale, "Ultimo aggiornamento 01:23", Now);

        Assert.Equal("In attesa del primo aggiornamento", NotchPresentation.StatusMessage(null));
        Assert.Null(NotchPresentation.StatusMessage(fresh));
        Assert.Equal("Ultimo aggiornamento 01:23", NotchPresentation.StatusMessage(stale));

        Assert.Equal("In attesa del primo aggiornamento", NotchPresentation.NoDataCaption(NotchPresentation.StatusMessage(null)));
        // A fresh snapshot can come back without windows (null five_hour/seven_day): it is not "waiting".
        Assert.Equal("Nessuna finestra di quota", NotchPresentation.NoDataCaption(NotchPresentation.StatusMessage(fresh)));
        Assert.Equal("Ultimo aggiornamento 01:23", NotchPresentation.NoDataCaption(NotchPresentation.StatusMessage(stale)));
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
