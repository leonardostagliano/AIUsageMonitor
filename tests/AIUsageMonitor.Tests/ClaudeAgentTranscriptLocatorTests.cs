using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public sealed class ClaudeAgentTranscriptLocatorTests
{
    private const string SessionId = "0e587bb1-d0d1-4f31-9593-f4948aa8ee2f";

    [Fact]
    public void FindsAWorkflowAgentTranscriptUnderTheSessionDirectory()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        var agent = temp.File($"projects/proj/{SessionId}/subagents/workflows/wf_05aab4e0/agent-a962644fe6f85f0dd.jsonl", "{}\n");

        var locator = new ClaudeAgentTranscriptLocator();

        AssertSamePath(agent, locator.Locate(session, SessionId, "a962644fe6f85f0dd"));
    }

    [Fact]
    public void FindsAnAgentToolTranscriptDirectlyUnderSubagents()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        var agent = temp.File($"projects/proj/{SessionId}/subagents/agent-c9917bd2.jsonl", "{}\n");

        var locator = new ClaudeAgentTranscriptLocator();

        AssertSamePath(agent, locator.Locate(session, SessionId, "c9917bd2"));
    }

    [Fact]
    public void IgnoresTheSidecarMetadataFileOfTheAgent()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        temp.File($"projects/proj/{SessionId}/subagents/agent-c9917bd2.meta.json", "{}\n");

        var locator = new ClaudeAgentTranscriptLocator();

        Assert.Null(locator.Locate(session, SessionId, "c9917bd2"));
    }

    [Fact]
    public void ReturnsNullWhenNothingMatches()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        temp.File($"projects/proj/{SessionId}/subagents/agent-someoneelse.jsonl", "{}\n");

        var locator = new ClaudeAgentTranscriptLocator();

        Assert.Null(locator.Locate(session, SessionId, "c9917bd2"));
    }

    [Fact]
    public void ReturnsNullWithoutASessionTranscriptOrASubagentsDirectory()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        var locator = new ClaudeAgentTranscriptLocator();

        Assert.Null(locator.Locate(null, SessionId, "c9917bd2"));
        Assert.Null(locator.Locate("   ", SessionId, "c9917bd2"));
        Assert.Null(locator.Locate(session, SessionId, "  "));
        Assert.Null(locator.Locate(session, SessionId, "c9917bd2")); // no subagents directory at all
    }

    [Fact]
    public void RefusesAnIdThatIsNotAPlainFileNameToken()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        temp.File($"projects/proj/{SessionId}/subagents/agent-c9917bd2.jsonl", "{}\n");

        var locator = new ClaudeAgentTranscriptLocator();

        // A wildcard must not be handed to the search pattern (it would match any agent), and the synthetic
        // "anon:<n>" id of a subagent event without agent_id names no file.
        Assert.Null(locator.Locate(session, SessionId, "*"));
        Assert.Null(locator.Locate(session, SessionId, "c99*"));
        Assert.Null(locator.Locate(session, SessionId, "anon:1"));
        Assert.Null(locator.Locate(session, SessionId, @"..\..\c9917bd2"));
    }

    [Fact]
    public void CachesTheHitSoTheTreeIsNotWalkedAgain()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        var agent = temp.File($"projects/proj/{SessionId}/subagents/agent-c9917bd2.jsonl", "{}\n");

        var locator = new ClaudeAgentTranscriptLocator();
        AssertSamePath(agent, locator.Locate(session, SessionId, "c9917bd2"));
        Assert.Equal(1, locator.Searches);

        Directory.Delete(Path.Combine(temp.Path, "projects", "proj", SessionId), recursive: true);

        AssertSamePath(agent, locator.Locate(session, SessionId, "c9917bd2"));
        Assert.Equal(1, locator.Searches);
    }

    [Fact]
    public void ForgetDropsTheCachedPathAndReturnsIt()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        var agent = temp.File($"projects/proj/{SessionId}/subagents/agent-c9917bd2.jsonl", "{}\n");

        var locator = new ClaudeAgentTranscriptLocator();
        locator.Locate(session, SessionId, "c9917bd2");

        AssertSamePath(agent, locator.Forget("c9917bd2"));
        Assert.Null(locator.Forget("c9917bd2"));

        Directory.Delete(Path.Combine(temp.Path, "projects", "proj", SessionId), recursive: true);
        Assert.Null(locator.Locate(session, SessionId, "c9917bd2"));
    }

    [Fact]
    public void AMissIsRetriedUntilTheAgentWritesItsTranscript()
    {
        using var temp = new TempDir();
        var session = temp.File($"projects/proj/{SessionId}.jsonl", "{}\n");
        temp.Sub($"projects/proj/{SessionId}/subagents");

        var locator = new ClaudeAgentTranscriptLocator();
        Assert.Null(locator.Locate(session, SessionId, "c9917bd2"));

        var agent = temp.File($"projects/proj/{SessionId}/subagents/agent-c9917bd2.jsonl", "{}\n");

        AssertSamePath(agent, locator.Locate(session, SessionId, "c9917bd2"));
    }

    /// <summary>Same file, whatever mix of separators the two paths use.</summary>
    private static void AssertSamePath(string expected, string? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(Path.GetFullPath(expected), Path.GetFullPath(actual), ignoreCase: true);
    }
}
