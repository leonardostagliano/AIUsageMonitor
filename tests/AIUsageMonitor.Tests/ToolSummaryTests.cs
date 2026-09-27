using System.Text.Json;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;

namespace AIUsageMonitor.Tests;

public class ToolSummaryTests
{
    private const string Cwd = @"C:\Users\demo\Progetti\Demo";

    private static JsonElement Input(object value) => JsonSerializer.SerializeToElement(value);

    private static AttentionDetail Describe(string tool, object input, string? cwd = Cwd) =>
        ToolSummary.Describe(tool, Input(input), cwd);

    [Fact]
    public void A_question_shows_its_first_question_and_how_many_more_follow()
    {
        var one = Describe("AskUserQuestion", new { questions = new[] { new { question = "Quale database usiamo?", header = "DB" } } });
        Assert.Equal(new AttentionDetail(AttentionKind.Question, null, "Quale database usiamo?"), one);

        var three = Describe("AskUserQuestion", new
        {
            questions = new[]
            {
                new { question = "Quale  database\nusiamo?", header = "DB" },
                new { question = "Serve la cache?", header = "Cache" },
                new { question = "Chi fa la review?", header = "Review" }
            }
        });
        Assert.Equal(AttentionKind.Question, three.Kind);
        Assert.Null(three.Tool);
        Assert.Equal("Quale database usiamo? (+2)", three.Summary);
    }

    [Fact]
    public void A_long_first_question_is_cut_so_that_the_count_still_fits()
    {
        var question = new string('d', 200);
        var detail = Describe("AskUserQuestion", new { questions = new[] { new { question }, new { question = "altra" } } });

        Assert.Equal(ToolSummary.MaxLength, detail.Summary!.Length);
        Assert.EndsWith("… (+1)", detail.Summary);
    }

    [Fact]
    public void A_question_without_readable_questions_has_no_summary()
    {
        Assert.Equal(new AttentionDetail(AttentionKind.Question), Describe("AskUserQuestion", new { questions = Array.Empty<object>() }));
        Assert.Equal(new AttentionDetail(AttentionKind.Question), Describe("AskUserQuestion", new { __unparsedToolInput = new { raw = "{" } }));
    }

