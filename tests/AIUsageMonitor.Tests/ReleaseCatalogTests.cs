using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.Tests;

public class ReleaseCatalogTests
{
    private const PackageVariant Fd = PackageVariant.FrameworkDependent;
    private const PackageVariant Sc = PackageVariant.SelfContained;
    private static readonly string Hash = new('a', 64);

    // ---- Costruzione delle risposte finte dell'API release ------------------------------------------------------

    private static JsonObject Asset(long id, string name, long size = 1024, string state = "uploaded", string? digest = null)
    {
        var asset = new JsonObject { ["id"] = id, ["state"] = state, ["name"] = name, ["size"] = size };
        if (digest is not null) asset["digest"] = digest;
        return asset;
    }

    /// <summary>Release come la pubblica la CI: le due varianti dell'eseguibile e SHA256SUMS.txt.</summary>
    private static JsonObject Release(string version, long id = 500, bool checksums = true, bool selfContained = true, string? tag = null)
    {
        var assets = new JsonArray { Asset(id + 1, UpdateSource.AssetName(version, Fd)) };
        if (selfContained) assets.Add(Asset(id + 2, UpdateSource.AssetName(version, Sc), size: 2048));
        if (checksums) assets.Add(Asset(id + 3, UpdateSource.ChecksumManifestName, size: 200));
        return new JsonObject
        {
            ["id"] = id,
            ["draft"] = false,
            ["prerelease"] = false,
            ["tag_name"] = tag ?? $"v{version}",
            ["published_at"] = "2026-09-01T10:00:00Z",
            ["body"] = $"Release {version}",
            ["assets"] = assets
        };
    }

    private static JsonElement Element(JsonNode node) => Element(node.ToJsonString());

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement List(params JsonNode[] releases) => Element(new JsonArray(releases));

    private static JsonArray Assets(JsonObject release) => release["assets"]!.AsArray();

    private static UpdateException Throws(Action action) => Assert.ThrowsAny<UpdateException>(action);

    // ---- Candidate ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_published_stable_release_becomes_a_candidate()
    {
        var candidate = ReleaseCatalog.Candidate(Element(Release("1.2.3")), Fd);

        Assert.NotNull(candidate);
        Assert.Equal(500, candidate.ReleaseId);
        Assert.Equal(new ReleaseAsset(501, "AIUsageMonitor-1.2.3-win-x64.exe", 1024, null), candidate.Package);
        Assert.Equal(503, candidate.Checksums!.Id);
        var view = candidate.View;
        Assert.Equal("1.2.3", view.Version);
        Assert.Equal("v1.2.3", view.Tag);
        Assert.Equal("2026-09-01T10:00:00Z", view.PublishedAt);
        Assert.Equal("https://github.com/leonardostagliano/AIUsageMonitor/releases/tag/v1.2.3", view.Url);
        Assert.Equal("Release 1.2.3", view.Notes);
        Assert.Equal("AIUsageMonitor-1.2.3-win-x64.exe", view.AssetName);
        Assert.Equal(1024, view.AssetSize);
        Assert.Equal(ChecksumSource.Sha256Sums, view.Checksum);
    }

    [Fact]
    public void A_tag_without_the_v_prefix_is_accepted()
    {
        var candidate = ReleaseCatalog.Candidate(Element(Release("2.0.0", tag: "2.0.0")), Fd);

        Assert.Equal("2.0.0", candidate!.View.Version);
        Assert.Equal("2.0.0", candidate.View.Tag);
        Assert.EndsWith("/releases/tag/2.0.0", candidate.View.Url);
    }

