using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

/// <summary>
/// Card lifecycle of the notification stack: replace per session, retire, dedupe after ✕, at most three on screen with
/// the persistent cards first, timers that run only on screen and stop under the mouse, the quiet queue, and the sound
/// each operation plays.
/// </summary>
public class NotificationBoardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Eight = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan Six = TimeSpan.FromSeconds(6);

    private static readonly BoardResult Unchanged = new(false, NotificationSoundKind.None);
    private static readonly BoardResult Redrawn = new(true, NotificationSoundKind.None);
    private static readonly BoardResult RedrawnWithDone = new(true, NotificationSoundKind.Done);
    private static readonly BoardResult RedrawnWithAttention = new(true, NotificationSoundKind.Attention);

    private readonly FakeClock _clock = new(T0);
    private readonly NotificationBoard _board;

    public NotificationBoardTests() => _board = new NotificationBoard(_clock);

    private static NotificationCard Permission(string session, string command = "npm test",
        NotificationSoundKind sound = NotificationSoundKind.Attention) =>
        SessionCard(session, NotificationKind.Permission, NotificationTone.Warning, "NeedsInput", "Permesso · Bash", command, null, sound);

    private static NotificationCard Question(string session, string question = "Quale database uso?") =>
        SessionCard(session, NotificationKind.Question, NotificationTone.Question, "NeedsInput", "Domanda", question, null,
            NotificationSoundKind.Attention);

    private static NotificationCard Finished(string session, string message = "Turno completato") =>
        SessionCard(session, NotificationKind.Finished, NotificationTone.Success, "Idle", "Finito · 42s", message, Eight,
            NotificationSoundKind.Done);

    // The session id doubles as the title, so the tests can read the stack by name.
    private static NotificationCard SessionCard(string session, NotificationKind kind, NotificationTone tone, string phase,
        string label, string message, TimeSpan? autoClose, NotificationSoundKind sound) =>
        new($"session:claude:{session}", kind, tone, AgentKind.Claude, session, label, message, $"{phase}|{label}|{message}",
            autoClose, sound, NotificationAction.FocusSession);

    private static NotificationCard Notice(string title, string text, TimeSpan? autoClose,
        NotificationSoundKind sound = NotificationSoundKind.None)
    {
        var key = $"notice:{title}|{text}";
        return new NotificationCard(key, NotificationKind.Notice, autoClose is null ? NotificationTone.Warning : NotificationTone.Neutral,
            null, "AIUsageMonitor", title, text, key, autoClose, sound, NotificationAction.PinNotch);
    }

    private BoardResult Show(NotificationCard card) => _board.Apply(new ShowIntent(card));

    private BoardResult Retire(string session) => _board.Apply(new RetireIntent($"session:claude:{session}"));

    private BoardResult Dismiss(string session) => _board.Dismiss($"session:claude:{session}");

    /// <summary>The stack from the top: the session of a session card, the title of a notice.</summary>
    private List<string> Stack() =>
        _board.Visible.Select(e => e.Card.Kind == NotificationKind.Notice ? e.Card.Label : e.Card.Title).ToList();

    [Fact]
    public void A_new_card_goes_on_screen_with_its_sound()
    {
        Assert.True(_board.IsEmpty);

        Assert.Equal(RedrawnWithAttention, Show(Permission("a")));

        var entry = Assert.Single(_board.Visible);
        Assert.Equal("Permesso · Bash", entry.Card.Label);
        Assert.Equal(T0, entry.ShownAt);
        Assert.Null(entry.ExpiresAt);          // a permission waits for the user
        Assert.Null(entry.Remaining);
        Assert.Equal(0, _board.HiddenCount);
        Assert.False(_board.IsEmpty);
        Assert.False(_board.IsQuiet);
    }

    [Fact]
    public void A_finished_card_plays_the_soft_sound_and_its_timer_starts_on_screen()
    {
        Assert.Equal(RedrawnWithDone, Show(Finished("a")));

        var entry = Assert.Single(_board.Visible);
        Assert.Equal(T0 + Eight, entry.ExpiresAt);
        Assert.Null(entry.Remaining);
    }

    [Fact]
    public void The_same_state_of_a_session_changes_nothing()
    {
        Show(Finished("a"));
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(Unchanged, Show(Finished("a")));

        var entry = Assert.Single(_board.Visible);
        Assert.Equal(T0, entry.ShownAt);
        Assert.Equal(T0 + Eight, entry.ExpiresAt);
    }

    [Fact]
    public void A_new_state_replaces_the_card_of_its_session()
    {
        Show(Finished("a"));
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(RedrawnWithAttention, Show(Permission("a")));

        var entry = Assert.Single(_board.Visible);
        Assert.Equal(NotificationKind.Permission, entry.Card.Kind);
        Assert.Equal(T0.AddSeconds(3), entry.ShownAt);
        Assert.Null(entry.ExpiresAt);          // a persistent card now: the timer of "Finito" is gone
    }

    [Fact]
    public void A_replaced_timed_card_sounds_and_starts_its_timer_again()
    {
        Show(Finished("a", "Primo turno"));
        _clock.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(RedrawnWithDone, Show(Finished("a", "Secondo turno")));

        var entry = Assert.Single(_board.Visible);
        Assert.Equal("Secondo turno", entry.Card.Message);
        Assert.Equal(T0.AddSeconds(5) + Eight, entry.ExpiresAt);
    }

    [Fact]
    public void A_replaced_card_moves_to_the_top_of_the_stack()
    {
        Show(Permission("a", "npm test"));
        Show(Permission("b"));

        Show(Permission("a", "npm run build"));

        Assert.Equal(["a", "b"], Stack());
        Assert.Equal("npm run build", _board.Visible[0].Card.Message);
    }

    [Fact]
    public void An_identical_notice_is_renewed_without_a_second_sound()
    {
        // The composer gives notices no sound; the board must not sound a renewal even when a notice has one.
        var notice = Notice("AIUsageMonitor aggiornato", "Versione 1.4.0", Six, NotificationSoundKind.Attention);
        Assert.Equal(RedrawnWithAttention, Show(notice));
        _clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(Redrawn, Show(notice));

        var entry = Assert.Single(_board.Visible);
        Assert.Equal(T0.AddSeconds(4), entry.ShownAt);
        Assert.Equal(T0.AddSeconds(10), entry.ExpiresAt);
    }

    [Fact]
    public void Different_notices_are_separate_cards()
    {
        Show(Notice("Hook installati", "Claude Code", Six));
        Show(Notice("Hook installati", "Codex", Six));

        Assert.Equal(2, _board.Visible.Count);
    }

    [Fact]
    public void A_retire_removes_the_card_and_an_unknown_key_changes_nothing()
    {
        Show(Permission("a"));

        Assert.Equal(Redrawn, Retire("a"));
        Assert.True(_board.IsEmpty);
        Assert.Empty(_board.Visible);
        Assert.Equal(Unchanged, Retire("a"));
    }

    [Fact]
    public void A_state_closed_with_the_x_does_not_come_back_until_the_session_changes()
    {
        Show(Permission("a", "npm test"));

        Assert.Equal(Redrawn, Dismiss("a"));
        Assert.True(_board.IsEmpty);

        Assert.Equal(Unchanged, Show(Permission("a", "npm test")));      // the same permission, notified again
        Assert.True(_board.IsEmpty);

        Assert.Equal(RedrawnWithAttention, Show(Permission("a", "npm run build")));   // a new one
        Assert.Single(_board.Visible);
    }

    [Fact]
    public void A_retire_forgets_the_state_closed_with_the_x()
    {
        Show(Finished("a"));
        Dismiss("a");

        Retire("a");                           // a new turn started

        Assert.Equal(RedrawnWithDone, Show(Finished("a")));
        Assert.Single(_board.Visible);
    }

    [Fact]
    public void Dismissing_a_card_that_is_not_there_changes_nothing() =>
        Assert.Equal(Unchanged, Dismiss("none"));

    [Fact]
    public void A_notice_closed_with_the_x_can_come_back()
    {
        var notice = Notice("Claude Code · demo", "Terminale non trovato", null);
        Show(notice);
        _board.Dismiss(notice.Key);

        Assert.Equal(Redrawn, Show(notice));   // the next failed click says it again
        Assert.Single(_board.Visible);
    }

    [Fact]
    public void Timed_cards_leave_when_their_time_is_up()
    {
        Show(Finished("a"));

        _clock.Advance(TimeSpan.FromMilliseconds(7_900));
        Assert.Equal(Unchanged, _board.Tick());
        Assert.Single(_board.Visible);

        _clock.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Equal(Redrawn, _board.Tick());
        Assert.True(_board.IsEmpty);
    }

    [Fact]
    public void An_expired_state_is_remembered_like_one_closed_with_the_x()
    {
        Show(Finished("a"));
        _clock.Advance(Eight);
        _board.Tick();

        Assert.Equal(Unchanged, Show(Finished("a")));

        Retire("a");
        Assert.Equal(RedrawnWithDone, Show(Finished("a")));
    }

    [Fact]
    public void Persistent_cards_never_expire()
    {
        Show(Permission("a"));
        _clock.Advance(TimeSpan.FromHours(5));

        Assert.Equal(Unchanged, _board.Tick());
        Assert.Single(_board.Visible);
    }

    [Fact]
    public void At_most_three_cards_are_on_screen_newest_first()
    {
        Show(Permission("a"));
        Show(Permission("b"));
        Show(Permission("c"));
        Show(Permission("d"));                 // all at the same instant: the order of arrival decides

        Assert.Equal(["d", "c", "b"], Stack());
        Assert.Equal(1, _board.HiddenCount);

        Retire("c");

        Assert.Equal(["d", "b", "a"], Stack());   // the waiting card moves up
        Assert.Equal(0, _board.HiddenCount);
    }

    [Fact]
    public void A_finished_card_never_pushes_out_a_pending_permission()
    {
        Show(Permission("a"));
        Show(Permission("b"));
        Show(Permission("c"));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Show(Finished("d"));

        Assert.Equal(["c", "b", "a"], Stack());
        Assert.Equal(1, _board.HiddenCount);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Unchanged, _board.Tick());   // hidden: its timer has not started
        Retire("b");

        Assert.Equal(["d", "c", "a"], Stack());
        Assert.Equal(_clock.UtcNow + Eight, _board.Visible[0].ExpiresAt);   // the full 8 s from the moment it shows
    }

    [Fact]
    public void Timed_cards_fill_the_free_places_newest_first()
    {
        Show(Permission("p"));
        Show(Finished("f1"));
        Show(Finished("f2"));
        Show(Finished("f3"));

        Assert.Equal(["f3", "f2", "p"], Stack());
        Assert.Equal(1, _board.HiddenCount);
    }

    [Fact]
    public void A_timed_card_pushed_off_screen_loses_its_timer()
    {
        Show(Finished("f"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        Show(Permission("a"));
        Show(Permission("b"));
        Show(Permission("c"));

        Assert.DoesNotContain("f", Stack());
        _clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(Unchanged, _board.Tick());   // on screen it would have expired at T0 + 8 s

        Retire("a");

        var finished = _board.Visible.Single(e => e.Card.Title == "f");
        Assert.Equal(T0.AddSeconds(15) + Eight, finished.ExpiresAt);
    }

    [Fact]
    public void The_mouse_freezes_every_timer_and_leaving_resumes_it()
    {
        Show(Finished("a"));
        _clock.Advance(TimeSpan.FromSeconds(3));

        Assert.Equal(Redrawn, _board.SetHover(true));
        var frozen = Assert.Single(_board.Visible);
        Assert.Null(frozen.ExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(5), frozen.Remaining);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(Unchanged, _board.Tick());

        Assert.Equal(Redrawn, _board.SetHover(false));
        var running = Assert.Single(_board.Visible);
        Assert.Equal(T0.AddSeconds(68), running.ExpiresAt);
        Assert.Null(running.Remaining);

        _clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(Unchanged, _board.Tick());
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(Redrawn, _board.Tick());
        Assert.True(_board.IsEmpty);
    }

    [Fact]
    public void A_card_that_shows_under_the_mouse_starts_frozen()
    {
        _board.SetHover(true);
        Show(Finished("a"));

        var entry = Assert.Single(_board.Visible);
        Assert.Null(entry.ExpiresAt);
        Assert.Equal(Eight, entry.Remaining);

        _clock.Advance(TimeSpan.FromSeconds(30));
        _board.SetHover(false);

        Assert.Equal(T0.AddSeconds(30) + Eight, _board.Visible[0].ExpiresAt);
    }

    [Fact]
    public void The_mouse_changes_nothing_without_a_running_timer()
    {
        Show(Permission("a"));

        Assert.Equal(Unchanged, _board.SetHover(true));
        Assert.Equal(Unchanged, _board.SetHover(true));
        Assert.Equal(Unchanged, _board.SetHover(false));
    }

    [Fact]
    public void Quiet_mode_queues_new_cards_without_a_sound()
    {
        Assert.Equal(Unchanged, _board.SetQuiet(true));   // nothing on the board to hide
        Assert.True(_board.IsQuiet);

        Assert.Equal(Redrawn, Show(Permission("a")));
        Assert.Equal(Redrawn, Show(Finished("b")));
        Assert.Equal(Redrawn, Show(Permission("a", "npm run build")));   // a new state while quiet: stored, still silent

        Assert.Empty(_board.Visible);
        Assert.Equal(0, _board.HiddenCount);
        Assert.False(_board.IsEmpty);
        Assert.Equal(Unchanged, _board.SetQuiet(true));
    }

    [Fact]
    public void Quiet_mode_takes_every_card_off_screen()
    {
        Show(Permission("a"));
        Show(Permission("b"));
        Show(Permission("c"));
        Show(Permission("d"));

        Assert.Equal(Redrawn, _board.SetQuiet(true));

        Assert.Empty(_board.Visible);
        Assert.Equal(0, _board.HiddenCount);
    }

    [Fact]
    public void Leaving_quiet_mode_shows_only_the_cards_still_valid_with_one_sound()
    {
        Show(Finished("f0"));
        _board.SetQuiet(true);
        Show(Permission("p"));
        Show(Question("q"));
        Show(Finished("f1"));
        Show(Notice("AIUsageMonitor aggiornato", "Versione 1.4.0", Six));
        Show(Notice("Claude Code · demo", "Terminale non trovato", null));

        Assert.Equal(RedrawnWithAttention, _board.SetQuiet(false));   // two cards owe it, and it plays once

        Assert.False(_board.IsQuiet);
        Assert.Equal(["Claude Code · demo", "q", "p"], Stack());
        Assert.Equal(0, _board.HiddenCount);
        Assert.Equal(Unchanged, Show(Finished("f1")));   // dropped like an expired card
    }

    [Fact]
    public void A_queued_finished_turn_and_a_notice_owe_no_sound()
    {
        _board.SetQuiet(true);
        Show(Finished("f"));
        Show(Notice("Claude Code · demo", "Terminale non trovato", null));

        Assert.Equal(Redrawn, _board.SetQuiet(false));
        Assert.Equal(["Claude Code · demo"], Stack());
    }

    [Fact]
    public void Only_a_card_that_asks_for_attention_owes_its_sound_to_the_end_of_quiet()
    {
        // A card that stays and has the soft sound: it survives the end of quiet, yet only Attention is ever owed.
        var stays = SessionCard("s", NotificationKind.Finished, NotificationTone.Success, "Idle", "Finito", "Turno completato",
            null, NotificationSoundKind.Done);
        _board.SetQuiet(true);
        Show(stays);

        Assert.Equal(Redrawn, _board.SetQuiet(false));
        Assert.Equal(["s"], Stack());
    }

    [Fact]
    public void The_latest_state_of_a_queued_card_decides_what_it_owes()
    {
        _board.SetQuiet(true);
        Show(Permission("a", "npm test"));
        Show(Permission("a", "npm run build", NotificationSoundKind.None));   // the sound was switched off meanwhile

        Assert.Equal(Redrawn, _board.SetQuiet(false));
        Assert.Equal("npm run build", Assert.Single(_board.Visible).Card.Message);
    }

    [Fact]
    public void A_card_retired_during_quiet_owes_no_sound()
    {
        _board.SetQuiet(true);
        Show(Permission("a"));
        Retire("a");                           // answered in the terminal meanwhile

        Assert.Equal(Unchanged, _board.SetQuiet(false));
        Assert.True(_board.IsEmpty);
    }

    [Fact]
    public void A_card_that_sounded_before_the_quiet_does_not_sound_again()
    {
        Show(Permission("a"));
        _board.SetQuiet(true);

        Assert.Equal(Redrawn, _board.SetQuiet(false));
        Assert.Equal(["a"], Stack());
    }

    [Theory]
    [InlineData(NotificationSoundKind.None, NotificationSoundKind.Done)]
    [InlineData(NotificationSoundKind.Done, NotificationSoundKind.Attention)]
    [InlineData(NotificationSoundKind.None, NotificationSoundKind.Attention)]
    public void The_stronger_sound_wins_when_two_are_joined(NotificationSoundKind weaker, NotificationSoundKind stronger)
    {
        // The App joins the sounds of two operations of one step (the quiet probe, then the card) with the larger value.
        Assert.True(stronger > weaker);
        Assert.Equal(stronger, (NotificationSoundKind)Math.Max((int)weaker, (int)stronger));
        Assert.Equal(stronger, (NotificationSoundKind)Math.Max((int)stronger, (int)weaker));
    }

    [Fact]
    public void Apply_rejects_a_missing_intent() =>
        Assert.Throws<ArgumentNullException>(() => _board.Apply(null!));
}
