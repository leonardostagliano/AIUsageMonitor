using System.Text.Json.Nodes;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class HookInstallerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);

    // Shape of a real ~/.claude/settings.json with third-party hooks that must survive untouched.
    private const string ClaudeSettings = """
        {
          "model": "opus",
          "permissions": { "allow": ["Bash(git status:*)"] },
          "hooks": {
            "PreToolUse": [
              { "matcher": "Bash", "hooks": [ { "type": "command", "command": "node \"C:/Users/demo/.claude/hooks/no-claude-in-commits.js\"" } ] }
            ],
            "SessionEnd": [
              { "hooks": [ { "type": "command", "command": "powershell -File toast.ps1 # Sessione terminata (à)" } ] }
            ],
            "SessionStart": [
              { "hooks": [ { "type": "command", "command": "node \"C:/Users/demo/.claude/hooks/kb-context.js\"" } ] },
              { "matcher": "startup", "hooks": [ { "type": "command", "command": "powershell -File herdr-agent-state.ps1 session" } ] }
            ]
          }
        }
        """;

    private const string CodexHooks = """
        {
          "hooks": {
            "SessionStart": [ { "hooks": [ { "command": "node \"C:\\Users\\demo\\.codex\\hooks\\obsidian-kb\\index.cjs\"", "timeout": 10, "type": "command" } ] } ],
            "Stop": [ { "hooks": [ { "type": "command", "command": "node \"C:\\Users\\demo\\.codex\\hooks\\obsidian-kb\\index.cjs\"", "timeout": 10 } ] } ]
          }
        }
        """;

    private static (HookInstaller Installer, AppPaths Paths) Build(TempDir dir)
    {
        var paths = new AppPaths(dir.Path, dir.Sub("lad"));
        return (new HookInstaller(paths, new FakeClock(Now)), paths);
    }

    private static int CountOurs(string file, string evt) =>
        (JsonNode.Parse(File.ReadAllText(file))!["hooks"]![evt] as JsonArray)!
            .Count(g => g!["hooks"]!.AsArray().Any(h => h!["command"]!.GetValue<string>().Contains("hook.cjs")));

    [Fact]
    public void Install_on_claude_adds_our_hooks_once_and_preserves_existing_ones()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        dir.File(@".claude\settings.json", ClaudeSettings);

        Assert.Equal(HookStatus.NotInstalled, installer.GetStatus(AgentKind.Claude).Status);
        var report = installer.Install(AgentKind.Claude);
        Assert.Equal(HookStatus.Installed, report.Status);

        var root = JsonNode.Parse(File.ReadAllText(paths.ClaudeSettingsFile))!;
        Assert.Equal("opus", root["model"]!.GetValue<string>());
        Assert.Equal("Bash(git status:*)", root["permissions"]!["allow"]![0]!.GetValue<string>());
        var hooks = root["hooks"]!;
        Assert.Contains("no-claude-in-commits.js", hooks["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>());
        Assert.Equal(3, hooks["SessionStart"]!.AsArray().Count);
        Assert.Contains("Sessione terminata (à)", File.ReadAllText(paths.ClaudeSettingsFile)); // no \u00e0 escaping

        foreach (var reg in HookInstaller.Registrations[AgentKind.Claude])
            Assert.Equal(1, CountOurs(paths.ClaudeSettingsFile, reg.Event));
        var postToolUse = hooks["PostToolUse"]!.AsArray().Single();
        Assert.Equal("AskUserQuestion", postToolUse!["matcher"]!.GetValue<string>());
        var ourCommand = postToolUse["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal($"node \"{paths.HookScriptFile}\" claude", ourCommand);
        Assert.Equal(5, postToolUse["hooks"]![0]!["timeout"]!.GetValue<int>());

        Assert.True(File.Exists(paths.HookScriptFile));
        Assert.Equal(HookScript.Content, File.ReadAllText(paths.HookScriptFile));
        Assert.Single(Directory.GetFiles(paths.BackupsDir, "settings.json.*.bak"));

        // Idempotent: second install changes nothing and adds no backup.
        installer.Install(AgentKind.Claude);
        foreach (var reg in HookInstaller.Registrations[AgentKind.Claude])
            Assert.Equal(1, CountOurs(paths.ClaudeSettingsFile, reg.Event));
        Assert.Single(Directory.GetFiles(paths.BackupsDir, "settings.json.*.bak"));
    }

    [Fact]
    public void Install_on_codex_and_remove_leaves_only_third_party_hooks()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        dir.File(@".codex\hooks.json", CodexHooks);

        Assert.Equal(HookStatus.Installed, installer.Install(AgentKind.Codex).Status);
        Assert.Equal(4, HookInstaller.Registrations[AgentKind.Codex].Count);
        Assert.Equal(1, CountOurs(paths.CodexHooksFile, "UserPromptSubmit"));

        var removed = installer.Remove(AgentKind.Codex);
        Assert.Equal(HookStatus.NotInstalled, removed.Status);
        var hooks = JsonNode.Parse(File.ReadAllText(paths.CodexHooksFile))!["hooks"]!.AsObject();
        Assert.Equal(new[] { "SessionStart", "Stop" }, hooks.Select(kv => kv.Key).OrderBy(k => k).ToArray());
        Assert.Single(hooks["SessionStart"]!.AsArray());
        Assert.Contains("obsidian-kb", File.ReadAllText(paths.CodexHooksFile));
        Assert.Equal(2, Directory.GetFiles(paths.BackupsDir, "hooks.json.*.bak").Length);
    }

    [Fact]
    public void Install_creates_the_config_file_when_missing()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        Assert.Equal(HookStatus.ConfigMissing, installer.GetStatus(AgentKind.Codex).Status);
        Assert.Equal(HookStatus.Installed, installer.Install(AgentKind.Codex).Status);
        Assert.True(File.Exists(paths.CodexHooksFile));
        Assert.Empty(Directory.Exists(paths.BackupsDir) ? Directory.GetFiles(paths.BackupsDir) : Array.Empty<string>());
    }

    [Fact]
    public void Invalid_json_is_never_overwritten()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        dir.File(@".claude\settings.json", "{ this is not json");
        Assert.Equal(HookStatus.ConfigInvalid, installer.GetStatus(AgentKind.Claude).Status);
        Assert.Equal(HookStatus.ConfigInvalid, installer.Install(AgentKind.Claude).Status);
        Assert.Equal("{ this is not json", File.ReadAllText(paths.ClaudeSettingsFile));
        Assert.Equal(HookStatus.ConfigInvalid, installer.Remove(AgentKind.Claude).Status);
    }

    [Fact]
    public void Partial_status_when_some_events_are_missing()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        // The command embeds a quoted path, so both backslashes and quotes need JSON escaping.
        var cmd = installer.HookCommandFor(AgentKind.Claude).Replace("\\", "\\\\").Replace("\"", "\\\"");
        dir.File(@".claude\settings.json", $$$"""{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"{{{cmd}}}"}]}]}}""");
        var status = installer.GetStatus(AgentKind.Claude);
        Assert.Equal(HookStatus.Partial, status.Status);
        Assert.Contains("1/7", status.Detail);
    }

    [Fact]
    public void Codex_status_warns_when_hooks_are_disabled_in_config_toml()
    {
        using var dir = new TempDir();
        var (installer, _) = Build(dir);
        dir.File(@".codex\config.toml", "model = \"gpt-6\"\n[features]\nhooks = false\n");
        installer.Install(AgentKind.Codex);
        Assert.Contains("hooks = false", installer.GetStatus(AgentKind.Codex).Detail);
    }

    [Fact]
    public void Marker_matches_forward_and_back_slashes()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        dir.File(@".claude\settings.json", """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"node C:/x/.aiusagemonitor/hook.cjs claude"}]}]}}""");
        Assert.Equal(HookStatus.Partial, installer.GetStatus(AgentKind.Claude).Status);
        installer.Remove(AgentKind.Claude);
        Assert.Equal(HookStatus.NotInstalled, installer.GetStatus(AgentKind.Claude).Status);
    }

    [Fact]
    public void Remove_is_a_no_op_when_none_of_our_hooks_are_present()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        const string foreign = """{"hooks":{"PreToolUse":[{"matcher":"Bash","hooks":[]}],"Stop":[]}}""";
        dir.File(@".claude\settings.json", foreign);

        var report = installer.Remove(AgentKind.Claude);

        Assert.Equal(HookStatus.NotInstalled, report.Status);
        Assert.Equal(foreign, File.ReadAllText(paths.ClaudeSettingsFile));
        Assert.True(!Directory.Exists(paths.BackupsDir) || Directory.GetFiles(paths.BackupsDir).Length == 0);
    }

    [Fact]
    public void Duplicate_json_keys_are_reported_as_invalid_and_never_overwritten()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        // Hand-edited settings.json: JSON.parse (and Claude Code) keeps the last key, JsonNode throws on materialization.
        const string duplicated = """{"hooks":{"Stop":[]},"hooks":{"SessionStart":[]}}""";
        dir.File(@".claude\settings.json", duplicated);

        var status = installer.GetStatus(AgentKind.Claude);
        Assert.Equal(HookStatus.ConfigInvalid, status.Status);
        Assert.Contains(paths.ClaudeSettingsFile, status.Detail);
        Assert.Equal(HookStatus.ConfigInvalid, installer.Install(AgentKind.Claude).Status);
        Assert.Equal(HookStatus.ConfigInvalid, installer.Remove(AgentKind.Claude).Status);
        Assert.Equal(duplicated, File.ReadAllText(paths.ClaudeSettingsFile));
        Assert.True(!Directory.Exists(paths.BackupsDir) || Directory.GetFiles(paths.BackupsDir).Length == 0);
    }

    [Fact]
    public void Nested_duplicate_json_keys_are_reported_as_invalid()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        const string duplicated = """{"hooks":{"Stop":[{"hooks":[],"hooks":[]}]}}""";
        dir.File(@".codex\hooks.json", duplicated);

        Assert.Equal(HookStatus.ConfigInvalid, installer.GetStatus(AgentKind.Codex).Status);
        Assert.Equal(HookStatus.ConfigInvalid, installer.Install(AgentKind.Codex).Status);
        Assert.Equal(HookStatus.ConfigInvalid, installer.Remove(AgentKind.Codex).Status);
        Assert.Equal(duplicated, File.ReadAllText(paths.CodexHooksFile));
    }

    [Fact]
    public void Codex_status_asks_to_approve_the_hooks_when_no_trusted_hash_exists()
    {
        using var dir = new TempDir();
        var (installer, _) = Build(dir);

        var report = installer.Install(AgentKind.Codex);

        Assert.Equal(HookStatus.Installed, report.Status);
        Assert.Contains(HookInstaller.CodexTrustHint, report.Detail);
        Assert.Contains(HookInstaller.CodexTrustHint, installer.GetStatus(AgentKind.Codex).Detail);
    }

    [Fact]
    public void Codex_status_is_clean_when_every_group_of_ours_has_a_trusted_hash()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        installer.Install(AgentKind.Codex);
        // Real format written by Codex: snake_case event names, positional group/command indices.
        var state = string.Concat(HookInstaller.Registrations[AgentKind.Codex]
            .Select(r => $"[hooks.state.'{paths.CodexHooksFile}:{HookInstaller.CodexStateEventName(r.Event)}:0:0']\ntrusted_hash = \"sha256:abc\"\n"));
        dir.File(@".codex\config.toml", "model = \"gpt-6\"\n[hooks.state]\n" + state);

        var detail = installer.GetStatus(AgentKind.Codex).Detail;

        Assert.DoesNotContain(HookInstaller.CodexTrustHint, detail);
        Assert.Contains("4/4", detail);
    }

    [Fact]
    public void Codex_trust_keys_use_snake_case_event_names()
    {
        Assert.Equal("session_start", HookInstaller.CodexStateEventName("SessionStart"));
        Assert.Equal("user_prompt_submit", HookInstaller.CodexStateEventName("UserPromptSubmit"));
        Assert.Equal("stop", HookInstaller.CodexStateEventName("Stop"));
        Assert.Equal("session_end", HookInstaller.CodexStateEventName("SessionEnd"));
    }

    [Fact]
    public void Codex_status_ignores_pascal_case_trust_keys_that_codex_never_writes()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        installer.Install(AgentKind.Codex);
        var state = string.Concat(HookInstaller.Registrations[AgentKind.Codex]
            .Select(r => $"[hooks.state.'{paths.CodexHooksFile}:{r.Event}:0:0']\ntrusted_hash = \"sha256:abc\"\n"));
        dir.File(@".codex\config.toml", "model = \"gpt-6\"\n" + state);

        Assert.Contains(HookInstaller.CodexTrustHint, installer.GetStatus(AgentKind.Codex).Detail);
    }

    [Fact]
    public void Codex_status_matches_real_config_toml_layout_with_second_group_and_command()
    {
        using var dir = new TempDir();
        var (installer, paths) = Build(dir);
        // A user group already approved (two commands) comes first; ours is appended as group 1 by Install.
        dir.File(@".codex\hooks.json", """{"hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"node a.cjs"},{"type":"command","command":"node b.cjs"}]}]}}""");
        installer.Install(AgentKind.Codex);
        var file = paths.CodexHooksFile;
        var toml = "model = \"gpt-6\"\n[hooks.state]\n" +
            $"[hooks.state.'{file}:session_start:0:0']\ntrusted_hash = \"sha256:aaa\"\n" +
            $"[hooks.state.'{file}:session_start:0:1']\ntrusted_hash = \"sha256:bbb\"\n" +
            $"[hooks.state.'{file}:session_start:1:0']\ntrusted_hash = \"sha256:ccc\"\n" +
            $"[hooks.state.'{file}:user_prompt_submit:0:0']\ntrusted_hash = \"sha256:ddd\"\n" +
            $"[hooks.state.'{file}:stop:0:0']\ntrusted_hash = \"sha256:eee\"\n" +
            $"[hooks.state.'{file}:session_end:0:0']\ntrusted_hash = \"sha256:fff\"\n";
        dir.File(@".codex\config.toml", toml);

        var detail = installer.GetStatus(AgentKind.Codex).Detail;

        Assert.DoesNotContain(HookInstaller.CodexTrustHint, detail);
        Assert.Contains("4/4", detail);
    }

    [Fact]
    public void Claude_status_never_mentions_the_codex_trust_hint()
    {
        using var dir = new TempDir();
        var (installer, _) = Build(dir);
        dir.File(@".claude\settings.json", ClaudeSettings);
        Assert.DoesNotContain(HookInstaller.CodexTrustHint, installer.Install(AgentKind.Claude).Detail);
    }
}
