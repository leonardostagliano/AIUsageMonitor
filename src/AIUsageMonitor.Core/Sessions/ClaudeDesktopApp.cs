namespace AIUsageMonitor.Core.Sessions;

/// <summary>
/// Where a click on a cloud session goes: <see cref="App"/> in the Claude desktop app when set, then
/// <see cref="Web"/> on claude.ai, as the fallback or the only choice.
/// </summary>
public sealed record CloudSessionLinks(string? App, string Web);

/// <summary>
/// A top-level window without an owner, seen at one moment: handle, owning process, window class, whether it is
/// shown and its title. The title is only compared, never logged: the app's window can carry a conversation's name.
/// </summary>
public readonly record struct TopLevelWindow(nint Handle, int Pid, string ClassName, bool Visible, string Title);

/// <summary>
/// Recognizes the Claude desktop app among the running processes and picks where a cloud session opens. The app and
/// the Claude Code CLI both run as <c>claude.exe</c> (the CLI's native install is <c>~\.local\bin\claude.exe</c>, and
/// the app starts CLI children of its own for local sessions), so the image name only picks the
/// candidates and the full image path decides. The app is an Electron app, so its main process and every helper
/// process run the same executable, from one of its two installs: Squirrel
/// (<c>%LOCALAPPDATA%\AnthropicClaude\app-&lt;version&gt;\claude.exe</c>, plus the launcher in the root) or MSIX
/// (<c>...\WindowsApps\Claude_&lt;version&gt;_x64__&lt;publisher&gt;\app\Claude.exe</c>).
/// </summary>
public static class ClaudeDesktopApp
{
    private const string ImageName = "claude.exe";
    private const string SquirrelRoot = "AnthropicClaude";
    private const string SquirrelVersionPrefix = "app-";
    private const string ProgramFiles = "Program Files";
    private const string PackagesRoot = "WindowsApps";
    private const string PackagePrefix = "Claude_";
    private const string PackageAppFolder = "app";
    private const string ElectronWindowClass = "Chrome_WidgetWin_";

    /// <summary>True when the image name is the one the app runs under, so its full path is worth reading.</summary>
    public static bool IsCandidate(string? imageName) => string.Equals(imageName, ImageName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the full image path is the app's executable in one of its two install folders, both anchored: the
    /// per-user Squirrel install in <paramref name="localAppData"/> (<c>AnthropicClaude\app-&lt;version&gt;\claude.exe</c>,
    /// or the launcher right in <c>AnthropicClaude</c>) or an MSIX package in <c>&lt;drive&gt;:\Program Files\WindowsApps</c>
    /// (or <c>&lt;drive&gt;:\WindowsApps</c>, for apps moved to another drive), as <c>Claude_*\app\claude.exe</c>. A
    /// folder with the same name anywhere else is not the app, nor is any other folder inside an install, where the
    /// app keeps its CLI copies.
    /// </summary>
    public static bool IsAppImage(string? imagePath, string? localAppData)
    {
        var path = Segments(imagePath);
        if (path.Length < 2 || !IsCandidate(path[^1])) return false;
        return IsSquirrelImage(path, Segments(localAppData)) || IsPackageImage(path);
    }

    /// <summary>
    /// True for a window of the app's own UI: Electron creates every window of its main process, shown or hidden,
    /// with a <c>Chrome_WidgetWin_*</c> class. A console window or any other window of the same executable is not one.
    /// </summary>
    public static bool IsAppWindow(TopLevelWindow window) => window.ClassName.StartsWith(ElectronWindowClass, StringComparison.Ordinal);

    /// <summary>
    /// The window that shows the app acted on a link, from its windows and the foreground window before and after the
    /// link: an app window that came to the foreground, else a shown one that was not shown before (new, or reopened
    /// from the tray), else a shown one whose title changed; titled windows first. 0 when nothing moved: a window
    /// that was already on screen proves nothing, since the app may have ignored the link.
    /// </summary>
    public static nint ReactedWindow(IReadOnlyList<TopLevelWindow> before, nint foregroundBefore, IReadOnlyList<TopLevelWindow> after, nint foregroundAfter)
    {
        var shown = after.Where(w => w.Visible && IsAppWindow(w)).OrderBy(w => w.Title.Length == 0).ToList();
        if (foregroundAfter != 0 && foregroundAfter != foregroundBefore && shown.Any(w => w.Handle == foregroundAfter)) return foregroundAfter;
        var earlier = new Dictionary<nint, TopLevelWindow>();
        foreach (var window in before) earlier[window.Handle] = window;
        foreach (var window in shown)
        {
            if (!earlier.TryGetValue(window.Handle, out var old) || !old.Visible) return window.Handle;
        }
        foreach (var window in shown)
        {
            if (!string.Equals(earlier[window.Handle].Title, window.Title, StringComparison.Ordinal)) return window.Handle;
        }
        return 0;
    }

    /// <summary>
    /// The links a click on a cloud session tries, in order. The app comes first only while it is already running
    /// (a click never starts it just to show a link) and only for a plain session id; the page on claude.ai is
    /// always there.
    /// </summary>
    public static CloudSessionLinks LinksFor(string sessionId, bool appRunning) =>
        new(appRunning ? CloudSession.DesktopUrl(sessionId) : null, CloudSession.WebUrl(sessionId));

    /// <summary><c>&lt;localAppData&gt;\AnthropicClaude\claude.exe</c> or <c>...\AnthropicClaude\app-*\claude.exe</c>.</summary>
    private static bool IsSquirrelImage(string[] path, string[] localAppData)
    {
        var root = localAppData.Length;
        if (root == 0 || path.Length < root + 2 || path.Length > root + 3) return false;
        for (var i = 0; i < root; i++)
        {
            if (!Same(path[i], localAppData[i])) return false;
        }
        return Same(path[root], SquirrelRoot)
            && (path.Length == root + 2 || path[root + 1].StartsWith(SquirrelVersionPrefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>&lt;drive&gt;:\[Program Files\]WindowsApps\Claude_*\app\claude.exe</c>.</summary>
    private static bool IsPackageImage(string[] path)
    {
        var n = path.Length;
        if (n is not (5 or 6) || !IsDrive(path[0]) || (n == 6 && !Same(path[1], ProgramFiles))) return false;
        return Same(path[n - 4], PackagesRoot)
            && path[n - 3].StartsWith(PackagePrefix, StringComparison.OrdinalIgnoreCase)
            && Same(path[n - 2], PackageAppFolder);
    }

    /// <summary>The folders and file of a path, without the <c>\\?\</c> or <c>\\.\</c> prefix of a long path.</summary>
    private static string[] Segments(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return [];
        var segments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments[0] is "?" or "." ? segments[1..] : segments;
    }

    private static bool IsDrive(string segment) => segment.Length == 2 && char.IsAsciiLetter(segment[0]) && segment[1] == ':';

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
