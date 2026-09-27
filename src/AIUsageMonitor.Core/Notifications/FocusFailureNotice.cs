using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>
/// The notice shown when a click could not bring a session to the front. The session row of the notch and the session
/// card raise the same one, so both say the same thing.
/// </summary>
public static class FocusFailureNotice
{
    public const string TerminalNotFound = "Terminale non trovato";
    public const string PageNotOpened = "Pagina della sessione non aperta";

    /// <summary>"Claude Code · webshop": the agent and the session the click was for.</summary>
    public static string Title(SessionState session) => $"{session.Agent.DisplayName()} · {session.DisplayName}";

    /// <summary>
    /// A cloud session or routine opens a page (the Claude desktop app or claude.ai); any other session a terminal or an
    /// app window.
    /// </summary>
    public static string Text(SessionState session) =>
        session.Origin is SessionOrigin.Cloud or SessionOrigin.Routine ? PageNotOpened : TerminalNotFound;
}
