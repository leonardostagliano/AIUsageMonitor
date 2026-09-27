using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Settings;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

/// <summary>
/// Every path that removes a session reaches the notification composer as a Removed change, whatever else it is
/// (quiet included): its card is retired and the board forgets the state closed with ✕, so a session that comes back
/// with the same id and state shows its card again.
/// </summary>
public class SessionRemovalRetiresCardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan AppIdleWindow = TimeSpan.FromMinutes(10);

    public static TheoryData<string> Removals() => ["SessionEnd", "quiet SessionEnd", "RemoveStale", "RemoveIdle"];

    private static HookEvent Ev(DateTimeOffset ts, string evt, string? message = null, bool quiet = false) =>
        new(ts, AgentKind.Claude, evt, "s1", @"C:\p\demo", null, message, null, Origin: SessionOrigin.App, Quiet: quiet);

    [Theory]
    [MemberData(nameof(Removals))]
    public void A_removed_session_retires_its_card_and_the_board_forgets_the_closed_state(string removal)
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        var board = new NotificationBoard(clock);
        var settings = new AppSettings();
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;
        tracker.Changed += change =>
        {
            if (NotificationComposer.Compose(change, settings) is { } intent) board.Apply(intent);
        };

        // An error card, closed with ✕: the same state does not bring it back.
        tracker.Apply(Ev(T0, "UserPromptSubmit"));
        tracker.Apply(Ev(T0.AddSeconds(1), "StopFailure", "API Error: 529 overloaded"));
        var key = NotificationComposer.SessionKey(AgentKind.Claude, "s1");
        Assert.Equal(key, Assert.Single(board.Visible).Card.Key);
        board.Dismiss(key);
        tracker.Apply(Ev(T0.AddSeconds(2), "StopFailure", "API Error: 529 overloaded"));
        Assert.True(board.IsEmpty);

        clock.Advance(removal switch
        {
            "RemoveStale" => StaleAfter + TimeSpan.FromMinutes(1),
            "RemoveIdle" => AppIdleWindow + TimeSpan.FromMinutes(1),
            _ => TimeSpan.FromSeconds(10)
        });
        switch (removal)
        {
            case "SessionEnd": tracker.Apply(Ev(clock.UtcNow, "SessionEnd")); break;
            case "quiet SessionEnd": tracker.Apply(Ev(clock.UtcNow, "SessionEnd", quiet: true)); break;
            case "RemoveStale": tracker.RemoveStale(StaleAfter); break;
            case "RemoveIdle": tracker.RemoveIdle(SessionOrigin.App, AppIdleWindow); break;
        }

        Assert.Empty(tracker.Sessions);
        var removed = changes.Last();
        Assert.Equal(SessionChangeKind.Removed, removed.Kind);
        Assert.Equal(new RetireIntent(key), NotificationComposer.Compose(removed, settings));

        // The same id comes back straight in the same state (no turn in between that would retire the card anyway):
        // the card shows again.
        tracker.Apply(Ev(clock.UtcNow.AddSeconds(1), "StopFailure", "API Error: 529 overloaded"));
        Assert.Equal(key, Assert.Single(board.Visible).Card.Key);
    }
}