    [Fact]
    public void A_plan_shows_its_first_line_with_text_without_the_heading_marks()
    {
        var detail = Describe("ExitPlanMode", new { plan = "\n\n###\n## Piano:  migrare i test\n\n1. Spostare i fixture" });

        Assert.Equal(new AttentionDetail(AttentionKind.Plan, null, "Piano: migrare i test"), detail);
        Assert.Equal(new AttentionDetail(AttentionKind.Plan), Describe("ExitPlanMode", new { planFilePath = "x.md" }));
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    public void A_command_shows_its_first_line_with_the_whitespace_collapsed(string tool)
    {
        var detail = Describe(tool, new { command = "\n  dotnet   test\ttests/Demo.Tests \\\n  --filter Fast", description = "Run the tests" });

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, tool, @"dotnet test tests/Demo.Tests \"), detail);
    }

    [Fact]
    public void A_long_command_is_cut_at_the_maximum_length_with_an_ellipsis()
    {
        var detail = Describe("Bash", new { command = "echo " + new string('a', 300) });

        Assert.Equal(ToolSummary.MaxLength, detail.Summary!.Length);
        Assert.StartsWith("echo aaa", detail.Summary);
        Assert.EndsWith("…", detail.Summary);
    }

    [Theory]
    [InlineData("Edit")]
    [InlineData("Write")]
    [InlineData("MultiEdit")]
    [InlineData("Read")]
    public void A_file_under_the_working_directory_is_shown_relative_to_it(string tool)
    {
        var detail = Describe(tool, new { file_path = @"C:\Users\demo\Progetti\Demo\src\App\Program.cs" });

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, tool, @"src\App\Program.cs"), detail);
    }

    [Theory]
    [InlineData(@"c:\users\DEMO\progetti\demo\README.md", Cwd, "README.md")]                       // Windows paths ignore case
    [InlineData("C:/Users/demo/Progetti/Demo/docs/a.md", "C:/Users/demo/Progetti/Demo/", "docs/a.md")] // forward slashes, trailing separator
    [InlineData(@"C:\Users\demo\Progetti\Demo2\x.cs", Cwd, @"C:\Users\demo\Progetti\Demo2\x.cs")]   // a sibling folder is not a child
    [InlineData(@"D:\altro\x.cs", Cwd, @"D:\altro\x.cs")]
    [InlineData(@"D:\altro\x.cs", null, @"D:\altro\x.cs")]
    public void A_file_outside_the_working_directory_keeps_its_full_path(string path, string? cwd, string expected) =>
        Assert.Equal(expected, Describe("Edit", new { file_path = path }, cwd).Summary);

    [Fact]
    public void A_notebook_edit_names_its_notebook()
    {
        var detail = Describe("NotebookEdit", new { notebook_path = @"C:\Users\demo\Progetti\Demo\nb\analisi.ipynb", new_source = "x" });

        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "NotebookEdit", @"nb\analisi.ipynb"), detail);
    }

    [Fact]
    public void Web_tools_show_the_url_without_its_query_or_the_search_text()
    {
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "WebFetch", "https://example.com/docs/page"),
            Describe("WebFetch", new { url = "https://example.com/docs/page?token=abc&x=1#top", prompt = "riassumi" }));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "WebFetch", "https://example.com/a"),
            Describe("WebFetch", new { url = "https://example.com/a#sezione" }));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "WebSearch", "prezzi api 2026"),
            Describe("WebSearch", new { query = "prezzi   api 2026" }));
    }

    [Theory]
    [InlineData("Agent")]
    [InlineData("Task")]
    public void An_agent_shows_its_description(string tool) =>
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, tool, "Esplora il modulo di login"),
            Describe(tool, new { description = "Esplora il modulo di login", prompt = "lungo prompt", subagent_type = "Explore" }));

    [Theory]
    [InlineData("mcp__claude-in-chrome__navigate", "navigate", "claude-in-chrome")]
    [InlineData("mcp__plugin_design_figma__get_design_context", "get_design_context", "plugin_design_figma")]
    [InlineData("mcp__demo_server__run_query", "run_query", "demo_server")]
    public void An_mcp_tool_shows_the_tool_and_its_server(string name, string tool, string server) =>
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, tool, server), Describe(name, new { tabId = 1 }));

    [Theory]
    [InlineData("Skill")]
    [InlineData("mcp__broken")]
    [InlineData("mcp__server__")]
    public void Any_other_tool_says_it_wants_to_be_used(string name) =>
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, name, $"Vuole usare {name}"), Describe(name, new { x = 1 }));

    [Fact]
    public void Missing_or_malformed_input_never_throws_and_leaves_no_summary()
    {
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash"), ToolSummary.Describe("Bash", default, Cwd));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Bash"), Describe("Bash", new { command = 42 }));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "Edit"), Describe("Edit", new[] { "not", "an", "object" }));
        Assert.Equal(new AttentionDetail(AttentionKind.Question), ToolSummary.Describe("AskUserQuestion", Input("text"), null));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission, "WebFetch"), Describe("WebFetch", new { url = "   " }));
        Assert.Equal(new AttentionDetail(AttentionKind.Permission), ToolSummary.Describe("  ", default, null));
    }

    [Theory]
    [InlineData("  due   parole \n", "due parole")]
    [InlineData("riga uno\r\n\triga due", "riga uno riga due")]
    [InlineData("", "")]
    [InlineData(" \n\t ", "")]
    public void Shorten_collapses_and_trims_the_whitespace(string text, string expected) =>
        Assert.Equal(expected, ToolSummary.Shorten(text));

    [Fact]
    public void Shorten_keeps_a_text_of_exactly_the_maximum_length_and_cuts_a_longer_one()
    {
        var exact = new string('a', ToolSummary.MaxLength);
        Assert.Equal(exact, ToolSummary.Shorten(exact));

        var cut = ToolSummary.Shorten(exact + "b");
        Assert.Equal(ToolSummary.MaxLength, cut.Length);
        Assert.Equal(new string('a', ToolSummary.MaxLength - 1) + "…", cut);
    }

    [Fact]
    public void Shorten_never_splits_a_surrogate_pair()
    {
        // 118 letters, then an emoji (two chars) that would straddle the cut at 119.
        var text = new string('a', ToolSummary.MaxLength - 2) + "😀" + "tail";

        var cut = ToolSummary.Shorten(text);

        Assert.Equal(new string('a', ToolSummary.MaxLength - 2) + "…", cut);
        Assert.DoesNotContain(cut, c => char.IsSurrogate(c));
    }
}
