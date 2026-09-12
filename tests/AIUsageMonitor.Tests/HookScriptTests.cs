using AIUsageMonitor.Core.Hooks;

namespace AIUsageMonitor.Tests;

public class HookScriptTests
{
    [Fact]
    public void Embedded_script_is_the_source_file()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "AIUsageMonitor.Core", "Hooks", "hook.cjs"));
        Assert.Equal(source, HookScript.Content);
        Assert.Contains("module.exports = { buildLine }", HookScript.Content);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIUsageMonitor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
