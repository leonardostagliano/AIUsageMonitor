using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Settings;

namespace AIUsageMonitor.Tests;

/// <summary>
/// What a click on a session card acts on: the board keeps the card's Session snapshot while its state key does not
/// change, so the click must look the session up again by the card's key and use the snapshot only when it is gone.
/// </summary>
public class SessionCardTargetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    private static SessionState Session(string name, AgentKind agent = AgentKind.Claude, string sessionId = "s1", HostInfo? host = null) =>
        new(agent, sessionId, name, @"C:\p\demo", SessionPhase.NeedsInput, "Claude needs your permission", T0, T0,
            Host: host, Attention: new AttentionDetail(AttentionKind.Permission, "Bash", "npm test"));

    private static NotificationCard CardOf(SessionState session) =>
        Assert.IsType<ShowIntent>(NotificationComposer.Compose(
            new SessionChange(SessionChangeKind.Updated, session, SessionPhase.Working), new AppSettings())).Card;

    [Fact]
    public void A_click_acts_on_the_current_state_of_the_session_not_on_the_snapshot_of_the_card()
    {
        var card = CardOf(Session("demo"));
        // Same agent and id, meanwhile renamed and bound to a new terminal: the card kept its old snapshot.
        var current = Session("demo-renamed", host: new HostInfo(4242, "w1:p2", null, null, null));

        var target = SessionCardTarget.Resolve(card, [Session("other", sessionId: "s2"), current]);

        Assert.Same(current, target);
    }

    [Fact]
    public void The_snapshot_of_the_card_is_used_when_the_session_is_gone()
    {
        var snapshot = Session("demo");
        var card = CardOf(snapshot);

        Assert.Same(card.Session, SessionCardTarget.Resolve(card, []));
        Assert.Equal(snapshot, SessionCardTarget.Resolve(card, [Session("other", sessionId: "s2")]));
    }

    [Fact]
    public void Only_a_session_of_the_same_agent_and_id_is_the_current_one()
    {
        var card = CardOf(Session("demo"));
        var codexTwin = Session("codex-twin", agent: AgentKind.Codex);

        Assert.Same(card.Session, SessionCardTarget.Resolve(card, [codexTwin]));
    }

    [Fact]
    public void An_app_notice_targets_no_session()
    {
        var notice = NotificationComposer.Notice("Claude Code · hook", "Hook installati", NoticeSeverity.Info, NotificationAction.PinNotch);

        Assert.Null(SessionCardTarget.Resolve(notice, [Session("demo")]));
    }
}
