using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Lettura difensiva delle risposte dell'API release di GitHub: solo release pubblicate e stabili, con esattamente un
/// eseguibile della variante richiesta e al piu' un manifest SHA256SUMS. Nessun campo della risposta finisce nella UI
/// senza essere ripulito e troncato.
/// </summary>
public static partial class ReleaseCatalog
{
    public const int MaxReleasesPerPage = 100;
    private const int MaxAssetsPerRelease = 1000;
    private const int MaxNotesLength = 24_000;

    /// <summary>La release piu' recente (confronto SemVer, non l'etichetta "Latest") tra quelle di una pagina.</summary>
    public static ReleaseCandidate? Latest(JsonElement releases, PackageVariant variant)
    {
        if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > MaxReleasesPerPage)
            throw new UpdateException("UPDATES_RESPONSE", UpdateMessages.ReleaseListInvalid);
        ReleaseCandidate? latest = null;
        foreach (var release in releases.EnumerateArray())
        {
            var candidate = Candidate(release, variant);
            if (candidate is null) continue;
            if (latest is null || UpdateSource.CompareVersions(candidate.View.Version, latest.View.Version) > 0) latest = candidate;
        }
        return latest;
    }

    /// <summary>
    /// Candidata da una singola release, o null se non e' pubblicata, e' una pre-release, non ha un tag stabile o non ha
    /// esattamente un eseguibile della variante. Un manifest dei checksum duplicato o troppo grande e' un errore.
    /// </summary>
    public static ReleaseCandidate? Candidate(JsonElement release, PackageVariant variant)
    {
        if (release.ValueKind != JsonValueKind.Object) return null;
        if (!IsFalse(release, "draft") || !IsFalse(release, "prerelease")) return null;
        var releaseId = PositiveId(release, "id");
        if (releaseId is null) return null;
        var tag = String(release, "tag_name");
        var version = UpdateSource.StableVersion(tag);
        if (version is null || tag is null) return null;
        if (!release.TryGetProperty("assets", out var assetsElement) || assetsElement.ValueKind != JsonValueKind.Array
            || assetsElement.GetArrayLength() > MaxAssetsPerRelease)
            return null;

        var assets = assetsElement.EnumerateArray().Select(Asset).Where(a => a is not null).Select(a => a!).ToList();
        var packageName = UpdateSource.AssetName(version, variant);
        var packages = assets.Where(a => a.Name == packageName && a.Size <= UpdateSource.MaxPackageBytes).ToList();
        if (packages.Count != 1) return null;
        var manifests = assets.Where(a => a.Name == UpdateSource.ChecksumManifestName).ToList();
        if (manifests.Count > 1 || manifests.Any(a => a.Size > UpdateSource.MaxChecksumManifestBytes))
            throw new UpdateException("UPDATES_CHECKSUM", UpdateMessages.ChecksumManifestInvalid);

        var package = packages[0];
        var checksums = manifests.FirstOrDefault();
        var view = new UpdateRelease(
            Version: version,
            Tag: tag,
            PublishedAt: Text(String(release, "published_at"), 40),
            Url: $"{UpdateSource.RepositoryUrl}/releases/tag/{Uri.EscapeDataString(tag)}",
            Notes: Text(String(release, "body"), MaxNotesLength),
            AssetName: package.Name,
            AssetSize: package.Size,
            Checksum: checksums is not null ? ChecksumSource.Sha256Sums : package.Digest is not null ? ChecksumSource.GitHubDigest : ChecksumSource.Unavailable);
        return new ReleaseCandidate(releaseId.Value, view, package, checksums);
    }

    /// <summary>
    /// Rilegge la release scelta al controllo prima di scaricare: id, versione, asset e dimensione devono essere gli
    /// stessi, altrimenti un asset e' stato sostituito nel frattempo (UPDATES_CHANGED).
    /// </summary>
    public static ReleaseCandidate Exact(JsonElement release, ReleaseCandidate selected, PackageVariant variant)
    {
        var fresh = Candidate(release, variant);
        if (fresh is null
            || fresh.ReleaseId != selected.ReleaseId
            || fresh.View.Version != selected.View.Version
            || fresh.Package.Id != selected.Package.Id
            || fresh.Package.Size != selected.Package.Size)
            throw new UpdateException("UPDATES_CHANGED", UpdateMessages.ReleaseChanged);
        return fresh;
    }

    /// <summary>
    /// SHA-256 (esadecimale minuscolo) di <paramref name="assetName"/> nel formato di <c>sha256sum</c>
    /// (<c>&lt;hash&gt;  &lt;nome&gt;</c>, con <c>*</c> opzionale per il modo binario). Deve esserci esattamente una riga.
    /// </summary>
    public static string ChecksumFor(string manifest, string assetName)
    {
        var matching = manifest.Split('\n')
            .Select(line => ChecksumLine().Match(line.Trim()))
            .Where(m => m.Success && m.Groups[2].Value == assetName)
            .ToList();
        if (matching.Count != 1) throw new UpdateException("UPDATES_CHECKSUM", UpdateMessages.ChecksumNotUnique);
        return matching[0].Groups[1].Value.ToLowerInvariant();
    }

    private static ReleaseAsset? Asset(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var id = PositiveId(value, "id");
        if (id is null || String(value, "state") != "uploaded") return null;
        var name = String(value, "name");
        if (name is null) return null;
        if (!value.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var bytes) || bytes <= 0)
            return null;
        var digest = String(value, "digest");
        var hex = digest is not null && DigestPattern().IsMatch(digest) ? digest[7..].ToLowerInvariant() : null;
        return new ReleaseAsset(id.Value, name, bytes, hex);
    }

    private static bool IsFalse(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.False;

    private static long? PositiveId(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id > 0 ? id : null;

    private static string? String(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Toglie i caratteri di controllo (tranne tab, a capo e ritorno carrello) e tronca.</summary>
    private static string Text(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var clean = ControlChars().Replace(value, "");
        return clean.Length > max ? clean[..max] : clean;
    }

    [GeneratedRegex(@"\Asha256:[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"\A([a-fA-F0-9]{64})\s+\*?(.+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ChecksumLine();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlChars();
}
