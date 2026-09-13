using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class CodexSessionResolverTests
{
    private const string Meta = """{"timestamp":"2026-09-12T19:20:46.540Z","type":"session_meta","payload":{"session_id":"01a0970c-b9e6-7c90-b787-f95e9166e315","id":"01a09710-a719-7cf2-9e08-1d41fe3b1980","cwd":"C:\\Users\\demo\\Progetti\\Vivisol Azure","originator":"codex-tui"}}""";

    [Fact]
    public void Resolves_cwd_by_file_name_id()
    {
        using var dir = new TempDir();
        dir.File(@".codex\sessions\2026\09\12\rollout-2026-09-12T21-20-46-01a09710-a719-7cf2-9e08-1d41fe3b1980.jsonl", Meta + "\n{\"type\":\"other\"}\n");
        var resolver = new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions"));
        Assert.Equal(@"C:\Users\demo\Progetti\Vivisol Azure", resolver.ResolveCwd("01a09710-a719-7cf2-9e08-1d41fe3b1980"));
    }

    [Fact]
    public void Falls_back_to_session_meta_session_id_and_caches()
    {
        using var dir = new TempDir();
        var file = dir.File(@".codex\sessions\2026\09\12\rollout-x.jsonl", Meta + "\n");
        var resolver = new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions"));
        Assert.Equal(@"C:\Users\demo\Progetti\Vivisol Azure", resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315"));
        File.Delete(file);
        Assert.Equal(@"C:\Users\demo\Progetti\Vivisol Azure", resolver.ResolveCwd("01a0970c-b9e6-7c90-b787-f95e9166e315"));
    }

    [Fact]
    public void Returns_null_when_unknown_or_dir_missing()
    {
        using var dir = new TempDir();
        Assert.Null(new CodexSessionResolver(Path.Combine(dir.Path, "missing")).ResolveCwd("abc"));
        dir.File(@".codex\sessions\2026\09\12\rollout-y.jsonl", "not json\n");
        Assert.Null(new CodexSessionResolver(Path.Combine(dir.Path, ".codex", "sessions")).ResolveCwd("abc"));
    }
}
