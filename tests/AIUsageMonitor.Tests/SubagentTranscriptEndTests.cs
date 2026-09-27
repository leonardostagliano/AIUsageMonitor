using System.Text.Json.Nodes;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Tests.Helpers;
using static AIUsageMonitor.Tests.Helpers.TranscriptLines;

namespace AIUsageMonitor.Tests;

/// <summary>
/// The end of a subagent transcript tells an agent that died without its SubagentStop: interrupted by the user,
/// stopped by an API error (the session limit), or stopped from the task list (its meta.json).
/// </summary>
public class SubagentTranscriptEndTests
{
    private const string Interrupted = "[Request interrupted by user]";
    private const string InterruptedForToolUse = "[Request interrupted by user for tool use]";

    /// <summary>The assistant line Claude Code writes when the API refuses to go on (the text is invented).</summary>
    private static string ApiError(int plusSeconds, bool isError = true)
    {
        var line = JsonNode.Parse(Text("m9", "Limite di sessione raggiunto, riprova tra poco", plusSeconds))!.AsObject();
        line["isApiErrorMessage"] = isError;
        return line.ToJsonString();
    }

    /// <summary>What an agent does before it ends: a command and its result.</summary>
    private static readonly string[] Work =
    [
        Prompt("Controlla i test del modulo", 0),
        ToolUse("m1", "t1", "Bash", new { command = "dotnet test" }, 1),
        Result("t1", 2)
    ];

    private sealed class Agent : IDisposable
    {
        private readonly TempDir _dir = new();

        /// <summary>Writes <c>agent-a1.jsonl</c> under a workflow folder of the session, as Claude Code lays it out.</summary>
        public string Transcript(params string[] lines) => _dir.File("proj/s1/subagents/workflows/wf_1/agent-a1.jsonl", Jsonl(lines));

        public string Meta(string json) => _dir.File("proj/s1/subagents/workflows/wf_1/agent-a1.meta.json", json);

        public string Directory => _dir.Path;

        public void Dispose() => _dir.Dispose();
    }

    [Theory]
    [InlineData(Interrupted, false)]
    [InlineData(Interrupted, true)]
    [InlineData(InterruptedForToolUse, false)]
    [InlineData(InterruptedForToolUse, true)]
    public void An_agent_interrupted_by_the_user_is_terminated(string text, bool asBlock)
    {
        using var agent = new Agent();
        var path = agent.Transcript([.. Work, asBlock ? PromptBlocks(text, 3) : Prompt(text, 3)]);

        Assert.True(SubagentTranscriptEnd.IsTerminated(path));
    }

