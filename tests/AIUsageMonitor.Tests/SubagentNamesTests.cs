using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

/// <summary>
/// The name a subagent was started with: read from the agent's own files (the meta.json of a Claude agent, the
/// rollout of a Codex child), set once on the tracked agent and kept by every later event, shown by the notch row
/// before the agent type and the short id.
/// </summary>
public class SubagentNamesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    private static HookEvent Ev(string evt, int plusSeconds = 0, string? agentId = null, string? agentType = "workflow-subagent",
        IReadOnlyList<BackgroundTask>? tasks = null) =>
        new(T0.AddSeconds(plusSeconds), AgentKind.Claude, evt, "s1", @"C:\p\demo", null, null, null, agentId, agentType,
            BackgroundTasks: tasks);

    /// <summary>A Claude agent transcript under subagents/, with its meta.json next to it when <paramref name="meta"/> is given.</summary>
    private static string AgentTranscript(TempDir dir, string agentId, string? meta)
    {
        var transcript = dir.File($"projects/proj/s1/subagents/workflows/wf_01/agent-{agentId}.jsonl",
            """{"type":"user","message":{"role":"user","content":"Scrivi i test"}}""" + "\n");
        if (meta is not null) dir.File($"projects/proj/s1/subagents/workflows/wf_01/agent-{agentId}.meta.json", meta);
        return transcript;
    }

    [Fact]
    public void A_claude_agent_is_named_after_the_description_of_its_meta_json()
    {
        using var dir = new TempDir();
        var workflow = AgentTranscript(dir, "a1",
            """{"agentType":"workflow-subagent","description":"write:B (tasks 3,6)","workflowPhase":"Write","spawnDepth":1,"requestShape":"foreground","model":"opus"}""");
        var background = AgentTranscript(dir, "a2",
            """{"agentType":"general-purpose","description":"  Re-review Task 6\n  after fixes ","toolUseId":"toolu_01","requestShape":"background","stoppedByUser":true}""");

        Assert.True(SubagentNames.TryReadClaude(workflow, out var name));
        Assert.Equal("write:B (tasks 3,6)", name);
        Assert.True(SubagentNames.TryReadClaude(background, out var collapsed));
        Assert.Equal("Re-review Task 6 after fixes", collapsed);
    }

    [Fact]
    public void A_meta_json_without_a_description_settles_the_agent_as_nameless()
    {
        using var dir = new TempDir();
        var none = AgentTranscript(dir, "a1", """{"agentType":"general-purpose","model":"opus"}""");
        var blank = AgentTranscript(dir, "a2", """{"agentType":"general-purpose","description":"   "}""");
        var number = AgentTranscript(dir, "a3", """{"agentType":"general-purpose","description":42}""");

        Assert.True(SubagentNames.TryReadClaude(none, out var noName));
        Assert.Null(noName);
        Assert.True(SubagentNames.TryReadClaude(blank, out var blankName));
        Assert.Null(blankName);
        Assert.True(SubagentNames.TryReadClaude(number, out var numberName));
        Assert.Null(numberName);
    }

    /// <summary>
    /// An escaped lone surrogate ("\ud83d") in the description, or in a property name the lookup compares, cannot be
    /// decoded: the file was read and will not change, so the agent is settled without a name instead of the exception
    /// aborting the pump's periodic pass at every run.
    /// </summary>
    [Fact]
    public void A_meta_json_with_an_escaped_lone_surrogate_settles_the_agent_as_nameless()
    {
        using var dir = new TempDir();
        var description = AgentTranscript(dir, "a1", """{"agentType":"general-purpose","description":"Rivedi \ud83d il login"}""");
        var property = AgentTranscript(dir, "a2", """{"agentType":"general-purpose","\ud83d":1,"description":"write:A"}""");

        Assert.True(SubagentNames.TryReadClaude(description, out var name));
        Assert.Null(name);
        Assert.Null(Record.Exception(() => SubagentNames.TryReadClaude(property, out _)));
    }

    [Fact]
    public void A_missing_corrupt_or_locked_meta_json_says_nothing_yet()
    {
        using var dir = new TempDir();
        var missing = AgentTranscript(dir, "a1", null);
        var corrupt = AgentTranscript(dir, "a2", "{\"agentType\":\"general-purpose\",\"descri");
        var locked = AgentTranscript(dir, "a3", """{"description":"write:A"}""");

        Assert.False(SubagentNames.TryReadClaude(null, out _));
        Assert.False(SubagentNames.TryReadClaude(missing, out var none));
        Assert.Null(none);
        Assert.False(SubagentNames.TryReadClaude(corrupt, out _));
        using (new FileStream(Path.ChangeExtension(locked, ".meta.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.False(SubagentNames.TryReadClaude(locked, out _));
        Assert.True(SubagentNames.TryReadClaude(locked, out var name));
        Assert.Equal("write:A", name);
    }

    [Theory]
    [InlineData("Harvey", "/root/login_audit", "Harvey · login_audit")]
    [InlineData("Harvey", "/root/audit/login_audit/", "Harvey · login_audit")]
    [InlineData("Harvey", null, "Harvey")]
    [InlineData("Harvey", "/", "Harvey")]
    [InlineData(null, "/root/login_audit", "login_audit")]
    [InlineData("  ", "root\\login_audit", "login_audit")]
    [InlineData(null, null, null)]
    [InlineData("", "", null)]
    public void A_codex_child_is_named_after_its_nickname_and_the_last_segment_of_its_path(string? nickname, string? path, string? expected)
    {
        Assert.Equal(expected, SubagentNames.FromCodex(nickname, path));
    }

    [Fact]
    public void A_name_is_cut_at_60_characters_without_splitting_a_surrogate_pair()
    {
        var sixty = new string('a', 60);
        Assert.Equal(sixty, SubagentNames.Shorten(sixty));

        var cut = SubagentNames.Shorten(new string('a', 70));
        Assert.Equal(new string('a', 59) + "…", cut);
        Assert.Equal(SubagentNames.MaxLength, cut!.Length);

        // An emoji (two UTF-16 units) is kept whole when it fits before the "…", dropped whole when the cut would split it.
        Assert.Equal(new string('b', 57) + "😀…", SubagentNames.Shorten(new string('b', 57) + "😀" + new string('c', 10)));
        Assert.Equal(new string('b', 58) + "…", SubagentNames.Shorten(new string('b', 58) + "😀" + new string('c', 10)));

        Assert.Null(SubagentNames.Shorten(" \n\t "));
        Assert.Equal("write: A", SubagentNames.Shorten("write:\n\n A"));
    }

    [Fact]
    public void The_tracker_names_the_agents_once_and_raises_a_single_change()
    {
        var tracker = new SessionTracker(new FakeClock(T0));
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        tracker.Apply(Ev("SubagentStart", 2, "a2"));
        var changes = new List<SessionChange>();
        tracker.Changed += changes.Add;

        var change = tracker.UpdateSubagentNames(AgentKind.Claude, "s1",
            new Dictionary<string, string> { ["a1"] = "write:B (tasks 3,6)", ["a2"] = " ", ["ghost"] = "write:C" });

        Assert.NotNull(change);
        Assert.Same(change, Assert.Single(changes));
        Assert.Equal(SessionChangeKind.Updated, change.Kind);
        Assert.Equal(SessionPhase.Working, change.PreviousPhase);
        var s = tracker.Sessions.Single();
        Assert.Equal("write:B (tasks 3,6)", s.Subagents!.Single(a => a.AgentId == "a1").Name);
        Assert.Null(s.Subagents!.Single(a => a.AgentId == "a2").Name);
        Assert.Equal(2, s.Subagents!.Count);
        Assert.Equal(T0.AddSeconds(2), s.LastEventAt);

        // Once set, a name stays: another one for the same agent changes nothing, nor does an unknown session.
        Assert.Null(tracker.UpdateSubagentNames(AgentKind.Claude, "s1", new Dictionary<string, string> { ["a1"] = "write:Z" }));
        Assert.Null(tracker.UpdateSubagentNames(AgentKind.Codex, "s1", new Dictionary<string, string> { ["a1"] = "write:Z" }));
        Assert.Null(tracker.UpdateSubagentNames(AgentKind.Claude, "s1", new Dictionary<string, string>()));
        Assert.Single(changes);
        Assert.Equal("write:B (tasks 3,6)", tracker.Sessions.Single().Subagents!.Single(a => a.AgentId == "a1").Name);
    }

    [Fact]
    public void A_name_survives_every_later_event_of_its_agent()
    {
        var clock = new FakeClock(T0);
        var tracker = new SessionTracker(clock);
        tracker.Apply(Ev("UserPromptSubmit"));
        tracker.Apply(Ev("SubagentStart", 1, "a1"));
        tracker.Apply(Ev("SubagentStart", 2, "bg", "general-purpose"));
        tracker.UpdateSubagentNames(AgentKind.Claude, "s1", new Dictionary<string, string> { ["a1"] = "write:B", ["bg"] = "Re-review" });
        string? Name(string id) => tracker.Sessions.Single().Subagents!.Single(a => a.AgentId == id).Name;

        // A re-announce, the Stop of the turn that lists the background agent, the tokens: nothing touches the names.
        tracker.Apply(Ev("SubagentStart", 3, "a1"));
        tracker.Apply(Ev("Stop", 4, tasks: [new BackgroundTask("bg", BackgroundTask.SubagentType, "general-purpose")]));
        tracker.UpdateTokens(AgentKind.Claude, "s1", new TokenUsage(10, 5, 0, 0),
            new Dictionary<string, TokenUsage> { ["a1"] = new(3, 1, 0, 0) }, new Dictionary<string, string> { ["a1"] = "claude-opus-4-7" });
        Assert.Equal("write:B", Name("a1"));
        Assert.Equal("Re-review", Name("bg"));

        // The end of the agent, and its return for another turn (a Codex child does that).
        tracker.Apply(Ev("SubagentStop", 5, "a1"));
        Assert.Equal(SubagentPhase.Done, tracker.Sessions.Single().Subagents!.Single(a => a.AgentId == "a1").Phase);
        Assert.Equal("write:B", Name("a1"));
        tracker.Apply(Ev("SubagentStart", 6, "a1"));
        Assert.Equal("write:B", Name("a1"));

        // The timeout sweep that releases a silent agent.
        clock.Advance(TimeSpan.FromHours(1));
        tracker.SweepSubagentTimeouts(TimeSpan.FromMinutes(30));
        Assert.All(tracker.Sessions.Single().Subagents!, a => Assert.Equal(SubagentPhase.Done, a.Phase));
        Assert.Equal("write:B", Name("a1"));
        Assert.Equal("Re-review", Name("bg"));

        // Back at work, while the oldest finished agents are trimmed away around it.
        tracker.Apply(Ev("SubagentStart", 3650, "a1"));
        for (var i = 0; i < SessionTracker.MaxDoneSubagents; i++)
        {
            tracker.Apply(Ev("SubagentStart", 3700 + i, $"x{i}"));
            tracker.Apply(Ev("SubagentStop", 3700 + i, $"x{i}"));
        }
        Assert.DoesNotContain(tracker.Sessions.Single().Subagents!, a => a.AgentId == "bg");
        Assert.Equal("write:B", Name("a1"));
    }

    [Fact]
    public void The_row_shows_the_name_then_the_type_then_the_short_id()
    {
        var agent = new SubagentState("a0383bad8dfbcf7db", "workflow-subagent", SubagentPhase.Running, T0, null, null, TokenUsage.Zero);

        Assert.Equal("workflow-subagent", NotchPresentation.SubagentTitle(agent));
        Assert.Equal("workflow-subagent", NotchPresentation.SubagentTooltip(agent));

        var named = agent with { Name = "write:B (tasks 3,6)" };
        Assert.Equal("write:B (tasks 3,6)", NotchPresentation.SubagentTitle(named));
        Assert.Equal("write:B (tasks 3,6)\nworkflow-subagent", NotchPresentation.SubagentTooltip(named));

        var untyped = agent with { AgentType = null };
        Assert.Equal("a0383bad", NotchPresentation.SubagentTitle(untyped));
        Assert.Equal("a0383bad", NotchPresentation.SubagentTooltip(untyped));
        Assert.Equal("write:B", NotchPresentation.SubagentTooltip(untyped with { Name = "write:B" }));
        Assert.Equal("a1", NotchPresentation.SubagentTitle(untyped with { AgentId = "a1", AgentType = " " }));
    }
}
