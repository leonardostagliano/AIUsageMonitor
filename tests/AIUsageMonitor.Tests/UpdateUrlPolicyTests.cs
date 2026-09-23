using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.Tests;

public class UpdateUrlPolicyTests
{
    private const string Api = "https://api.github.com/repos/leonardostagliano/AIUsageMonitor";
    private const string Download = "https://github.com/leonardostagliano/AIUsageMonitor/releases/download";

    [Theory]
    [InlineData(Api + "/releases")]
    [InlineData(Api + "/releases/")]
    [InlineData(Api + "/releases?per_page=100&page=1")]
    [InlineData(Api + "/releases/123")]
    [InlineData(Api + "/releases/assets/456")]
    [InlineData("https://API.GitHub.COM/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://api.github.com:443/repos/leonardostagliano/AIUsageMonitor/releases/7")]
    [InlineData(Api + "/releases/x/../assets/9")]
    public void Api_release_endpoints_of_the_repository_are_permitted_for_metadata_and_assets(string url)
    {
        Assert.True(UpdateUrlPolicy.IsPermitted(new Uri(url), asset: false));
        Assert.True(UpdateUrlPolicy.IsPermitted(new Uri(url), asset: true));
    }

    [Theory]
    [InlineData(Download + "/v1.2.3/AIUsageMonitor-1.2.3-win-x64.exe")]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/abc?sp=r&sig=SIGNED%2F")]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/2?X-Amz-Signature=abc")]
    [InlineData("https://github-releases.githubusercontent.com/1/2")]
    [InlineData("https://OBJECTS.githubusercontent.com/x")]
    public void Release_downloads_and_cdn_hosts_are_permitted_only_for_assets(string url)
    {
        Assert.True(UpdateUrlPolicy.IsPermitted(new Uri(url), asset: true));
        Assert.False(UpdateUrlPolicy.IsPermitted(new Uri(url), asset: false));
    }

    [Theory]
    // Schema, credenziali nell'URL, porte, frammenti.
    [InlineData("http://api.github.com/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("ftp://api.github.com/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://user:pass@api.github.com/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://token@objects.githubusercontent.com/x")]
    [InlineData("https://api.github.com:8443/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://objects.githubusercontent.com:444/x")]
    [InlineData(Api + "/releases#fragment")]
    [InlineData(Api + "/releases#")]
    [InlineData("https://objects.githubusercontent.com/x#y")]
    // Host simili ma diversi.
    [InlineData("https://api.github.com.evil/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://api.github.com./repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://evilapi.github.com/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://uploads.github.com/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://\u0430pi.github.com/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://140.82.112.6/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://[::1]/repos/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://evil.objects.githubusercontent.com/x")]
    [InlineData("https://objects.githubusercontent.com.evil.com/x")]
    [InlineData("https://raw.githubusercontent.com/leonardostagliano/AIUsageMonitor/main/x.exe")]
    [InlineData("https://www.github.com/leonardostagliano/AIUsageMonitor/releases/download/v1.0.0/x.exe")]
    // Percorsi fuori dalle release del repository, anche dopo la normalizzazione di Uri.
    [InlineData("https://api.github.com/repos/leonardostagliano/AIUsageMonitor")]
    [InlineData("https://api.github.com/repos/leonardostagliano/AIUsageMonitor/releasesX")]
    [InlineData("https://api.github.com/repos/leonardostagliano/AIUsageMonitor/git/refs")]
    [InlineData("https://api.github.com/repos/leonardostagliano/Other/releases")]
    [InlineData("https://api.github.com/repos/someone/AIUsageMonitor/releases")]
    [InlineData("https://api.github.com/user")]
    [InlineData(Api + "/releases/../../Other/releases")]
    [InlineData(Api + "/releases/%2e%2e/%2E%2E/Other/releases")]
    [InlineData(Api + "/releases/.%2e/.%2e/Other/releases")]
    [InlineData(Api + "/releases/..%2f..%2fOther%2freleases")]
    [InlineData(Api + "/releases%2f123")]
    [InlineData(Api + "/releases/%61ssets/1%09")]
    [InlineData("https://api.github.com/repos/LEONARDOSTAGLIANO/aiusagemonitor/releases")]
    [InlineData("https://api.github.com/REPOS/leonardostagliano/AIUsageMonitor/releases")]
    [InlineData("https://github.com/leonardostagliano/AIUsageMonitor/archive/refs/heads/main.zip")]
    [InlineData("https://github.com/leonardostagliano/AIUsageMonitor/releases/tag/v1.0.0")]
    [InlineData("https://github.com/someone/AIUsageMonitor/releases/download/v1.0.0/x.exe")]
    [InlineData(Download + "/../../../someone/x/releases/download/v1/x.exe")]
    [InlineData(Download + "/v1.0.0/%2e%2e%2f%2e%2e%2fx.exe")]
    public void Anything_else_is_rejected_even_for_assets(string url)
    {
        Assert.False(UpdateUrlPolicy.IsPermitted(new Uri(url), asset: true));
        Assert.False(UpdateUrlPolicy.IsPermitted(new Uri(url), asset: false));
    }

    [Fact]
    public void Relative_and_missing_urls_are_rejected()
    {
        Assert.False(UpdateUrlPolicy.IsPermitted(new Uri("/repos/leonardostagliano/AIUsageMonitor/releases", UriKind.Relative), asset: true));
        Assert.False(UpdateUrlPolicy.IsPermitted(null!, asset: true));
        // Su Linux "/percorso" diventa un Uri file:// assoluto: non e' comunque https.
        Assert.False(UpdateUrlPolicy.IsPermitted(new Uri("/repos/leonardostagliano/AIUsageMonitor/releases", UriKind.RelativeOrAbsolute), asset: true));
    }

    [Fact]
    public void Dot_segments_are_resolved_before_the_check()
    {
        // Uri risolve "..": quello che si controlla e' il percorso davvero inviato.
        var inside = new Uri(Api + "/releases/assets/../7");
        Assert.Equal("/repos/leonardostagliano/AIUsageMonitor/releases/7", inside.AbsolutePath);
        Assert.True(UpdateUrlPolicy.IsPermitted(inside, asset: false));

        var outside = new Uri(Api + "/releases/%2e%2e/%2e%2e/Other/releases");
        Assert.Equal("/repos/leonardostagliano/Other/releases", outside.AbsolutePath);
        Assert.False(UpdateUrlPolicy.IsPermitted(outside, asset: false));
    }
}
