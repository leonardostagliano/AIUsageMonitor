using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Presentation;

namespace AIUsageMonitor.Tests;

public class NotificationPresentationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Eight = TimeSpan.FromSeconds(8);

    private static NotificationCard Card(NotificationKind kind = NotificationKind.Finished, TimeSpan? autoClose = null, AgentKind? agent = AgentKind.Claude) =>
        new("session:claude:s1", kind, NotificationTone.Success, agent, "demo", "Finito", "Turno completato",
            "Idle|Finito|Turno completato", autoClose, NotificationSoundKind.None, NotificationAction.FocusSession);

    private static BoardEntry Entry(TimeSpan? autoClose, DateTimeOffset? expiresAt, TimeSpan? remaining) =>
        new(Card(autoClose: autoClose), Now.AddSeconds(-2), expiresAt, remaining);

    private static StackSlot Live(string key) => new(key, false);
    private static StackSlot Leaving(string key) => new(key, true);

    [Theory]
    [InlineData(0, "ora")]
    [InlineData(59, "ora")]
    [InlineData(-5, "ora")]              // clock skew: never a negative age
    [InlineData(60, "1 min")]
    [InlineData(150, "2 min")]
    [InlineData(3599, "59 min")]
    [InlineData(3600, "1 h")]
    [InlineData(86399, "23 h")]
    [InlineData(86400, "1 g")]
    [InlineData(180000, "2 g")]
    public void The_age_reads_now_then_minutes_hours_and_days(int seconds, string expected) =>
        Assert.Equal(expected, NotificationPresentation.AgeText(Now.AddSeconds(-seconds), Now));

    /// <summary>
    /// Row 3 is one line (spec §5.2): a TextBlock draws every line break of its text even without wrapping, and the last
    /// message of a turn often spans several. The whole text stays in the tooltip; nothing is cut here, the ellipsis is
    /// the TextBlock's.
    /// </summary>
    [Theory]
    [InlineData("Tutti i test passano", "Tutti i test passano")]
    [InlineData("Fatto.\r\n\r\n- build ok\r\n- test ok", "Fatto. - build ok - test ok")]
    [InlineData("\nRiepilogo:\n\n\tdue file", "Riepilogo: due file")]
    [InlineData("  Errore\r API   overloaded ", "Errore API overloaded")]
    [InlineData("\r\n \t", "")]
    [InlineData("", "")]
    public void The_message_of_a_card_reads_on_one_line(string message, string expected) =>
        Assert.Equal(expected, NotificationPresentation.OneLine(message));

    [Fact]
    public void A_long_message_on_one_line_is_not_cut()
    {
        var message = string.Join("\n", Enumerable.Repeat("riga di prova", 20));

        Assert.Equal(string.Join(" ", Enumerable.Repeat("riga di prova", 20)), NotificationPresentation.OneLine(message));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(-1, "")]
    [InlineData(1, "+1 altra")]
    [InlineData(4, "+4 altre")]
    public void The_pill_counts_the_cards_waiting_for_a_slot(int hidden, string expected) =>
        Assert.Equal(expected, NotificationPresentation.MoreText(hidden));

    [Theory]
    [InlineData(NotificationTone.Success, "SuccessText")]
    [InlineData(NotificationTone.Warning, "WarningText")]
    [InlineData(NotificationTone.Question, "Focus")]
    [InlineData(NotificationTone.Danger, "DangerText")]
    [InlineData(NotificationTone.Neutral, "TextMuted")]
    public void Every_tone_has_its_text_brush(NotificationTone tone, string key) =>
        Assert.Equal(key, NotificationPresentation.ToneKey(tone));

    [Fact]
    public void Only_cards_that_close_by_themselves_have_a_timer_brush()
    {
        Assert.Equal("ToneNormalBrush", NotificationPresentation.TimerBrushKey(Card(NotificationKind.Finished, Eight)));
        Assert.Equal("TextDisabled", NotificationPresentation.TimerBrushKey(Card(NotificationKind.Notice, TimeSpan.FromSeconds(6), agent: null)));
        Assert.Null(NotificationPresentation.TimerBrushKey(Card(NotificationKind.Permission)));
        Assert.Null(NotificationPresentation.TimerBrushKey(Card(NotificationKind.Notice, agent: null)));
    }

    [Fact]
    public void Agent_cards_use_the_brand_of_their_agent_and_notices_none()
    {
        Assert.Equal(("ClaudeGlow", "BrandClaude", "ClaudeIcon"),
            (NotificationPresentation.GlowKey(AgentKind.Claude), NotificationPresentation.BrandKey(AgentKind.Claude), NotificationPresentation.IconKey(AgentKind.Claude)));
        Assert.Equal(("CodexGlow", "BrandCodex", "CodexIcon"),
            (NotificationPresentation.GlowKey(AgentKind.Codex), NotificationPresentation.BrandKey(AgentKind.Codex), NotificationPresentation.IconKey(AgentKind.Codex)));
        Assert.Null(NotificationPresentation.GlowKey(null));
        Assert.Null(NotificationPresentation.BrandKey(null));
        Assert.Null(NotificationPresentation.IconKey(null));
    }

    [Fact]
    public void A_running_timer_counts_down_to_its_expiry()
    {
        var entry = Entry(Eight, Now.AddSeconds(6), null);
        Assert.Equal(TimeSpan.FromSeconds(6), NotificationPresentation.TimerRemaining(entry, Now));
        Assert.Equal(0.75, NotificationPresentation.TimerFraction(entry, Now), 6);
    }

    [Fact]
    public void The_remaining_time_of_the_board_wins_while_it_is_paused()
    {
        // Hover pauses the board: Remaining stops moving while ExpiresAt would keep counting.
        var entry = Entry(Eight, Now.AddSeconds(7), TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), NotificationPresentation.TimerRemaining(entry, Now));
        Assert.Equal(0.25, NotificationPresentation.TimerFraction(entry, Now), 6);
        Assert.Equal(0.25, NotificationPresentation.TimerFraction(entry, Now.AddSeconds(30)), 6);
    }

    [Fact]
    public void The_timer_stays_between_empty_and_full()
    {
        Assert.Equal(0, NotificationPresentation.TimerFraction(Entry(Eight, Now.AddSeconds(-1), null), Now), 6);
        Assert.Equal(1, NotificationPresentation.TimerFraction(Entry(Eight, null, null), Now), 6);
        Assert.Equal(1, NotificationPresentation.TimerFraction(Entry(Eight, null, TimeSpan.FromSeconds(20)), Now), 6);
    }

    [Fact]
    public void A_card_that_stays_has_no_timer()
    {
        var entry = Entry(null, null, null);
        Assert.Null(NotificationPresentation.TimerRemaining(entry, Now));
        Assert.Equal(0, NotificationPresentation.TimerFraction(entry, Now));
    }

    [Fact]
    public void New_cards_follow_the_board_order()
    {
        Assert.Equal([Live("a"), Live("b")], NotificationPresentation.Arrange([], ["a", "b"]));
        Assert.Equal([Live("c"), Live("a"), Live("b")], NotificationPresentation.Arrange([Live("a"), Live("b")], ["c", "a", "b"]));
    }

    [Fact]
    public void A_card_replaced_in_place_keeps_its_slot()
    {
        var current = new[] { Live("a"), Live("b") };
        Assert.Equal(current, NotificationPresentation.Arrange(current, ["a", "b"]));
    }

    [Fact]
    public void A_card_that_leaves_stays_where_it_was_until_its_exit_ends()
    {
        Assert.Equal([Live("a"), Leaving("b"), Live("c")],
            NotificationPresentation.Arrange([Live("a"), Live("b"), Live("c")], ["a", "c"]));
        // A new card on top does not move the one that is leaving.
        Assert.Equal([Live("n"), Live("a"), Leaving("b"), Live("c")],
            NotificationPresentation.Arrange([Live("a"), Live("b"), Live("c")], ["n", "a", "c"]));
        Assert.Equal([Leaving("a"), Live("b")], NotificationPresentation.Arrange([Live("a"), Live("b")], ["b"]));
        Assert.Equal([Leaving("x"), Leaving("y")], NotificationPresentation.Arrange([Leaving("x"), Live("y")], []));
    }

    [Fact]
    public void The_board_order_wins_and_a_leaving_card_can_come_back()
    {
        // A permission takes precedence over a "Finito": the live cards swap, the leaving one keeps its neighbour.
        Assert.Equal([Leaving("x"), Live("p"), Live("f"), Leaving("y")],
            NotificationPresentation.Arrange([Leaving("x"), Live("f"), Leaving("y"), Live("p")], ["p", "f"]));
        Assert.Equal([Live("b"), Live("a")], NotificationPresentation.Arrange([Live("a"), Leaving("b")], ["b", "a"]));
    }

    // The board keeps its timers frozen until it hears SetHover(false), and WPF may never send MouseLeave to a window
    // hidden under the mouse: hiding the window or emptying the stack must end the hover by itself.
    [Theory]
    [InlineData(true, true, 1, true)]
    [InlineData(true, true, 3, true)]
    [InlineData(false, true, 2, false)]
    [InlineData(true, false, 2, false)]   // hidden under the mouse
    [InlineData(true, true, 0, false)]    // only cards on their way out are left
    [InlineData(false, false, 0, false)]
    public void The_stack_counts_as_hovered_only_while_it_shows_live_cards(bool pointerOver, bool windowShown, int liveCards, bool expected) =>
        Assert.Equal(expected, NotificationPresentation.StackHover(pointerOver, windowShown, liveCards));
}
