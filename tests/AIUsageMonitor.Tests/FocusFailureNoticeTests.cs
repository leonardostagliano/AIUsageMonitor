using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;

namespace AIUsageMonitor.Tests;

public class FocusFailureNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    private static SessionState Session(AgentKind agent, string name, SessionOrigin origin) =>
        new(agent, "s-1", name, null, SessionPhase.Idle, null, Now, Now, Origin: origin);

    [Theory]
    [InlineData(SessionOrigin.Terminal, "Terminale non trovato")]
    [InlineData(SessionOrigin.App, "Terminale non trovato")]
    [InlineData(SessionOrigin.Cloud, "Pagina della sessione non aperta")]
    [InlineData(SessionOrigin.Routine, "Pagina della sessione non aperta")]
    public void Text_depends_on_where_the_session_runs(SessionOrigin origin, string expected) =>
        Assert.Equal(expected, FocusFailureNotice.Text(Session(AgentKind.Claude, "webshop", origin)));

    [Fact]
    public void Title_names_the_agent_and_the_session()
    {
        Assert.Equal("Claude Code · webshop", FocusFailureNotice.Title(Session(AgentKind.Claude, "webshop", SessionOrigin.Terminal)));
        Assert.Equal("Codex · billing-service", FocusFailureNotice.Title(Session(AgentKind.Codex, "billing-service", SessionOrigin.Cloud)));
    }
}
