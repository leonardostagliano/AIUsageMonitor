using System.Text.RegularExpressions;

namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Origine fissata in build di questa applicazione. I download arrivano solo da questo repository: niente di
/// configurabile a runtime puo' dirottare l'eseguibile scaricato.
/// </summary>
public static partial class UpdateSource
{
    public const string Repository = "leonardostagliano/AIUsageMonitor";
    public const string RepositoryUrl = "https://github.com/" + Repository;
    public const string ApiRoot = "https://api.github.com/repos/" + Repository;
    public const string PackageName = "AIUsageMonitor";
    public const string ChecksumManifestName = "SHA256SUMS.txt";
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const long MaxChecksumManifestBytes = 1024 * 1024;

    /// <summary>
    /// Nome esatto dell'asset emesso da <c>scripts/windows-release.mjs</c>:
    /// <c>AIUsageMonitor-&lt;versione&gt;-win-x64.exe</c> (framework-dependent) e
    /// <c>AIUsageMonitor-&lt;versione&gt;-win-x64-selfcontained.exe</c>.
    /// </summary>
    public static string AssetName(string version, PackageVariant variant) => variant switch
    {
        PackageVariant.SelfContained => $"{PackageName}-{version}-win-x64-selfcontained.exe",
        _ => $"{PackageName}-{version}-win-x64.exe"
    };

    /// <summary>SemVer stabile <c>MAJOR.MINOR.PATCH</c>, con o senza <c>v</c>; null per pre-release, metadata o zeri iniziali.</summary>
    public static string? StableVersion(string? value)
    {
        if (value is null) return null;
        var match = StableVersionPattern().Match(value);
        if (!match.Success) return null;
        var core = match.Groups[1].Value;
        return core.Split('.').All(part => int.TryParse(part, out _)) ? core : null;
    }

    /// <summary>Confronto semantico di due versioni stabili; lancia se una delle due non lo e'.</summary>
    public static int CompareVersions(string left, string right)
    {
        var a = StableVersion(left) ?? throw new ArgumentException("Versione SemVer stabile non valida.", nameof(left));
        var b = StableVersion(right) ?? throw new ArgumentException("Versione SemVer stabile non valida.", nameof(right));
        var ap = a.Split('.').Select(int.Parse).ToArray();
        var bp = b.Split('.').Select(int.Parse).ToArray();
        for (var i = 0; i < 3; i++)
        {
            var diff = ap[i].CompareTo(bp[i]);
            if (diff != 0) return diff;
        }
        return 0;
    }

    /// <summary>
    /// Versione dell'app da <c>AssemblyInformationalVersion</c>: toglie il metadata di build che l'SDK aggiunge
    /// (<c>1.2.3+&lt;commit&gt;</c>). Restituisce la stringa ripulita anche se non e' una versione stabile.
    /// </summary>
    public static string NormalizeVersion(string? informationalVersion)
    {
        var value = (informationalVersion ?? "").Trim();
        var plus = value.IndexOf('+');
        return plus >= 0 ? value[..plus] : value;
    }

    // [0-9] e non \d (che in .NET accetta ogni cifra Unicode), \z e non $ (che accetta un "\n" finale).
    [GeneratedRegex(@"\Av?((?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*))\z", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();
}
