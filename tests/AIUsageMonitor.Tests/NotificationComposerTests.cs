using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Settings;

namespace AIUsageMonitor.Tests;

/// <summary>
/// Session changes and app notices into board intents: every card type of spec §5.1 with its label, message, tone,
/// timer and sound (spec §8), the per-event and per-agent filters, Silent, the origin prefix and the retire rules of §5.4.
/// </summary>
public class NotificationComposerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly NotificationIntent RetireS1 = new RetireIntent("session:claude:s1");

    private static SessionState Session(SessionPhase phase, string? message = null, AttentionDetail? attention = null,
        AgentKind agent = AgentKind.Claude, SessionOrigin origin = SessionOrigin.Terminal, DateTimeOffset? turnStartedAt = null,
        int lastEventSeconds = 0) =>
        new(agent, "s1", "demo", @"C:\p\demo", phase, message, T0.AddSeconds(lastEventSeconds), T0, Origin: origin,
            Attention: attention, TurnStartedAt: turnStartedAt);

    private static SessionChange Change(SessionState session, SessionPhase? previous,
        SessionChangeKind kind = SessionChangeKind.Updated, bool silent = false) =>
        new(kind, session, previous, Silent: silent);

    private static NotificationIntent? Compose(SessionChange change, AppSettings? settings = null) =>
        NotificationComposer.Compose(change, settings ?? new AppSettings());

    private static NotificationCard Card(SessionChange change, AppSettings? settings = null) =>
        Assert.IsType<ShowIntent>(Compose(change, settings)).Card;

    private static NotificationCard Waiting(AttentionDetail? attention, string? message = null, AppSettings? settings = null) =>
        Card(Change(Session(SessionPhase.NeedsInput, message, attention), SessionPhase.Working), settings);

    private static SessionChange EndOfTurn(string? message = "Tutti i test passano", DateTimeOffset? turnStartedAt = null,
        int lastEventSeconds = 252, bool silent = false) =>
        Change(Session(SessionPhase.Idle, message, turnStartedAt: turnStartedAt, lastEventSeconds: lastEventSeconds),
            SessionPhase.Working, silent: silent);

    [Fact]
    public void A_session_has_one_key_per_agent_and_id()
    {
        Assert.Equal("session:claude:abc", NotificationComposer.SessionKey(AgentKind.Claude, "abc"));
        Assert.Equal("session:codex:abc", NotificationComposer.SessionKey(AgentKind.Codex, "abc"));
    }

    [Fact]
    public void Finished_cards_close_after_eight_seconds_and_info_notices_after_six()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), NotificationComposer.FinishedAutoClose);
        Assert.Equal(TimeSpan.FromSeconds(6), NotificationComposer.InfoAutoClose);
    }

    [Fact]
    public void A_permission_card_says_which_tool_and_what_it_runs()
    {
        var session = Session(SessionPhase.NeedsInput, "Claude needs your permission",
            new AttentionDetail(AttentionKind.Permission, "Bash", "npm test"));

        var card = Card(Change(session, SessionPhase.Working));

        Assert.Equal("session:claude:s1", card.Key);
        Assert.Equal(NotificationKind.Permission, card.Kind);
        Assert.Equal(NotificationTone.Warning, card.Tone);
        Assert.Equal(AgentKind.Claude, card.Agent);
        Assert.Equal("demo", card.Title);
        Assert.Equal("Permesso · Bash", card.Label);
        Assert.Equal("npm test", card.Message);
        Assert.Equal("NeedsInput|Permesso · Bash|npm test", card.StateKey);
        Assert.Null(card.AutoClose);
        Assert.Equal(NotificationSoundKind.Attention, card.Sound);
        Assert.Equal(NotificationAction.FocusSession, card.Action);
        Assert.Same(session, card.Session);
    }

    [Theory]
    [InlineData(null, false, "Permesso")]
    [InlineData("Bash", false, "Permesso · Bash")]
    [InlineData(null, true, "Permesso · agente in background")]
    [InlineData("Bash", true, "Permesso · Bash · agente in background")]
    [InlineData(" ", false, "Permesso")]
    public void A_permission_label_names_the_tool_and_a_background_agent(string? tool, bool background, string label) =>
        Assert.Equal(label, Waiting(new AttentionDetail(AttentionKind.Permission, tool, "npm test", background)).Label);

    [Fact]
    public void A_permission_never_shows_the_generic_text_of_Claude()
    {
        var card = Waiting(new AttentionDetail(AttentionKind.Permission, "Bash"), "Claude needs your permission");

        Assert.Equal("Permesso richiesto", card.Message);
        Assert.Equal("Permesso richiesto", Waiting(new AttentionDetail(AttentionKind.Permission, "Bash", "  ")).Message);
    }

    [Fact]
    public void A_wait_without_a_detail_is_a_permission()
    {
        var card = Waiting(null, "Claude needs your permission");

        Assert.Equal(NotificationKind.Permission, card.Kind);
        Assert.Equal("Permesso", card.Label);
        Assert.Equal("Permesso richiesto", card.Message);
        Assert.Equal("NeedsInput|Permesso|Permesso richiesto", card.StateKey);
    }

    [Fact]
    public void A_plan_asks_for_approval()
    {
        var card = Waiting(new AttentionDetail(AttentionKind.Plan, Summary: "Sposta il parser in Core"));

        Assert.Equal(NotificationKind.Plan, card.Kind);
        Assert.Equal(NotificationTone.Warning, card.Tone);
        Assert.Equal("Piano da approvare", card.Label);
        Assert.Equal("Sposta il parser in Core", card.Message);
        Assert.Equal("Claude ha preparato un piano",
            Waiting(new AttentionDetail(AttentionKind.Plan), "Claude needs your permission").Message);
    }

    [Fact]
    public void A_question_shows_the_question_then_the_message()
    {
        var card = Waiting(new AttentionDetail(AttentionKind.Question, Summary: "Quale database uso? (+1)"));

        Assert.Equal(NotificationKind.Question, card.Kind);
        Assert.Equal(NotificationTone.Question, card.Tone);
        Assert.Equal("Domanda", card.Label);
        Assert.Equal("Quale database uso? (+1)", card.Message);
        Assert.Equal("Scegli un server MCP", Waiting(new AttentionDetail(AttentionKind.Question), "Scegli un server MCP").Message);
        Assert.Equal("Claude ti fa una domanda", Waiting(new AttentionDetail(AttentionKind.Question)).Message);
    }

    [Theory]
    [InlineData(false, "Attende input")]
    [InlineData(true, "Un agente attende input")]
    public void An_input_wait_is_a_question_that_names_a_background_agent(bool background, string label)
    {
        var card = Waiting(new AttentionDetail(AttentionKind.Input, Summary: "Conferma il deploy", Background: background));

        Assert.Equal(NotificationKind.Question, card.Kind);
        Assert.Equal(NotificationTone.Question, card.Tone);
        Assert.Equal(label, card.Label);
        Assert.Equal("Conferma il deploy", card.Message);
    }

    [Fact]
    public void An_input_wait_falls_back_to_the_message_then_to_a_generic_text()
    {
        Assert.Equal("Rivedi la PR", Waiting(new AttentionDetail(AttentionKind.Input), "Rivedi la PR").Message);
        Assert.Equal("Input richiesto", Waiting(new AttentionDetail(AttentionKind.Input)).Message);
    }

    [Fact]
    public void An_error_card_shows_the_error()
    {
        var card = Card(Change(Session(SessionPhase.Error, "API overloaded"), SessionPhase.Working));

        Assert.Equal(NotificationKind.Error, card.Kind);
        Assert.Equal(NotificationTone.Danger, card.Tone);
        Assert.Equal("Errore", card.Label);
        Assert.Equal("API overloaded", card.Message);
        Assert.Equal("Error|Errore|API overloaded", card.StateKey);
        Assert.Null(card.AutoClose);
        Assert.Equal(NotificationSoundKind.Attention, card.Sound);
        Assert.Equal("Errore API", Card(Change(Session(SessionPhase.Error), null, SessionChangeKind.Added)).Message);
    }

    [Fact]
    public void A_finished_turn_shows_how_long_it_took()
    {
        var card = Card(EndOfTurn(turnStartedAt: T0));

        Assert.Equal(NotificationKind.Finished, card.Kind);
        Assert.Equal(NotificationTone.Success, card.Tone);
        Assert.Equal("Finito · 4m 12s", card.Label);
        Assert.Equal("Tutti i test passano", card.Message);
        Assert.Equal("Idle|Finito · 4m 12s|Tutti i test passano", card.StateKey);
        Assert.Equal(TimeSpan.FromSeconds(8), card.AutoClose);
        Assert.Equal(NotificationSoundKind.Done, card.Sound);
        Assert.Equal(NotificationAction.FocusSession, card.Action);
    }

    [Fact]
    public void Finito_has_no_duration_without_a_known_start_or_with_a_start_after_the_end()
    {
        Assert.Equal("Finito", Card(EndOfTurn(turnStartedAt: null)).Label);
        Assert.Equal("Finito", Card(EndOfTurn(turnStartedAt: T0.AddSeconds(300))).Label);
        Assert.Equal("Finito · 0s", Card(EndOfTurn(turnStartedAt: T0, lastEventSeconds: 0)).Label);
    }

    [Fact]
    public void A_finished_turn_without_a_message_says_so()
    {
        Assert.Equal("Turno completato", Card(EndOfTurn(message: null)).Message);
    }

    [Fact]
    public void A_silent_end_of_turn_shows_nothing()
    {
        Assert.Null(Compose(EndOfTurn(turnStartedAt: T0, silent: true)));
    }

    /// <summary>
    /// A Silent change reports what happened while the app was not looking (an agent restored by the replay found over
    /// in its transcript) or is no change of state at all (a token refresh, an agent's name): a wait or an error it
    /// carries began before, and announcing it now would bring back history, with the attention sound.
    /// </summary>
    [Theory]
    [InlineData(SessionPhase.NeedsInput, SessionPhase.NeedsInput)]
    [InlineData(SessionPhase.NeedsInput, SessionPhase.Working)]
    [InlineData(SessionPhase.Error, SessionPhase.Error)]
    [InlineData(SessionPhase.Error, SessionPhase.Working)]
    public void A_silent_change_never_shows_a_wait_or_an_error(SessionPhase phase, SessionPhase previous)
    {
        var session = Session(phase, "You've hit your session limit",
            phase == SessionPhase.NeedsInput ? new AttentionDetail(AttentionKind.Permission, "Bash", "npm test") : null);

        Assert.Null(Compose(Change(session, previous, silent: true)));
    }

    [Theory]
    [InlineData(SessionPhase.NeedsInput)]
    [InlineData(SessionPhase.Error)]
    public void A_silent_change_still_retires_a_card_whose_state_is_over(SessionPhase previous)
    {
        Assert.Equal(RetireS1, Compose(Change(Session(SessionPhase.Idle, "Turno completato"), previous, silent: true)));
        Assert.Equal(RetireS1, Compose(Change(Session(SessionPhase.Working), previous, silent: true)));
        // A wait whose event the settings mute is retired whether the change is silent or not.
        Assert.Equal(RetireS1, Compose(Change(Session(SessionPhase.NeedsInput), SessionPhase.NeedsInput, silent: true),
            new AppSettings { NotifyNeedsInput = false }));
    }

    [Theory]
    [InlineData(SessionPhase.NeedsInput)]
    [InlineData(SessionPhase.Error)]
    public void The_end_of_a_wait_or_of_an_error_retires_the_card(SessionPhase previous)
    {
        Assert.Equal(RetireS1, Compose(Change(Session(SessionPhase.Idle, "Turno completato"), previous)));
    }

    [Fact]
    public void An_idle_session_that_was_not_at_work_leaves_the_board_alone()
    {
        Assert.Null(Compose(Change(Session(SessionPhase.Idle, "Turno completato"), SessionPhase.Idle)));   // a token update
        Assert.Null(Compose(Change(Session(SessionPhase.Idle), null, SessionChangeKind.Added)));
    }

    [Theory]
    [InlineData(SessionPhase.NeedsInput)]
    [InlineData(SessionPhase.Error)]
    [InlineData(SessionPhase.Idle)]
    [InlineData(SessionPhase.Working)]
    public void Back_at_work_retires_the_card(SessionPhase previous)
    {
        Assert.Equal(RetireS1, Compose(Change(Session(SessionPhase.Working), previous)));
    }

    [Fact]
    public void A_session_first_seen_at_work_retires_its_key_too()
    {
        Assert.Equal(RetireS1, Compose(Change(Session(SessionPhase.Working), null, SessionChangeKind.Added)));
    }

    [Fact]
    public void A_removed_session_retires_its_card()
    {
        var removed = Change(Session(SessionPhase.NeedsInput, attention: new AttentionDetail(AttentionKind.Question)),
            SessionPhase.NeedsInput, SessionChangeKind.Removed);

        Assert.Equal(RetireS1, Compose(removed));
    }

    [Fact]
    public void A_muted_agent_retires_its_card()
    {
        var claudeWait = Change(Session(SessionPhase.NeedsInput), SessionPhase.Working);
        var codexError = Change(Session(SessionPhase.Error, agent: AgentKind.Codex), SessionPhase.Working);

        Assert.Equal(RetireS1, Compose(claudeWait, new AppSettings { NotifyClaude = false }));
        Assert.Equal(new RetireIntent("session:codex:s1"), Compose(codexError, new AppSettings { NotifyCodex = false }));
        Assert.IsType<ShowIntent>(Compose(claudeWait, new AppSettings { NotifyCodex = false }));
        Assert.IsType<ShowIntent>(Compose(codexError, new AppSettings { NotifyClaude = false }));
    }

    [Fact]
    public void A_muted_event_retires_its_card_and_leaves_the_others_alone()
    {
        var wait = Change(Session(SessionPhase.NeedsInput), SessionPhase.Working);
        var error = Change(Session(SessionPhase.Error), SessionPhase.Working);
        var finished = EndOfTurn();

        Assert.Equal(RetireS1, Compose(wait, new AppSettings { NotifyNeedsInput = false }));
        Assert.Equal(RetireS1, Compose(error, new AppSettings { NotifyError = false }));
        Assert.Equal(RetireS1, Compose(finished, new AppSettings { NotifyTurnCompleted = false }));

        var allButFinished = new AppSettings { NotifyTurnCompleted = false };
        Assert.IsType<ShowIntent>(Compose(wait, allButFinished));
        Assert.IsType<ShowIntent>(Compose(error, allButFinished));
        Assert.IsType<ShowIntent>(Compose(finished, new AppSettings { NotifyNeedsInput = false, NotifyError = false }));
    }

    [Theory]
    [InlineData(AttentionKind.Permission)]
    [InlineData(AttentionKind.Plan)]
    [InlineData(AttentionKind.Question)]
    [InlineData(AttentionKind.Input)]
    public void Waits_play_the_attention_sound_unless_the_sound_is_off(AttentionKind kind)
    {
        Assert.Equal(NotificationSoundKind.Attention, Waiting(new AttentionDetail(kind)).Sound);
        Assert.Equal(NotificationSoundKind.None,
            Waiting(new AttentionDetail(kind), settings: new AppSettings { NotifySound = false }).Sound);
    }

    [Fact]
    public void Errors_play_the_attention_sound_and_finished_turns_the_soft_one_unless_the_sound_is_off()
    {
        var error = Change(Session(SessionPhase.Error, "API overloaded"), SessionPhase.Working);
        var off = new AppSettings { NotifySound = false };

        Assert.Equal(NotificationSoundKind.Attention, Card(error).Sound);
        Assert.Equal(NotificationSoundKind.None, Card(error, off).Sound);
        Assert.Equal(NotificationSoundKind.Done, Card(EndOfTurn()).Sound);
        Assert.Equal(NotificationSoundKind.None, Card(EndOfTurn(), off).Sound);
    }

    [Theory]
    [InlineData(SessionOrigin.Terminal, "demo")]
    [InlineData(SessionOrigin.App, "app · demo")]
    [InlineData(SessionOrigin.Cloud, "cloud · demo")]
    [InlineData(SessionOrigin.Routine, "routine · demo")]
    public void The_title_says_where_the_session_runs_when_it_is_not_a_terminal(SessionOrigin origin, string title) =>
        Assert.Equal(title, Card(Change(Session(SessionPhase.Error, origin: origin), SessionPhase.Working)).Title);

    [Fact]
    public void A_codex_card_carries_its_agent()
    {
        var card = Card(Change(Session(SessionPhase.Idle, agent: AgentKind.Codex), SessionPhase.Working));

        Assert.Equal("session:codex:s1", card.Key);
        Assert.Equal(AgentKind.Codex, card.Agent);
    }

    [Fact]
    public void The_state_key_tells_a_new_wait_from_the_same_one_seen_again()
    {
        var first = Waiting(new AttentionDetail(AttentionKind.Permission, "Bash", "npm test"));
        var seenAgain = Session(SessionPhase.NeedsInput, attention: new AttentionDetail(AttentionKind.Permission, "Bash", "npm test"),
            lastEventSeconds: 30);
        var again = Card(Change(seenAgain, SessionPhase.NeedsInput));   // a token update of the same wait
        var other = Waiting(new AttentionDetail(AttentionKind.Permission, "Bash", "npm run build"));

        Assert.Equal(first.StateKey, again.StateKey);
        Assert.NotEqual(first.StateKey, other.StateKey);
        Assert.Equal(first.Key, other.Key);
    }

    [Theory]
    [InlineData(NoticeSeverity.Info, NotificationTone.Neutral, 6)]
    [InlineData(NoticeSeverity.Warning, NotificationTone.Warning, null)]
    [InlineData(NoticeSeverity.Error, NotificationTone.Danger, null)]
    public void An_app_notice_is_a_silent_card_of_its_own(NoticeSeverity severity, NotificationTone tone, int? autoCloseSeconds)
    {
        var card = NotificationComposer.Notice("Claude Code · demo", "Terminale non trovato", severity, NotificationAction.PinNotch);

        Assert.Equal("notice:Claude Code · demo|Terminale non trovato", card.Key);
        Assert.Equal(card.Key, card.StateKey);
        Assert.Equal(NotificationKind.Notice, card.Kind);
        Assert.Equal(tone, card.Tone);
        Assert.Null(card.Agent);
        Assert.Equal("AIUsageMonitor", card.Title);
        Assert.Equal("Claude Code · demo", card.Label);
        Assert.Equal("Terminale non trovato", card.Message);
        TimeSpan? autoClose = autoCloseSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null;
        Assert.Equal(autoClose, card.AutoClose);
        Assert.Equal(NotificationSoundKind.None, card.Sound);
        Assert.Equal(NotificationAction.PinNotch, card.Action);
        Assert.Null(card.Session);
    }

    [Fact]
    public void A_notice_keeps_the_action_it_was_given()
    {
        var card = NotificationComposer.Notice("AIUsageMonitor · aggiornamento", "È disponibile la versione 1.5.0.",
            NoticeSeverity.Info, NotificationAction.OpenUpdate);

        Assert.Equal(NotificationAction.OpenUpdate, card.Action);
    }

    [Theory]
    [InlineData(0.0, "0s")]
    [InlineData(42.0, "42s")]
    [InlineData(59.9, "59s")]
    [InlineData(60.0, "1m 00s")]
    [InlineData(245.0, "4m 05s")]
    [InlineData(252.0, "4m 12s")]
    [InlineData(3599.0, "59m 59s")]
    [InlineData(3600.0, "1h 00m")]
    [InlineData(3780.0, "1h 03m")]
    [InlineData(3839.0, "1h 03m")]
    [InlineData(90000.0, "25h 00m")]
    [InlineData(-5.0, "0s")]
    public void Durations_read_as_seconds_minutes_or_hours(double seconds, string text) =>
        Assert.Equal(text, NotificationComposer.FormatDuration(TimeSpan.FromSeconds(seconds)));
}