    [Fact]
    public void An_agent_stopped_by_an_API_error_is_terminated()
    {
        using var agent = new Agent();

        Assert.True(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, ApiError(3)])));
    }

    [Fact]
    public void An_agent_that_ended_normally_or_is_still_at_work_is_not_terminated()
    {
        using var agent = new Agent();

        // Waiting for the model after a tool result, a final answer, a tool still running.
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript(Work)));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Text("m2", "Tutti i test passano.", 3)])));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, ToolUse("m2", "t2", "Bash", new { command = "npm test" }, 3)])));
        // Interrupted, then resumed: only the last line of the conversation counts.
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Prompt(Interrupted, 3), Prompt("Continua pure", 4)])));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, ApiError(3), Text("m3", "Riprendo.", 4)])));
        // The marker must open the text, and the error flag must be true.
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Prompt("Ignora " + Interrupted, 3)])));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, ApiError(3, isError: false)])));
    }

    [Fact]
    public void Attachment_and_system_lines_after_the_last_turn_do_not_count()
    {
        using var agent = new Agent();

        Assert.True(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Prompt(Interrupted, 3), Other("attachment", 40, 4), Other("system", 0, 5)])));
        Assert.True(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, ApiError(3), Other("attachment", 0, 4)])));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Other("attachment", 40, 3)])));
    }

    [Fact]
    public void A_missing_or_unreadable_transcript_is_not_terminated()
    {
        using var agent = new Agent();
        Assert.False(SubagentTranscriptEnd.IsTerminated(null));
        Assert.False(SubagentTranscriptEnd.IsTerminated(" "));
        Assert.False(SubagentTranscriptEnd.IsTerminated(Path.Combine(agent.Directory, "agent-missing.jsonl")));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Directory));

        var path = agent.Transcript([.. Work, Prompt(Interrupted, 3)]);
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(SubagentTranscriptEnd.IsTerminated(path));
        Assert.True(SubagentTranscriptEnd.IsTerminated(path));
    }

    [Fact]
    public void Corrupt_lines_are_skipped()
    {
        using var agent = new Agent();

        // A line cut while Claude Code writes it, a torn one, JSON that is not an object.
        Assert.True(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Prompt(Interrupted, 3), """{"type":"attachment","attach""", "{ torn", "[1,2]", "42"])));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript("{ torn", "not json at all")));
        // A user line without content, or with content of an unexpected kind, is not an interruption.
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, """{"type":"user","message":{"role":"user"}}"""])));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, """{"type":"user","message":{"role":"user","content":7}}"""])));
    }

    /// <summary>
    /// An escaped lone surrogate ("\ud83d", half an emoji) is valid JSON, but System.Text.Json throws when it has to
    /// decode it: the line is skipped like a corrupt one, instead of the exception aborting the pump's periodic pass for
    /// every later session. A meta.json holding one (the stop flag is never decoded as text) breaks nothing either.
    /// </summary>
    [Fact]
    public void A_line_or_a_meta_with_an_escaped_lone_surrogate_is_skipped()
    {
        using var agent = new Agent();
        const string content = """{"type":"user","message":{"role":"user","content":"\ud83d a meta"}}""";
        const string block = """{"type":"user","message":{"role":"user","content":[{"type":"text","text":"\ud83d"}]}}""";
        const string name = """{"type":"assistant","\ud83d":1,"message":{"role":"assistant","content":[]}}""";

        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, content])));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, block])));
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, name])));
        Assert.True(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, ApiError(3), content])));

        agent.Meta("""{"agentType":"general-purpose","\ud83d":"x","description":"\ud83d","stoppedByUser":true}""");
        var path = agent.Transcript([.. Work, Text("m2", "Fatto.", 3)]);
        var error = Record.Exception(() => SubagentTranscriptEnd.IsTerminated(path));
        Assert.Null(error);
    }

    [Fact]
    public void Only_the_last_64_KB_are_read()
    {
        using var agent = new Agent();
        var padding = Enumerable.Range(0, 80).Select(i => Other("attachment", 1000, 10 + i)).ToArray();

        // The interruption is further back than the window: nothing in it says the agent is over.
        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Prompt(Interrupted, 3), .. padding])));
        // A long history before the end is no obstacle, and the line cut by the start of the window is dropped whole.
        Assert.True(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, .. padding, Prompt(Interrupted, 100)])));
    }

    [Fact]
    public void An_agent_the_user_stopped_is_terminated_whatever_its_transcript_says()
    {
        using var agent = new Agent();
        var path = agent.Transcript([.. Work, Text("m2", "Mi fermo qui.", 3)]);
        Assert.False(SubagentTranscriptEnd.IsTerminated(path));

        agent.Meta("""{"agentType":"general-purpose","description":"Ricontrolla il modulo","spawnDepth":1,"stoppedByUser":true}""");
        Assert.True(SubagentTranscriptEnd.IsTerminated(path));
    }

    [Theory]
    [InlineData("""{"agentType":"workflow-subagent","description":"write:B","workflowPhase":"Write","spawnDepth":1}""")]
    [InlineData("""{"agentType":"general-purpose","stoppedByUser":false}""")]
    [InlineData("""{"agentType":"general-purpose","stoppedByUser":"true"}""")]
    [InlineData("""[{"stoppedByUser":true}]""")]
    [InlineData("""{ "agentType": "general-purpose", "stoppedBy""")]
    [InlineData("")]
    public void Without_a_stop_in_its_meta_the_transcript_decides(string meta)
    {
        using var agent = new Agent();
        agent.Meta(meta);

        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Text("m2", "Fatto.", 3)])));
        Assert.True(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Prompt(Interrupted, 3)])));
    }

    [Fact]
    public void Without_a_meta_file_the_transcript_decides()
    {
        using var agent = new Agent();

        Assert.False(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, Text("m2", "Fatto.", 3)])));
        Assert.True(SubagentTranscriptEnd.IsTerminated(agent.Transcript([.. Work, ApiError(3)])));
    }
}
