using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Core.Terminal;

namespace AIUsageMonitor.Tests;

public class WmuxClientTests
{
    private const string Workspaces = """
        [{"id":"ws-other","name":"Workspace 2","ptyIds":["daemon-00000001"]},
         {"id":"ws-main","name":"Workspace 1","activePtyId":"daemon-6b28a6c0","ptyIds":["daemon-8bab41b8","daemon-6b28a6c0"]}]
        """;

    private const string Panes = """
        {"asOfSeq":878,"panes":[
          {"id":"pane-a","surfaceCount":1,"active":true,"surfacePtyIds":["daemon-6b28a6c0"],
           "agents":[{"ptyId":"daemon-6b28a6c0","surfaceId":"surface-a","agentName":"Claude Code"}]},
          {"id":"pane-b","surfaceCount":2,"active":false,"surfacePtyIds":["daemon-11111111","daemon-8bab41b8"],
           "agents":[{"ptyId":"daemon-8bab41b8","surfaceId":"surface-b2","agentName":"Claude Code"}]}]}
        """;

    /// <summary>Transport finto: risponde con il JSON configurato per metodo e registra ogni chiamata in ordine.</summary>
    private sealed class FakeWmux
    {
        public Dictionary<string, string?> Responses { get; } = new()
        {
            ["workspace.list"] = Workspaces,
            ["workspace.focus"] = "true",
            ["pane.list"] = Panes,
            ["pane.focus"] = "true",
            ["surface.focus"] = "true",
        };

        public List<(string Method, string Parameters)> Calls { get; } = [];

        public Task<JsonElement?> Send(string method, JsonObject parameters, CancellationToken cancellationToken)
        {
            Calls.Add((method, parameters.ToJsonString()));
            return Task.FromResult(Responses.TryGetValue(method, out var json) && json is not null
                ? JsonDocument.Parse(json).RootElement.Clone()
                : (JsonElement?)null);
        }
    }

    [Fact]
    public async Task Focuses_the_workspace_then_the_pane_then_the_agent_tab_that_host_the_pty()
    {
        var wmux = new FakeWmux();

        Assert.True(await new WmuxClient(wmux.Send).FocusPtyAsync("daemon-8bab41b8"));

        Assert.Equal(
            [
                ("workspace.list", "{}"),
                ("workspace.focus", """{"id":"ws-main"}"""),
                ("pane.list", """{"workspaceId":"ws-main"}"""),
                ("pane.focus", """{"id":"pane-b"}"""),
                ("surface.focus", """{"id":"surface-b2"}"""),
            ],
            wmux.Calls);
    }

    [Fact]
    public async Task A_pty_that_no_workspace_lists_is_not_found_and_nothing_is_focused()
    {
        var wmux = new FakeWmux();

        Assert.False(await new WmuxClient(wmux.Send).FocusPtyAsync("daemon-deadbeef"));

        Assert.Equal(["workspace.list"], wmux.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task Wmux_not_running_means_not_found()
    {
        var wmux = new FakeWmux();
        wmux.Responses["workspace.list"] = null;

        Assert.False(await new WmuxClient(wmux.Send).FocusPtyAsync("daemon-8bab41b8"));
    }

    [Fact]
    public async Task A_pane_without_the_agent_entry_is_focused_without_touching_its_tabs()
    {
        var wmux = new FakeWmux();
        wmux.Responses["pane.list"] = """{"panes":[{"id":"pane-b","surfacePtyIds":["daemon-8bab41b8"],"agents":[]}]}""";

        Assert.True(await new WmuxClient(wmux.Send).FocusPtyAsync("daemon-8bab41b8"));

        Assert.Equal(["workspace.list", "workspace.focus", "pane.list", "pane.focus"], wmux.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task A_refused_pane_focus_is_a_failure()
    {
        var wmux = new FakeWmux();
        wmux.Responses["pane.focus"] = null;

        Assert.False(await new WmuxClient(wmux.Send).FocusPtyAsync("daemon-8bab41b8"));
    }

    [Fact]
    public async Task A_refused_tab_focus_still_leaves_the_pane_focused()
    {
        var wmux = new FakeWmux();
        wmux.Responses["surface.focus"] = null;

        Assert.True(await new WmuxClient(wmux.Send).FocusPtyAsync("daemon-8bab41b8"));
    }

    [Fact]
    public async Task A_pty_missing_from_the_pane_list_is_a_failure()
    {
        var wmux = new FakeWmux();
        wmux.Responses["pane.list"] = """{"panes":[]}""";

        Assert.False(await new WmuxClient(wmux.Send).FocusPtyAsync("daemon-8bab41b8"));
        Assert.DoesNotContain(wmux.Calls, c => c.Method == "pane.focus");
    }

    [Theory]
    [InlineData("""{"unexpected":true}""")]
    [InlineData("""[{"id":42,"ptyIds":"daemon-8bab41b8"}]""")]
    [InlineData("""[null,"x",{"ptyIds":["daemon-8bab41b8"]}]""")]
    public async Task Unexpected_workspace_shapes_are_not_found_instead_of_throwing(string json)
    {
        var wmux = new FakeWmux();
        wmux.Responses["workspace.list"] = json;

        Assert.False(await new WmuxClient(wmux.Send).FocusPtyAsync("daemon-8bab41b8"));
    }
}
