using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.Tests;

public class UpdateSourceTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("0.0.0", "0.0.0")]
    [InlineData("v10.20.30", "10.20.30")]
    [InlineData("2147483647.0.0", "2147483647.0.0")]
    public void StableVersion_accepts_a_stable_semver_with_or_without_v(string value, string expected) =>
        Assert.Equal(expected, UpdateSource.StableVersion(value));

    [Theory]
    [InlineData("1.2.3-rc.1")]
    [InlineData("1.2.3+build")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v")]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("V1.2.3")]
    [InlineData("vv1.2.3")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3 ")]
    [InlineData("1.2.3\n")]
    [InlineData("-1.2.3")]
    [InlineData("1.-2.3")]
    [InlineData("\u0661.\u0662.\u0663")] // cifre arabo-indiche: \d le accetterebbe, [0-9] no
    [InlineData("2147483648.0.0")] // fuori dall'intervallo di int
    [InlineData(null)]
    public void StableVersion_rejects_prereleases_metadata_padding_and_garbage(string? value) =>
        Assert.Null(UpdateSource.StableVersion(value));

    [Fact]
    public void CompareVersions_orders_major_then_minor_then_patch()
    {
        Assert.True(UpdateSource.CompareVersions("1.0.0", "2.0.0") < 0);
        Assert.True(UpdateSource.CompareVersions("2.1.0", "2.0.9") > 0);
        Assert.True(UpdateSource.CompareVersions("0.1.10", "0.1.9") > 0);
        Assert.True(UpdateSource.CompareVersions("1.10.0", "1.9.99") > 0);
        Assert.Equal(0, UpdateSource.CompareVersions("v1.2.3", "1.2.3"));
    }

    [Theory]
    [InlineData("1.2.3-rc.1", "1.2.3")]
    [InlineData("1.2.3", "nightly")]
    [InlineData("", "1.0.0")]
    public void CompareVersions_throws_when_either_side_is_not_stable(string left, string right) =>
        Assert.Throws<ArgumentException>(() => UpdateSource.CompareVersions(left, right));

    [Fact]
    public void AssetName_matches_the_names_published_by_the_release_workflow()
    {
        Assert.Equal("AIUsageMonitor-1.4.0-win-x64.exe", UpdateSource.AssetName("1.4.0", PackageVariant.FrameworkDependent));
        Assert.Equal("AIUsageMonitor-1.4.0-win-x64-selfcontained.exe", UpdateSource.AssetName("1.4.0", PackageVariant.SelfContained));
    }

    [Fact]
    public void The_origin_is_fixed_at_build_time()
    {
        Assert.Equal("leonardostagliano/AIUsageMonitor", UpdateSource.Repository);
        Assert.Equal("https://github.com/leonardostagliano/AIUsageMonitor", UpdateSource.RepositoryUrl);
        Assert.Equal("https://api.github.com/repos/leonardostagliano/AIUsageMonitor", UpdateSource.ApiRoot);
        Assert.Equal("SHA256SUMS.txt", UpdateSource.ChecksumManifestName);
        Assert.Equal(512L * 1024 * 1024, UpdateSource.MaxPackageBytes);
        Assert.Equal(1024L * 1024, UpdateSource.MaxChecksumManifestBytes);
        // L'API e il download delle release del repository sono indirizzi permessi dalla policy del trasporto.
        Assert.True(UpdateUrlPolicy.IsPermitted(new Uri(UpdateSource.ApiRoot + "/releases"), asset: false));
    }

    [Theory]
    [InlineData("1.2.3+4f2a9c1b", "1.2.3")]
    [InlineData("1.2.3+4f2a9c1b.dirty+x", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("  1.2.3  ", "1.2.3")]
    [InlineData("1.2.3-dev+abc", "1.2.3-dev")]
    [InlineData("1.0.0-local", "1.0.0-local")]
    [InlineData("+abc", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void NormalizeVersion_drops_the_build_metadata_added_by_the_sdk(string? informational, string expected) =>
        Assert.Equal(expected, UpdateSource.NormalizeVersion(informational));

    [Fact]
    public void A_normalized_release_version_is_stable()
    {
        Assert.Equal("1.2.3", UpdateSource.StableVersion(UpdateSource.NormalizeVersion("1.2.3+0123456789abcdef")));
        Assert.Null(UpdateSource.StableVersion(UpdateSource.NormalizeVersion("1.0.0-dev+abc")));
    }
}