    [Theory]
    [InlineData("draft", "true")]
    [InlineData("prerelease", "true")]
    [InlineData("draft", "\"false\"")]
    [InlineData("prerelease", "null")]
    [InlineData("draft", "0")]
    public void Drafts_prereleases_and_malformed_flags_are_skipped(string flag, string json)
    {
        var release = Release("1.2.3");
        release[flag] = JsonNode.Parse(json);

        Assert.Null(ReleaseCatalog.Candidate(Element(release), Fd));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    [InlineData("id")]
    [InlineData("tag_name")]
    [InlineData("assets")]
    public void A_missing_required_field_skips_the_release(string field)
    {
        var release = Release("1.2.3");
        release.Remove(field);

        Assert.Null(ReleaseCatalog.Candidate(Element(release), Fd));
    }

    [Theory]
    [InlineData("v1.2.3-rc.1")]
    [InlineData("1.2.3-beta")]
    [InlineData("v1.2.3+build.5")]
    [InlineData("v1.2")]
    [InlineData("v01.2.3")]
    [InlineData("V1.2.3")]
    [InlineData("nightly")]
    [InlineData("")]
    public void Only_stable_semver_tags_are_candidates(string tag)
    {
        Assert.Null(ReleaseCatalog.Candidate(Element(Release("1.2.3", tag: tag)), Fd));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-7")]
    [InlineData("\"500\"")]
    [InlineData("1.5")]
    [InlineData("null")]
    public void The_release_id_must_be_a_positive_integer(string id)
    {
        var release = Release("1.2.3");
        release["id"] = JsonNode.Parse(id);

        Assert.Null(ReleaseCatalog.Candidate(Element(release), Fd));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    [InlineData("\"x\"")]
    [InlineData("42")]
    public void Non_object_values_are_not_releases(string? json)
    {
        Assert.Null(ReleaseCatalog.Candidate(Element(json ?? "null"), Fd));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("starter")]
    [InlineData("")]
    public void Assets_that_are_not_uploaded_are_ignored(string state)
    {
        var release = Release("1.2.3");
        Assets(release)[0]!["state"] = state;

        Assert.Null(ReleaseCatalog.Candidate(Element(release), Fd));
        // L'altra variante, caricata, resta disponibile.
        Assert.NotNull(ReleaseCatalog.Candidate(Element(release), Sc));
    }

    [Fact]
    public void Each_variant_gets_its_own_executable()
    {
        var release = Element(Release("1.2.3"));

        var fd = ReleaseCatalog.Candidate(release, Fd)!;
        var sc = ReleaseCatalog.Candidate(release, Sc)!;

        Assert.Equal(501, fd.Package.Id);
        Assert.Equal("AIUsageMonitor-1.2.3-win-x64.exe", fd.View.AssetName);
        Assert.Equal(502, sc.Package.Id);
        Assert.Equal("AIUsageMonitor-1.2.3-win-x64-selfcontained.exe", sc.View.AssetName);
        Assert.Equal(2048, sc.View.AssetSize);
    }

    [Fact]
    public void A_release_without_the_running_variant_is_not_a_candidate()
    {
        var release = Element(Release("1.2.3", selfContained: false));

        Assert.NotNull(ReleaseCatalog.Candidate(release, Fd));
        Assert.Null(ReleaseCatalog.Candidate(release, Sc));
    }

    [Fact]
    public void Two_uploaded_executables_with_the_same_name_are_ambiguous()
    {
        var release = Release("1.2.3");
        Assets(release).Add(Asset(900, "AIUsageMonitor-1.2.3-win-x64.exe"));
        Assert.Null(ReleaseCatalog.Candidate(Element(release), Fd));

        // Se il duplicato non e' caricato non c'e' ambiguita'.
        Assets(release)[^1]!["state"] = "new";
        Assert.Equal(501, ReleaseCatalog.Candidate(Element(release), Fd)!.Package.Id);
    }

    [Theory]
    [InlineData("AIUsageMonitor-1.2.3-win-x64.exe.sig")]
    [InlineData("aiusagemonitor-1.2.3-win-x64.exe")]
    [InlineData("AIUsageMonitor-1.2.4-win-x64.exe")]
    [InlineData("AIUsageMonitor-v1.2.3-win-x64.exe")]
    [InlineData("AIUsageMonitor-1.2.3-win-arm64.exe")]
    [InlineData("AIUsageMonitor-1.2.3-x64.exe")]
    public void Only_the_exact_asset_name_counts(string name)
    {
        var release = Release("1.2.3", selfContained: false);
        Assets(release)[0]!["name"] = name;

        Assert.Null(ReleaseCatalog.Candidate(Element(release), Fd));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("536870913")] // MaxPackageBytes + 1
    [InlineData("\"1024\"")]
    [InlineData("1024.5")]
    public void Empty_oversized_or_malformed_executables_are_ignored(string size)
    {
        var release = Release("1.2.3");
        Assets(release)[0]!["size"] = JsonNode.Parse(size);

        Assert.Null(ReleaseCatalog.Candidate(Element(release), Fd));
    }

    [Fact]
    public void An_executable_of_exactly_the_maximum_size_is_accepted()
    {
        var release = Release("1.2.3");
        Assets(release)[0]!["size"] = UpdateSource.MaxPackageBytes;

        Assert.Equal(UpdateSource.MaxPackageBytes, ReleaseCatalog.Candidate(Element(release), Fd)!.View.AssetSize);
    }

    [Fact]
    public void A_duplicate_checksum_manifest_is_an_error()
    {
        var release = Release("1.2.3");
        Assets(release).Add(Asset(901, UpdateSource.ChecksumManifestName, size: 100));

        var error = Throws(() => ReleaseCatalog.Candidate(Element(release), Fd));

        Assert.Equal("UPDATES_CHECKSUM", error.Code);
        Assert.Equal(UpdateMessages.ChecksumManifestInvalid, error.Message);
    }

    [Fact]
    public void An_oversized_checksum_manifest_is_an_error()
    {
        var release = Release("1.2.3");
        Assets(release)[2]!["size"] = UpdateSource.MaxChecksumManifestBytes + 1;

        Assert.Equal("UPDATES_CHECKSUM", Throws(() => ReleaseCatalog.Candidate(Element(release), Fd)).Code);
    }

    [Fact]
    public void A_manifest_that_is_not_uploaded_is_ignored()
    {
        var release = Release("1.2.3");
        Assets(release).Add(Asset(901, UpdateSource.ChecksumManifestName, size: 100, state: "new"));

        Assert.Equal(503, ReleaseCatalog.Candidate(Element(release), Fd)!.Checksums!.Id);
    }

    [Fact]
    public void The_github_digest_is_read_and_lowercased()
    {
        var release = Release("1.2.3", checksums: false);
        Assets(release)[0]!["digest"] = "sha256:" + new string('A', 64);

        var candidate = ReleaseCatalog.Candidate(Element(release), Fd)!;

        Assert.Equal(Hash, candidate.Package.Digest);
        Assert.Null(candidate.Checksums);
        Assert.Equal(ChecksumSource.GitHubDigest, candidate.View.Checksum);
    }

    [Fact]
    public void The_digest_prefix_is_case_insensitive_like_in_chessadvisor()
    {
        var release = Release("1.2.3", checksums: false);
        Assets(release)[0]!["digest"] = "SHA256:" + Hash;

        Assert.Equal(Hash, ReleaseCatalog.Candidate(Element(release), Fd)!.Package.Digest);
    }

    [Theory]
    [InlineData("sha1:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public void An_invalid_digest_is_ignored(string digest)
    {
        var release = Release("1.2.3", checksums: false);
        Assets(release)[0]!["digest"] = digest;

        var candidate = ReleaseCatalog.Candidate(Element(release), Fd)!;

        Assert.Null(candidate.Package.Digest);
        Assert.Equal(ChecksumSource.Unavailable, candidate.View.Checksum);
    }

    [Fact]
    public void The_manifest_wins_over_the_digest_as_checksum_source()
    {
        var release = Release("1.2.3");
        Assets(release)[0]!["digest"] = "sha256:" + Hash;

        Assert.Equal(ChecksumSource.Sha256Sums, ReleaseCatalog.Candidate(Element(release), Fd)!.View.Checksum);
    }

    [Fact]
    public void Notes_and_dates_are_cleaned_and_truncated()
    {
        var release = Release("1.2.3");
        release["body"] = "Titolo\u0000\u0007\u001b[31m\tcon tab\r\nriga\u007f\u000b\u000cfine" + new string('x', 30_000);
        release["published_at"] = new string('9', 100);

        var view = ReleaseCatalog.Candidate(Element(release), Fd)!.View;

        Assert.StartsWith("Titolo[31m\tcon tab\r\nrigafine", view.Notes);
        Assert.Equal(24_000, view.Notes.Length);
        Assert.Equal(40, view.PublishedAt.Length);
    }

    [Fact]
    public void Truncation_never_splits_a_surrogate_pair()
    {
        var release = Release("1.2.3");
        release["body"] = new string('a', 23_999) + "\U0001F600" + "coda";

        var notes = ReleaseCatalog.Candidate(Element(release), Fd)!.View.Notes;

        Assert.Equal(23_999, notes.Length);
        Assert.False(char.IsSurrogate(notes[^1]));
    }

    [Theory]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("{\"a\":1}")]
    public void Non_string_notes_and_dates_become_empty(string json)
    {
        var release = Release("1.2.3");
        release["body"] = JsonNode.Parse(json);
        release["published_at"] = JsonNode.Parse(json);

        var view = ReleaseCatalog.Candidate(Element(release), Fd)!.View;

        Assert.Equal("", view.Notes);
        Assert.Equal("", view.PublishedAt);
    }

    [Fact]
    public void Undecodable_strings_do_not_escape_as_exceptions()
    {
        // JsonDocument accetta un surrogato isolato scritto come escape, ma GetString() lancerebbe.
        var json = Release("1.2.3").ToJsonString();
        var badBody = Element(json.Replace("Release 1.2.3", "\\ud800 note"));
        Assert.Equal("", ReleaseCatalog.Candidate(badBody, Fd)!.View.Notes);

        var badTag = Element(json.Replace("\"v1.2.3\"", "\"v1.2.3\\ud800\""));
        Assert.Null(ReleaseCatalog.Candidate(badTag, Fd));

        var badState = Element(json.Replace("\"uploaded\"", "\"uploaded\\udc00\""));
        Assert.Null(ReleaseCatalog.Candidate(badState, Fd));
    }

    // ---- Latest ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Latest_compares_semver_instead_of_trusting_the_order()
    {
        var releases = List(Release("1.9.0", id: 100), Release("1.10.0", id: 200), Release("1.2.0", id: 300));

        var latest = ReleaseCatalog.Latest(releases, Fd);

        Assert.Equal("1.10.0", latest!.View.Version);
        Assert.Equal(200, latest.ReleaseId);
    }

    [Fact]
    public void Latest_skips_drafts_prereleases_and_releases_without_the_variant()
    {
        var draft = Release("3.0.0", id: 100);
        draft["draft"] = true;
        var prerelease = Release("2.5.0", id: 200);
        prerelease["prerelease"] = true;
        var rc = Release("2.4.0", id: 300, tag: "v2.4.0-rc.1");
        var fdOnly = Release("2.3.0", id: 400, selfContained: false);
        var stable = Release("2.0.0", id: 500);

        var releases = List(draft, prerelease, rc, fdOnly, stable, JsonValue.Create(1), JsonValue.Create("x")!, new JsonArray());

        Assert.Equal("2.0.0", ReleaseCatalog.Latest(releases, Sc)!.View.Version);
        Assert.Equal("2.3.0", ReleaseCatalog.Latest(releases, Fd)!.View.Version);
    }

    [Fact]
    public void Latest_keeps_the_first_of_equal_versions()
    {
        var releases = List(Release("1.0.0", id: 100), Release("1.0.0", id: 200));

        Assert.Equal(100, ReleaseCatalog.Latest(releases, Fd)!.ReleaseId);
    }

    [Fact]
    public void Latest_is_null_when_nothing_is_usable()
    {
        Assert.Null(ReleaseCatalog.Latest(List(), Fd));
        var draft = Release("1.0.0");
        draft["draft"] = true;
        Assert.Null(ReleaseCatalog.Latest(List(draft), Fd));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("\"releases\"")]
    public void Latest_requires_an_array(string json)
    {
        var error = Throws(() => ReleaseCatalog.Latest(Element(json), Fd));

        Assert.Equal("UPDATES_RESPONSE", error.Code);
        Assert.Equal(UpdateMessages.ReleaseListInvalid, error.Message);
        Assert.Equal("UPDATES_RESPONSE", Throws(() => ReleaseCatalog.Latest(default, Fd)).Code);
    }

    [Fact]
    public void Latest_refuses_a_page_larger_than_requested()
    {
        var hundred = Enumerable.Range(1, 100).Select(i => (JsonNode)Release($"1.0.{i}", id: i * 10)).ToArray();
        Assert.Equal("1.0.100", ReleaseCatalog.Latest(List(hundred), Fd)!.View.Version);

        var more = hundred.Select(r => r.DeepClone()).Append(Release("9.9.9", id: 5000)).ToArray();
        Assert.Equal("UPDATES_RESPONSE", Throws(() => ReleaseCatalog.Latest(List(more), Fd)).Code);
    }

    [Fact]
    public void Latest_reports_an_invalid_manifest_in_any_release()
    {
        var broken = Release("0.1.0", id: 100);
        Assets(broken).Add(Asset(999, UpdateSource.ChecksumManifestName, size: 10));

        Assert.Equal("UPDATES_CHECKSUM", Throws(() => ReleaseCatalog.Latest(List(Release("1.0.0"), broken), Fd)).Code);
    }

    // ---- Exact ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Exact_accepts_the_same_release_read_again()
    {
        var selected = ReleaseCatalog.Candidate(Element(Release("1.2.3")), Fd)!;
        var again = Release("1.2.3");
        again["body"] = "note aggiornate";

        var fresh = ReleaseCatalog.Exact(Element(again), selected, Fd);

        Assert.Equal(selected.Package, fresh.Package);
        Assert.Equal("note aggiornate", fresh.View.Notes);
    }

    [Fact]
    public void Exact_returns_the_fresh_digest_for_the_service_to_compare()
    {
        var selected = ReleaseCatalog.Candidate(Element(Release("1.2.3")), Fd)!;
        var again = Release("1.2.3");
        Assets(again)[0]!["digest"] = "sha256:" + new string('b', 64);

        Assert.Equal(new string('b', 64), ReleaseCatalog.Exact(Element(again), selected, Fd).Package.Digest);
    }

    public static TheoryData<string> Changes => new() { "asset-id", "asset-size", "release-id", "version", "draft", "removed", "variant-gone" };

    [Theory]
    [MemberData(nameof(Changes))]
    public void Exact_detects_a_release_that_changed_after_the_check(string change)
    {
        var selected = ReleaseCatalog.Candidate(Element(Release("1.2.3")), Fd)!;
        var fresh = change switch
        {
            "release-id" => Release("1.2.3", id: 777),
            "version" => Release("1.2.4"),
            _ => Release("1.2.3")
        };
        switch (change)
        {
            case "asset-id": Assets(fresh)[0]!["id"] = 9999; break;
            case "asset-size": Assets(fresh)[0]!["size"] = 1025; break;
            case "draft": fresh["draft"] = true; break;
            case "removed": Assets(fresh).RemoveAt(0); break;
            case "variant-gone": Assets(fresh)[0]!["state"] = "new"; break;
        }
        if (change == "release-id")
        {
            // Stessi asset, release diversa.
            Assets(fresh)[0]!["id"] = 501;
        }

        var error = Throws(() => ReleaseCatalog.Exact(Element(fresh), selected, Fd));

        Assert.Equal("UPDATES_CHANGED", error.Code);
        Assert.Equal(UpdateMessages.ReleaseChanged, error.Message);
    }

    // ---- ChecksumFor ----------------------------------------------------------------------------------------------

    private const string Name = "AIUsageMonitor-1.2.3-win-x64.exe";

    [Theory]
    [InlineData("{0}  {1}\n")]
    [InlineData("{0}  {1}")]
    [InlineData("{0} *{1}\n")]
    [InlineData("{0}\t{1}\n")]
    [InlineData("  {0}  {1}  \r\n")]
    [InlineData("{2}  other.exe\r\n{0}  {1}\r\n{2}  SHA256SUMS.txt\r\n")]
    [InlineData("\uFEFF{0}  {1}\n{2}  AIUsageMonitor-1.2.3-win-x64-selfcontained.exe\n")]
    [InlineData("# commento\n\n{0}  {1}\n")]
    public void ChecksumFor_reads_the_sha256sum_format(string format)
    {
        var expected = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var manifest = string.Format(format, expected.ToUpperInvariant(), Name, new string('f', 64));

        Assert.Equal(expected, ReleaseCatalog.ChecksumFor(manifest, Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{0}  other.exe\n")]
    [InlineData("{0}  {1}.sig\n")]
    [InlineData("{0}  ./{1}\n")]
    [InlineData("{0}  {1}\n{0}  {1}\n")]
    [InlineData("{0}  {1}\n{2}  {1}\r\n")]
    [InlineData("{0}  *{1}\n{0}  {1}\n")]
    [InlineData("{3}  {1}\n")]
    [InlineData("{0}{1}\n")]
    [InlineData("{0}0  {1}\n")]
    public void ChecksumFor_requires_exactly_one_matching_line(string format)
    {
        var manifest = string.Format(format, Hash, Name, new string('b', 64), new string('c', 63));

        var error = Throws(() => ReleaseCatalog.ChecksumFor(manifest, Name));

        Assert.Equal("UPDATES_CHECKSUM", error.Code);
        Assert.Equal(UpdateMessages.ChecksumNotUnique, error.Message);
    }

    [Fact]
    public void ChecksumFor_distinguishes_the_two_variants()
    {
        var sc = "AIUsageMonitor-1.2.3-win-x64-selfcontained.exe";
        var manifest = $"{Hash}  {Name}\n{new string('b', 64)}  {sc}\n";

        Assert.Equal(Hash, ReleaseCatalog.ChecksumFor(manifest, Name));
        Assert.Equal(new string('b', 64), ReleaseCatalog.ChecksumFor(manifest, sc));
    }

    [Fact]
    public void ChecksumFor_treats_a_missing_manifest_as_invalid()
    {
        Assert.Equal("UPDATES_CHECKSUM", Throws(() => ReleaseCatalog.ChecksumFor(null!, Name)).Code);
    }
}
