using System.Security.Cryptography;
using System.Text;
using AIUsageMonitor.Core.Updates;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class ExecutableSwapTests
{
    private const string ExeName = "AIUsageMonitor.exe";

    [Fact]
    public void Replace_puts_the_verified_copy_in_place_and_keeps_the_old_exe_as_backup()
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old version", "new version");

        var result = ExecutableSwap.Replace(target, staged, Sha256("new version"), Size("new version"));

        Assert.Equal(Path.GetFullPath(target), result.TargetPath);
        Assert.Equal("new version", File.ReadAllText(target));
        Assert.Equal("old version", File.ReadAllText(result.BackupPath));
        Assert.Equal(Path.GetDirectoryName(target), Path.GetDirectoryName(result.BackupPath));
        Assert.Matches(@"\AAIUsageMonitor\.exe\.old-[0-9a-f]{32}\z", Path.GetFileName(result.BackupPath));
        Assert.Equal(new[] { ExeName, Path.GetFileName(result.BackupPath) }.Order(), InstallFiles(target).Order());
        // Il file scaricato resta al chiamante: la sostituzione lavora su una copia.
        Assert.Equal("new version", File.ReadAllText(staged));
    }

    [Fact]
    public void Replace_accepts_an_uppercase_expected_hash()
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old", "new");

        ExecutableSwap.Replace(target, staged, Sha256("new").ToUpperInvariant(), Size("new"));

        Assert.Equal("new", File.ReadAllText(target));
    }

    public static TheoryData<string, long> IntegrityMismatches() => new()
    {
        { Sha256("new version"), Size("new version") + 1 },
        { Sha256("something else"), Size("new version") },
        { "not-a-hash", Size("new version") },
        { Sha256("new version")[..63], Size("new version") },
        { Sha256("new version"), -1 }
    };

    [Theory]
    [MemberData(nameof(IntegrityMismatches))]
    public void Replace_rejects_a_copy_that_does_not_match_and_leaves_the_target_untouched(string sha256, long size)
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old version", "new version");

        var error = Assert.Throws<UpdateException>(() => ExecutableSwap.Replace(target, staged, sha256, size));

        Assert.Equal("UPDATES_INTEGRITY", error.Code);
        Assert.Equal(UpdateMessages.IntegrityChangedLocally, error.Message);
        Assert.Equal("old version", File.ReadAllText(target));
        Assert.Equal([ExeName], InstallFiles(target));
    }

    [Fact]
    public void Replace_rejects_a_missing_staged_file()
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old version", "new version");
        File.Delete(staged);

        var error = Assert.Throws<UpdateException>(() => ExecutableSwap.Replace(target, staged, Sha256("new version"), Size("new version")));

        Assert.Equal("UPDATES_INTEGRITY", error.Code);
        Assert.Equal([ExeName], InstallFiles(target));
    }

    [Fact]
    public void Replace_fails_cleanly_when_the_target_cannot_be_moved()
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old version", "new version");
        File.Delete(target);

        var missingTarget = Assert.Throws<UpdateException>(() => ExecutableSwap.Replace(target, staged, Sha256("new version"), Size("new version")));
        var missingFolder = Assert.Throws<UpdateException>(() =>
            ExecutableSwap.Replace(Path.Combine(dir.Path, "nowhere", ExeName), staged, Sha256("new version"), Size("new version")));

        Assert.Equal("UPDATES_INSTALL", missingTarget.Code);
        Assert.Equal(UpdateMessages.InstallReplaceFailed, missingTarget.Message);
        Assert.Empty(InstallFiles(target));
        Assert.Equal("UPDATES_INSTALL", missingFolder.Code);
    }

    [Fact]
    public void Rollback_restores_the_backup_and_removes_the_new_version()
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old version", "new version");
        var result = ExecutableSwap.Replace(target, staged, Sha256("new version"), Size("new version"));

        result.Rollback();

        Assert.Equal("old version", File.ReadAllText(target));
        Assert.Equal([ExeName], InstallFiles(target));
    }

    [Fact]
    public void Rollback_restores_the_backup_even_if_the_new_version_is_already_gone()
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old version", "new version");
        var result = ExecutableSwap.Replace(target, staged, Sha256("new version"), Size("new version"));
        File.Delete(target);

        result.Rollback();

        Assert.Equal("old version", File.ReadAllText(target));
        Assert.Equal([ExeName], InstallFiles(target));
    }

    [Fact]
    public void Rollback_without_a_backup_throws_and_keeps_the_current_exe()
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old version", "new version");
        var result = ExecutableSwap.Replace(target, staged, Sha256("new version"), Size("new version"));
        File.Delete(result.BackupPath);

        var error = Assert.Throws<UpdateException>(result.Rollback);

        Assert.Equal("UPDATES_INSTALL", error.Code);
        Assert.Equal(UpdateMessages.InstallRollbackFailed, error.Message);
        Assert.Equal("new version", File.ReadAllText(target));
    }

    [Fact]
    public void Cleanup_removes_only_the_exact_leftover_names_next_to_the_target()
    {
        using var dir = new TempDir();
        var install = dir.Sub("install");
        var target = Path.Combine(install, ExeName);
        File.WriteAllText(target, "current");
        var guid = Guid.NewGuid().ToString("N");
        var leftovers = new[] { $"{ExeName}.old-{guid}", $"{ExeName}.new-{Guid.NewGuid():N}" };
        var kept = new[]
        {
            ExeName,
            // Un altro guid: su NTFS lo stesso nome in maiuscolo sarebbe lo stesso file.
            $"{ExeName}.old-{Guid.NewGuid().ToString("N").ToUpperInvariant()}",
            $"{ExeName}.old-{guid[..31]}",
            $"{ExeName}.old-{guid}0",
            $"{ExeName}.old-{guid[..31]}g",
            $"{ExeName}.bak-{guid}",
            $"{ExeName}.old-{guid}.tmp",
            $"Other.exe.old-{guid}",
            $"x{ExeName}.old-{guid}",
            "settings.json"
        };
        foreach (var name in leftovers.Concat(kept.Skip(1))) File.WriteAllText(Path.Combine(install, name), "x");
        Directory.CreateDirectory(Path.Combine(install, $"{ExeName}.new-{Guid.NewGuid():N}"));

        var removed = ExecutableSwap.CleanupLeftovers(target);

        Assert.Equal(2, removed);
        Assert.Equal(kept.Order(), InstallFiles(target).Order());
        Assert.Single(Directory.GetDirectories(install));
        Assert.Equal(0, ExecutableSwap.CleanupLeftovers(target));
    }

    [Fact]
    public void Cleanup_of_a_missing_folder_removes_nothing()
    {
        using var dir = new TempDir();

        Assert.Equal(0, ExecutableSwap.CleanupLeftovers(Path.Combine(dir.Path, "missing", ExeName)));
    }

    [Fact]
    public void Cleanup_after_a_replace_removes_the_backup()
    {
        using var dir = new TempDir();
        var (target, staged) = Setup(dir, "old version", "new version");
        var result = ExecutableSwap.Replace(target, staged, Sha256("new version"), Size("new version"));

        Assert.Equal(1, ExecutableSwap.CleanupLeftovers(target));

        Assert.False(File.Exists(result.BackupPath));
        Assert.Equal("new version", File.ReadAllText(target));
    }

    [Fact]
    public void Writable_probe_leaves_nothing_behind_and_fails_on_missing_or_invalid_folders()
    {
        using var dir = new TempDir();
        var file = dir.File("file.txt", "x");

        Assert.True(ExecutableSwap.IsDirectoryWritable(dir.Path));
        Assert.Equal(["file.txt"], Directory.GetFileSystemEntries(dir.Path).Select(Path.GetFileName));
        Assert.False(ExecutableSwap.IsDirectoryWritable(Path.Combine(dir.Path, "missing")));
        Assert.False(ExecutableSwap.IsDirectoryWritable(file));
        Assert.False(ExecutableSwap.IsDirectoryWritable(""));
    }

    private static (string Target, string Staged) Setup(TempDir dir, string current, string next)
    {
        var target = Path.Combine(dir.Sub("install"), ExeName);
        File.WriteAllText(target, current);
        var staged = Path.Combine(dir.Sub("updates"), $"AIUsageMonitor-1.2.3-{Guid.NewGuid()}.exe");
        File.WriteAllText(staged, next);
        return (target, staged);
    }

    private static string[] InstallFiles(string target) =>
        Directory.GetFiles(Path.GetDirectoryName(target)!).Select(path => Path.GetFileName(path)).ToArray();

    private static string Sha256(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private static long Size(string content) => Encoding.UTF8.GetByteCount(content);
}
