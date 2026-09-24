using AIUsageMonitor.Core.Sessions;

namespace AIUsageMonitor.Tests;

public class ClaudeDesktopAppTests
{
    [Fact]
    public void The_desktop_link_uses_the_session_spelling_and_keeps_the_case_of_the_id()
    {
        Assert.Equal("claude://claude.ai/code/session_01E9zxspkDG9YVtXECLeyPJh", CloudSession.DesktopUrl("session_01E9zxspkDG9YVtXECLeyPJh"));
        Assert.Equal("claude://claude.ai/code/session_01E9zxspkDG9YVtXECLeyPJh", CloudSession.DesktopUrl("cse_01E9zxspkDG9YVtXECLeyPJh"));
        Assert.Equal("claude://claude.ai/code/session_01news", CloudSession.DesktopUrl("01news"));
        Assert.Equal("claude://claude.ai/code/session_a-b_c", CloudSession.DesktopUrl("cse_a-b_c"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("session_")]
    [InlineData("cse_")]
    [InlineData("session_01abc/../settings")]
    [InlineData("session_01abc?x=1")]
    [InlineData("session_01abc#top")]
    [InlineData("session_01%2Fabc")]
    [InlineData("session_01 abc")]
    [InlineData("session_01abc.")]
    [InlineData("cse_01àbc")]
    [InlineData("session_01abc\"&calc")]
    [InlineData("https://evil.example/x")]
    public void An_id_that_is_not_plain_gets_no_desktop_link_but_keeps_its_page(string id)
    {
        Assert.Null(CloudSession.DesktopUrl(id));
        Assert.StartsWith("https://claude.ai/code/session_", CloudSession.WebUrl(id));
    }

    [Fact]
    public void An_overlong_id_gets_no_desktop_link()
    {
        Assert.Equal("claude://claude.ai/code/session_" + new string('A', 128), CloudSession.DesktopUrl("session_" + new string('A', 128)));
        Assert.Null(CloudSession.DesktopUrl("session_" + new string('A', 129)));
        Assert.Null(CloudSession.DesktopUrl("cse_" + new string('A', 100_000)));
    }

    private const string LocalAppData = @"C:\Users\me\AppData\Local";

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\app-1.11187.4\claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\app-0.7.9\Claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\claude.exe")]
    [InlineData(@"c:\users\me\appdata\local\anthropicclaude\APP-1.0.0\CLAUDE.EXE")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.11187.4.0_x64__pzs8sxrjxfjjc\app\Claude.exe")]
    [InlineData(@"D:\WindowsApps\Claude_1.0.0.0_arm64__pzs8sxrjxfjjc\app\claude.exe")]
    [InlineData(@"\\?\C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe")]
    [InlineData(@"\\?\C:\Users\me\AppData\Local\AnthropicClaude\app-1.2.3\claude.exe")]
    [InlineData("C:/Users/me/AppData/Local/AnthropicClaude/app-1.2.3/claude.exe")]
    public void The_desktop_app_is_recognized_in_both_installs(string path)
    {
        Assert.True(ClaudeDesktopApp.IsAppImage(path, LocalAppData));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("claude.exe")]
    [InlineData(@"C:\Users\me\.local\bin\claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Roaming\Claude\claude-code\2.1.282\claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\claude-code\2.1.282\claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\app-1.2.3\resources\claude-code\claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\claude-code\claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\packages\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\app\resources\bin\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\claude-code\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\resources\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\app-1.2.3\node.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\AnthropicClaude\Update.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WindowsApps\Claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\ClaudeCode_1.0.0.0_x64__abc\app\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Other_1.0.0.0_x64__abc\app\claude.exe")]
    [InlineData(@"C:\tools\AnthropicClaudeBackup\app-1.0\claude.exe")]
    [InlineData(@"C:\Users\me\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\bin\claude.exe")]
    [InlineData(@"C:\Users\me\Downloads\AnthropicClaude\claude.exe")]
    [InlineData(@"C:\Users\me\Downloads\AnthropicClaude\app-1.0\claude.exe")]
    [InlineData(@"C:\Users\other\AppData\Local\AnthropicClaude\app-1.0\claude.exe")]
    [InlineData(@"C:\Users\AnthropicClaude\claude.exe")]
    [InlineData(@"\\server\share\AnthropicClaude\x\claude.exe")]
    [InlineData(@"C:\Temp\WindowsApps\Claude_fake\claude.exe")]
    [InlineData(@"C:\Temp\WindowsApps\Claude_fake\app\claude.exe")]
    [InlineData(@"C:\Users\me\Program Files\WindowsApps\Claude_fake\app\claude.exe")]
    public void The_cli_and_anything_else_is_not_the_desktop_app(string? path)
    {
        Assert.False(ClaudeDesktopApp.IsAppImage(path, LocalAppData));
    }

    [Fact]
    public void The_squirrel_install_is_anchored_to_the_local_app_data_of_this_user()
    {
        // A user profile named like the app's folder does not fool the check: the whole path is compared.
        const string odd = @"C:\Users\AnthropicClaude\AppData\Local";
        Assert.True(ClaudeDesktopApp.IsAppImage(@"C:\Users\AnthropicClaude\AppData\Local\AnthropicClaude\app-1.0\claude.exe", odd));
        Assert.False(ClaudeDesktopApp.IsAppImage(@"C:\Users\AnthropicClaude\claude.exe", odd));
        Assert.True(ClaudeDesktopApp.IsAppImage(@"C:\Users\me\AppData\Local\AnthropicClaude\app-1.0\claude.exe", @"C:\Users\me\AppData\Local\"));
        Assert.False(ClaudeDesktopApp.IsAppImage(@"C:\Users\me\AppData\Local\AnthropicClaude\app-1.0\claude.exe", null));
        Assert.False(ClaudeDesktopApp.IsAppImage(@"C:\Users\me\AppData\Local\AnthropicClaude\app-1.0\claude.exe", ""));
        Assert.True(ClaudeDesktopApp.IsAppImage(@"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__pzs8sxrjxfjjc\app\Claude.exe", null));
    }

    [Theory]
    [InlineData("claude.exe", true)]
    [InlineData("Claude.exe", true)]
    [InlineData("claude", false)]
    [InlineData("claude-code.exe", false)]
    [InlineData("node.exe", false)]
    [InlineData(null, false)]
    public void Only_claude_exe_is_worth_reading_the_path_of(string? name, bool candidate)
    {
        Assert.Equal(candidate, ClaudeDesktopApp.IsCandidate(name));
    }

    [Fact]
    public void A_running_app_gets_the_desktop_link_first_and_the_page_as_the_fallback()
    {
        var links = ClaudeDesktopApp.LinksFor("cse_01E9zxspkDG9YVtXECLeyPJh", appRunning: true);
        Assert.Equal("claude://claude.ai/code/session_01E9zxspkDG9YVtXECLeyPJh", links.App);
        Assert.Equal("https://claude.ai/code/session_01E9zxspkDG9YVtXECLeyPJh", links.Web);
    }

    [Fact]
    public void Without_the_app_running_the_session_opens_in_the_browser_only()
    {
        var links = ClaudeDesktopApp.LinksFor("session_01E9zxspkDG9YVtXECLeyPJh", appRunning: false);
        Assert.Null(links.App);
        Assert.Equal("https://claude.ai/code/session_01E9zxspkDG9YVtXECLeyPJh", links.Web);
    }

    [Fact]
    public void An_id_that_is_not_plain_opens_in_the_browser_even_with_the_app_running()
    {
        var links = ClaudeDesktopApp.LinksFor("session_01abc/../settings", appRunning: true);
        Assert.Null(links.App);
        Assert.Equal("https://claude.ai/code/session_01abc%2F..%2Fsettings", links.Web);
    }

    private static TopLevelWindow Window(nint handle, bool visible = true, string title = "Claude", string className = "Chrome_WidgetWin_1") =>
        new(handle, 42, className, visible, title);

    [Theory]
    [InlineData("Chrome_WidgetWin_1", true)]
    [InlineData("Chrome_WidgetWin_0", true)]
    [InlineData("ConsoleWindowClass", false)]
    [InlineData("", false)]
    public void Only_electron_windows_are_the_app_ui(string className, bool appWindow)
    {
        Assert.Equal(appWindow, ClaudeDesktopApp.IsAppWindow(Window(1, className: className)));
    }

    [Fact]
    public void A_window_already_on_screen_is_no_sign_that_the_app_took_the_link()
    {
        IReadOnlyList<TopLevelWindow> windows = [Window(1), Window(2, visible: false, title: "")];
        Assert.Equal(0, ClaudeDesktopApp.ReactedWindow(windows, 99, windows, 99));
        Assert.Equal(0, ClaudeDesktopApp.ReactedWindow(windows, 1, windows, 1));
        Assert.Equal(0, ClaudeDesktopApp.ReactedWindow([], 99, [], 99));
    }

    [Fact]
    public void The_app_coming_to_the_foreground_is_a_sign_it_took_the_link()
    {
        IReadOnlyList<TopLevelWindow> windows = [Window(1), Window(2)];
        Assert.Equal(2, ClaudeDesktopApp.ReactedWindow(windows, 99, windows, 2));
        Assert.Equal(0, ClaudeDesktopApp.ReactedWindow(windows, 99, windows, 7));
        Assert.Equal(0, ClaudeDesktopApp.ReactedWindow([Window(1)], 99, [Window(1, className: "ConsoleWindowClass")], 1));
    }

    [Fact]
    public void A_window_that_shows_up_is_a_sign_it_took_the_link()
    {
        Assert.Equal(1, ClaudeDesktopApp.ReactedWindow([Window(1, visible: false)], 99, [Window(1)], 99));
        Assert.Equal(3, ClaudeDesktopApp.ReactedWindow([Window(1)], 99, [Window(1), Window(3, title: ""), Window(4, className: "ConsoleWindowClass")], 99));
        Assert.Equal(4, ClaudeDesktopApp.ReactedWindow([], 99, [Window(3, title: ""), Window(4)], 99));
        Assert.Equal(0, ClaudeDesktopApp.ReactedWindow([], 99, [Window(3, visible: false)], 99));
    }

    [Fact]
    public void A_window_whose_title_changes_is_a_sign_it_took_the_link()
    {
        Assert.Equal(1, ClaudeDesktopApp.ReactedWindow([Window(1, title: "Claude")], 1, [Window(1, title: "Fix the login flow - Claude")], 1));
        Assert.Equal(0, ClaudeDesktopApp.ReactedWindow([Window(1, visible: false, title: "a")], 99, [Window(1, visible: false, title: "b")], 99));
    }
}
