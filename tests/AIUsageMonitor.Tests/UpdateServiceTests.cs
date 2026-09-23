using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Core.Updates;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private const string Token = "gho_fake_session_token";
    private const string Account = "octocat";
    private const string LinkedToken = "gho_linked_session_token";
    private const string LinkedAccount = "hubot";
    private const string ReleasesPath = "/releases?per_page=100&page=1";
    private const int PackageSize = 1024;
    private static readonly byte[] PackageBytes = [.. "MZ"u8, .. Enumerable.Repeat((byte)0x41, PackageSize - 2)];
    private static readonly string PackageSha256 = Sha256(PackageBytes);

    private readonly TempDir _dir = new();
    private readonly ManualTimeProvider _time = new();
    private readonly FakeTransport _transport = new();
    private readonly FakeCredentials _credentials = new();
    private readonly FakeLogin _login = new();
    private readonly FakeInstaller _installer = new();
    private readonly List<UpdateStatus> _statuses = [];
    private readonly List<string> _logs = [];
    private readonly List<UpdateService> _services = [];
    private string _currentVersion = "1.0.0";
    private bool _autoCheck = true;
    private int _quits;
    private int _returns;

    private string Downloads => Path.Combine(_dir.Path, "updates");

    public void Dispose()
    {
        foreach (var service in _services) service.Dispose();
        _dir.Dispose();
    }

    // ---- Stato iniziale e controllo automatico ----

    [Fact]
    public void Initial_status_reflects_options_session_and_installation()
    {
        var service = MakeService();

        var status = service.Status;

        Assert.Equal(0, status.Revision);
        Assert.Equal(UpdatePhase.Idle, status.Phase);
        Assert.Equal("1.0.0", status.CurrentVersion);
        Assert.Equal(InstallationKind.Supported, status.Installation);
        Assert.Equal(PackageVariant.FrameworkDependent, status.Variant);
        Assert.Equal(UpdateSource.Repository, status.Repository);
        Assert.Equal(UpdateSource.RepositoryUrl, status.RepositoryUrl);
        Assert.True(status.AutoCheck);
        Assert.Null(status.CheckedAt);
        Assert.Equal(UpdateAuthSource.GitHubApp, status.AuthSource);
        Assert.Equal(Account, status.GitHubAccount);
        Assert.Null(status.Release);
        Assert.Null(status.Download);
        Assert.False(status.CanDownload);
        Assert.False(status.CanInstall);
        Assert.Equal(UpdateMessages.Initial, status.Message);
        Assert.Null(status.ErrorCode);
        Assert.Equal(UpdateSource.RepositoryUrl + "/releases", service.ReleaseUrl());
        Assert.Empty(_statuses);
        Assert.Empty(_transport.JsonCalls);
    }

    [Fact]
    public async Task Without_a_saved_session_no_automatic_check_is_scheduled()
    {
        _credentials.Current = UpdateCredential.Missing(CredentialFailure.NotConnected);
        var service = MakeService();

        await service.StartAsync();
        _time.Advance(TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromHours(7));

        Assert.Empty(_transport.JsonCalls);
        Assert.Equal(UpdateAuthSource.Anonymous, service.Status.AuthSource);
        Assert.Null(service.Status.GitHubAccount);
    }

    [Fact]
    public async Task With_automatic_check_off_nothing_is_scheduled()
    {
        _autoCheck = false;
        var service = MakeService();

        await service.StartAsync();
        _time.Advance(TimeSpan.FromHours(7));

        Assert.False(service.Status.AutoCheck);
        Assert.Empty(_transport.JsonCalls);
    }

    [Fact]
    public async Task Automatic_check_runs_15_seconds_after_start_then_every_6_hours()
    {
        Serve([Release("0.9.0")]);
        var service = MakeService();

        await service.StartAsync();
        _time.Advance(UpdateService.FirstCheckDelay - TimeSpan.FromSeconds(1));
        Assert.Empty(_transport.JsonCalls);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(_transport.JsonCalls);
        Assert.Equal(UpdatePhase.UpToDate, service.Status.Phase);

        _time.Advance(UpdateService.CheckInterval - TimeSpan.FromSeconds(1));
        Assert.Single(_transport.JsonCalls);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, _transport.JsonCalls.Count);

        _time.Advance(UpdateService.CheckInterval);
        Assert.Equal(3, _transport.JsonCalls.Count);
        Assert.All(_transport.JsonCalls, call => Assert.Equal((ReleasesPath, Token), call));
    }

    [Theory]
    [InlineData(CredentialFailure.NotConnected, UpdateMessages.AuthRequired)]
    [InlineData(CredentialFailure.StoredCredentialUnavailable, UpdateMessages.AuthRequiredUnreadable)]
    public async Task Check_without_a_session_asks_to_connect(CredentialFailure failure, string message)
    {
        _credentials.Current = UpdateCredential.Missing(failure);
        var service = MakeService();

        var error = await Failure(service.CheckAsync());

        Assert.Equal("UPDATES_AUTH_REQUIRED", error.Code);
        Assert.Equal(message, error.Message);
        Assert.Empty(_transport.JsonCalls);
        var status = service.Status;
        Assert.Equal(UpdatePhase.Error, status.Phase);
        Assert.Equal("UPDATES_AUTH_REQUIRED", status.ErrorCode);
        Assert.Equal(UpdateAuthSource.Anonymous, status.AuthSource);
    }

    [Fact]
    public async Task Access_error_names_the_linked_account_without_logging_it()
    {
        _transport.JsonFailure = new UpdateAccessException(401, UpdateAccessReason.Credentials, UpdateMessages.AccessCredentials);
        var service = MakeService();

        var error = await Failure(service.CheckAsync());

        Assert.Equal("UPDATES_ACCESS", error.Code);
        Assert.Equal(UpdateMessages.AccessPrefix(401, UpdateMessages.AccessCredentials) + UpdateMessages.LinkedAccount(Account), error.Message);
        var status = service.Status;
        Assert.Equal(UpdatePhase.Error, status.Phase);
        Assert.Equal("UPDATES_ACCESS", status.ErrorCode);
        Assert.Equal(error.Message, status.Message);
        Assert.Contains(_logs, line => line.StartsWith("ERROR", StringComparison.Ordinal) && line.Contains("UPDATES_ACCESS", StringComparison.Ordinal));
        AssertLogsHaveNoSecrets(Token, Account);
    }

    [Fact]
    public async Task Unexpected_exceptions_become_a_generic_update_error()
    {
        _transport.JsonFailure = new InvalidOperationException("boom");
        var service = MakeService();

        var error = await Failure(service.CheckAsync());

        Assert.Equal("UPDATES_ERROR", error.Code);
        Assert.Equal(UpdateMessages.GenericFailure, error.Message);
        Assert.Equal("UPDATES_ERROR", service.Status.ErrorCode);
        Assert.Contains(_logs, line => line.Contains(nameof(InvalidOperationException), StringComparison.Ordinal));
    }

    // ---- Esito del controllo ----

    [Fact]
    public async Task A_release_that_is_not_newer_is_reported_up_to_date()
    {
        Serve([Release("0.9.0")]);
        var service = MakeService();

        var status = await service.CheckAsync();

        Assert.Equal(UpdatePhase.UpToDate, status.Phase);
        Assert.Equal("0.9.0", status.Release?.Version);
        Assert.Equal(UpdateMessages.UpToDate, status.Message);
        Assert.Equal(_time.GetUtcNow(), status.CheckedAt);
        Assert.False(status.CanDownload);
        Assert.Null(status.ErrorCode);
        Assert.Equal((ReleasesPath, Token), _transport.JsonCalls.Single());
    }

    [Fact]
    public async Task A_newer_release_is_available_with_its_checksum_manifest()
    {
        Serve([Release("0.9.0", id: 400), Release("1.1.0")]);
        var service = MakeService();

        var status = await service.CheckAsync();

        Assert.Equal(UpdatePhase.Available, status.Phase);
        Assert.Equal("1.1.0", status.Release?.Version);
        Assert.Equal(ChecksumSource.Sha256Sums, status.Release?.Checksum);
        Assert.Equal("AIUsageMonitor-1.1.0-win-x64.exe", status.Release?.AssetName);
        Assert.Equal(UpdateMessages.Available("1.1.0", ""), status.Message);
        Assert.True(status.CanDownload);
        Assert.False(status.CanInstall);
        Assert.Equal($"{UpdateSource.RepositoryUrl}/releases/tag/v1.1.0", service.ReleaseUrl());
        Assert.Contains(_statuses, s => s.Phase == UpdatePhase.Checking && s.Message == UpdateMessages.Checking);
        AssertRevisionsIncrease();
    }

    [Theory]
    [InlineData(InstallationKind.Supported, "", true)]
    [InlineData(InstallationKind.Development, UpdateMessages.SuffixDevelopment, false)]
    [InlineData(InstallationKind.ReadOnlyLocation, UpdateMessages.SuffixReadOnlyLocation, false)]
    [InlineData(InstallationKind.UnsupportedPlatform, UpdateMessages.SuffixUnsupportedPlatform, false)]
    public async Task Available_message_explains_the_installation_kind(InstallationKind kind, string suffix, bool canDownload)
    {
        _installer.Kind = kind;
        Serve([Release("1.1.0")]);
        var service = MakeService();

        var status = await service.CheckAsync();

        Assert.Equal(UpdatePhase.Available, status.Phase);
        Assert.Equal(kind, status.Installation);
        Assert.Equal(UpdateMessages.Available("1.1.0", suffix), status.Message);
        Assert.Equal(canDownload, status.CanDownload);
    }

    [Fact]
    public async Task A_release_without_checksum_adds_the_no_checksum_suffix()
    {
        _installer.Kind = InstallationKind.Development;
        Serve([Release("1.1.0", checksums: false)]);
        var service = MakeService();

        var status = await service.CheckAsync();

        Assert.Equal(ChecksumSource.Unavailable, status.Release?.Checksum);
        Assert.Equal(UpdateMessages.Available("1.1.0", UpdateMessages.SuffixDevelopment + UpdateMessages.SuffixNoChecksum), status.Message);
    }

    [Fact]
    public async Task A_development_build_reports_its_base_version()
    {
        _installer.Kind = InstallationKind.Development;
        _currentVersion = "1.2.0";
        Serve([Release("1.1.0")]);
        var service = MakeService();

        var status = await service.CheckAsync();

        Assert.Equal(UpdatePhase.UpToDate, status.Phase);
        Assert.Equal(UpdateMessages.UpToDateDev("1.2.0", "1.1.0"), status.Message);
    }

    [Fact]
    public async Task A_current_version_without_a_stable_base_is_refused()
    {
        _currentVersion = "1.0.0-dev";
        Serve([Release("1.1.0")]);
        var service = MakeService();

        var error = await Failure(service.CheckAsync());

        Assert.Equal("UPDATES_VERSION", error.Code);
        Assert.Equal(UpdateMessages.VersionUnstable, error.Message);
        Assert.Empty(_transport.JsonCalls);
    }

    [Fact]
    public async Task No_stable_release_with_an_executable_is_an_error()
    {
        var draft = Release("1.1.0");
        draft["draft"] = true;
        Serve([draft]);
        var service = MakeService();

        var error = await Failure(service.CheckAsync());

        Assert.Equal("UPDATES_NO_RELEASE", error.Code);
        Assert.Equal(UpdateMessages.NoRelease, error.Message);
        Assert.Equal(UpdatePhase.Error, service.Status.Phase);
    }

    // ---- Download ----

    [Fact]
    public async Task Download_verifies_the_sha256sums_manifest_and_stages_the_executable()
    {
        var service = await CheckedServiceAsync();

        var status = await service.DownloadAsync();

        Assert.Equal(UpdatePhase.Downloaded, status.Phase);
        Assert.Equal(UpdateMessages.DownloadedVerified, status.Message);
        Assert.Equal(new UpdateDownload(PackageSize, PackageSize, 100, PackageSha256, true), status.Download);
        Assert.True(status.CanInstall);
        Assert.False(status.CanDownload);
        var staged = Assert.Single(Directory.GetFiles(Downloads));
        Assert.Matches(@"\AAIUsageMonitor-1\.1\.0-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.exe\z", Path.GetFileName(staged));
        Assert.EndsWith(".exe.part", Assert.Single(_transport.DownloadPaths), StringComparison.Ordinal);
        Assert.Equal(("/releases/500", Token), _transport.JsonCalls[^1]);
        Assert.Equal(new[] { 503L }, _transport.AssetCalls);
        Assert.Contains(_statuses, s => s.Phase == UpdatePhase.Downloading && s.Message == UpdateMessages.Downloading && s.Download?.Percent == 0);
        Assert.Contains(_statuses, s => s.Phase == UpdatePhase.Downloading && s.Download is { ReceivedBytes: PackageSize / 4, Percent: 25, Verified: false });
        AssertRevisionsIncrease();
    }

    [Fact]
    public async Task Download_accepts_a_manifest_that_starts_with_a_byte_order_mark()
    {
        _transport.Asset = _ => [.. Encoding.UTF8.GetPreamble(), .. Manifest(PackageSha256)];
        var service = await CheckedServiceAsync();

        var status = await service.DownloadAsync();

        Assert.Equal(UpdatePhase.Downloaded, status.Phase);
        Assert.True(status.Download?.Verified);
    }

    [Fact]
    public async Task Download_verifies_the_github_digest_when_there_is_no_manifest()
    {
        var service = await CheckedServiceAsync(Release("1.1.0", checksums: false, digest: PackageSha256));
        Assert.Equal(ChecksumSource.GitHubDigest, service.Status.Release?.Checksum);

        var status = await service.DownloadAsync();

        Assert.Equal(UpdatePhase.Downloaded, status.Phase);
        Assert.True(status.Download?.Verified);
        Assert.Empty(_transport.AssetCalls);
    }

    [Fact]
    public async Task Download_without_any_published_checksum_is_staged_unverified()
    {
        var service = await CheckedServiceAsync(Release("1.1.0", checksums: false));

        var status = await service.DownloadAsync();

        Assert.Equal(UpdatePhase.Downloaded, status.Phase);
        Assert.Equal(UpdateMessages.DownloadedUnverified, status.Message);
        Assert.False(status.Download?.Verified);
        Assert.Equal(PackageSha256, status.Download?.Sha256);
        Assert.True(status.CanInstall);
    }

    [Fact]
    public async Task Download_refuses_a_manifest_that_contradicts_the_github_digest()
    {
        var service = await CheckedServiceAsync(Release("1.1.0", digest: new string('b', 64)));

        var error = await Failure(service.DownloadAsync());

        Assert.Equal("UPDATES_CHECKSUM", error.Code);
        Assert.Equal(UpdateMessages.ChecksumConflict, error.Message);
        Assert.Empty(_transport.DownloadPaths);
        Assert.Equal(UpdatePhase.Error, service.Status.Phase);
    }

    [Fact]
    public async Task Download_refuses_a_mismatching_hash_and_removes_the_partial_file()
    {
        _transport.Asset = _ => Manifest(new string('a', 64));
        var service = await CheckedServiceAsync();

        var error = await Failure(service.DownloadAsync());

        Assert.Equal("UPDATES_INTEGRITY", error.Code);
        Assert.Equal(UpdateMessages.IntegrityMismatch, error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Downloads));
        var status = service.Status;
        Assert.Equal(UpdatePhase.Error, status.Phase);
        Assert.False(status.CanInstall);
        Assert.True(status.CanDownload); // si puo' riprovare
    }

    [Fact]
    public async Task Download_refuses_a_file_that_is_not_a_windows_executable()
    {
        byte[] notExecutable = [.. "XX"u8, .. Enumerable.Repeat((byte)0x41, PackageSize - 2)];
        _transport.Asset = _ => Manifest(Sha256(notExecutable));
        _transport.Download = (path, _, progress, _) => FakeTransport.Write(path, notExecutable, progress);
        var service = await CheckedServiceAsync();

        var error = await Failure(service.DownloadAsync());

        Assert.Equal("UPDATES_FORMAT", error.Code);
        Assert.Equal(UpdateMessages.NotWindowsExecutable, error.Message);
        Assert.Empty(Directory.GetFileSystemEntries(Downloads));
    }

    [Fact]
    public async Task Download_refuses_a_release_changed_after_the_check()
    {
        var service = await CheckedServiceAsync(Release("1.1.0"), exact: Release("1.1.0", packageId: 999));

        var error = await Failure(service.DownloadAsync());

        Assert.Equal("UPDATES_CHANGED", error.Code);
        Assert.Equal(UpdateMessages.ReleaseChanged, error.Message);
        Assert.Empty(_transport.DownloadPaths);
    }

    [Fact]
    public async Task Download_failure_removes_the_partial_file()
    {
        _transport.Download = (path, _, _, _) =>
        {
            File.WriteAllBytes(path, PackageBytes[..100]);
            throw new UpdateException("UPDATES_TRANSFER", UpdateMessages.TransferIncomplete);
        };
        var service = await CheckedServiceAsync();

        var error = await Failure(service.DownloadAsync());

        Assert.Equal("UPDATES_TRANSFER", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(Downloads));
        Assert.Equal(UpdatePhase.Error, service.Status.Phase);
    }

    [Fact]
    public async Task Download_needs_a_checked_release_and_a_supported_installation()
    {
        Serve([Release("1.1.0")]);
        var service = MakeService();
        Assert.Equal("UPDATES_DOWNLOAD_STATE", (await Failure(service.DownloadAsync())).Code);

        await service.CheckAsync();
        _installer.Kind = InstallationKind.ReadOnlyLocation;
        var error = await Failure(service.DownloadAsync());

        Assert.Equal("UPDATES_DOWNLOAD_STATE", error.Code);
        Assert.Equal(UpdateMessages.DownloadState, error.Message);
        Assert.Equal(InstallationKind.ReadOnlyLocation, service.Status.Installation);
        Assert.False(service.Status.CanDownload);
        Assert.Empty(_transport.DownloadPaths);
    }

    [Fact]
    public async Task A_staged_download_survives_a_recheck_and_is_dropped_for_a_newer_release()
    {
        var service = await DownloadedServiceAsync();
        var staged = Assert.Single(Directory.GetFiles(Downloads));

        var again = await service.CheckAsync();
        Assert.Equal(UpdatePhase.Downloaded, again.Phase);
        Assert.True(again.CanInstall);
        Assert.NotNull(again.Download);
        Assert.True(File.Exists(staged));

        Serve([Release("1.2.0", id: 600)]);
        var newer = await service.CheckAsync();
        Assert.Equal(UpdatePhase.Available, newer.Phase);
        Assert.Equal("1.2.0", newer.Release?.Version);
        Assert.True(newer.CanDownload);
        Assert.False(newer.CanInstall);
        Assert.False(File.Exists(staged));
    }

    [Fact]
    public async Task Automatic_check_waits_while_a_download_is_staged()
    {
        var service = MakeService();
        await service.StartAsync();
        var checkedService = await CheckedServiceAsync(service: service);
        await checkedService.DownloadAsync();
        var calls = _transport.JsonCalls.Count;

        _time.Advance(UpdateService.CheckInterval);
        _time.Advance(UpdateService.CheckInterval);

        Assert.Equal(calls, _transport.JsonCalls.Count);
        Assert.Equal(UpdatePhase.Downloaded, service.Status.Phase);
    }

    // ---- Installazione ----

    [Fact]
    public async Task Install_verifies_the_staged_file_again_and_starts_the_new_version()
    {
        var service = await DownloadedServiceAsync();

        var status = await service.InstallAsync();

        var staged = Assert.Single(_installer.Installed);
        Assert.Equal("1.1.0", staged.Version);
        Assert.Equal(PackageSha256, staged.Sha256);
        Assert.Equal(PackageSha256, staged.ExpectedHash);
        Assert.Equal(PackageSize, staged.Size);
        Assert.True(File.Exists(staged.Path));
        Assert.Equal(1, _quits);
        Assert.Equal(UpdatePhase.Installing, status.Phase);
        Assert.Equal(UpdateMessages.InstallStarted, status.Message);
        Assert.False(status.CanInstall);
        Assert.False(status.CanDownload);
        Assert.Contains(_statuses, s => s.Phase == UpdatePhase.Installing && s.Message == UpdateMessages.FinalVerification);
        Assert.Equal("UPDATES_INSTALL_STATE", (await Failure(service.InstallAsync())).Code);
    }

    [Fact]
    public async Task Install_refuses_a_staged_version_that_is_not_newer()
    {
        // Irraggiungibile dall'API pubblica (il controllo scarta le release non piu' recenti): si verifica la difesa
        // in profondita' iniettando direttamente l'eseguibile pronto.
        var service = MakeService();
        _ = service.Status;
        var path = Path.Combine(_dir.Path, "old.exe");
        File.WriteAllBytes(path, PackageBytes);
        typeof(UpdateService).GetField("_staged", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(service, new StagedUpdate(path, PackageSha256, PackageSha256, PackageSize, "1.0.0"));

        var error = await Failure(service.InstallAsync());

        Assert.Equal("UPDATES_VERSION", error.Code);
        Assert.Equal(UpdateMessages.NotNewer, error.Message);
        Assert.Empty(_installer.Installed);
        Assert.Equal(0, _quits);
        Assert.Equal(UpdatePhase.Error, service.Status.Phase);
    }

    [Fact]
    public async Task Install_refuses_a_staged_file_altered_on_disk()
    {
        var service = await DownloadedServiceAsync();
        var staged = Assert.Single(Directory.GetFiles(Downloads));
        byte[] altered = [.. "MZ"u8, .. Enumerable.Repeat((byte)0x42, PackageSize - 2)];
        File.WriteAllBytes(staged, altered);

        var error = await Failure(service.InstallAsync());

        Assert.Equal("UPDATES_INTEGRITY", error.Code);
        Assert.Equal(UpdateMessages.IntegrityChangedLocally, error.Message);
        Assert.False(File.Exists(staged));
        Assert.Empty(_installer.Installed);
        var status = service.Status;
        Assert.Equal(UpdatePhase.Error, status.Phase);
        Assert.False(status.CanInstall);
        Assert.True(status.CanDownload); // si puo' scaricare di nuovo
    }

    [Fact]
    public async Task Install_refuses_a_staged_file_whose_size_changed()
    {
        var service = await DownloadedServiceAsync();
        var staged = Assert.Single(Directory.GetFiles(Downloads));
        File.AppendAllText(staged, "extra");

        var error = await Failure(service.InstallAsync());

        Assert.Equal("UPDATES_INTEGRITY", error.Code);
        Assert.Equal(UpdateMessages.LocalPackageChanged, error.Message);
        Assert.False(File.Exists(staged));
        Assert.False(service.Status.CanInstall);
        Assert.Empty(_installer.Installed);
    }

    [Fact]
    public async Task Install_failure_keeps_the_staged_file_for_a_retry()
    {
        _installer.Handler = (_, _) => throw new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallReplaceFailed);
        var service = await DownloadedServiceAsync();
        var staged = Assert.Single(Directory.GetFiles(Downloads));

        var error = await Failure(service.InstallAsync());

        Assert.Equal("UPDATES_INSTALL", error.Code);
        Assert.Equal(UpdateMessages.InstallReplaceFailed, error.Message);
        Assert.Equal(0, _quits);
        Assert.True(File.Exists(staged));
        var status = service.Status;
        Assert.Equal(UpdatePhase.Error, status.Phase);
        Assert.Equal("UPDATES_INSTALL", status.ErrorCode);
        Assert.True(status.CanInstall);
    }

    [Fact]
    public async Task Install_maps_an_unexpected_installer_exception_to_a_generic_error()
    {
        _installer.Handler = (_, _) => throw new IOException("disk");
        var service = await DownloadedServiceAsync();

        var error = await Failure(service.InstallAsync());

        Assert.Equal("UPDATES_ERROR", error.Code);
        Assert.Equal(UpdateMessages.GenericFailure, error.Message);
        Assert.Equal(0, _quits);
    }

    [Fact]
    public async Task Install_checks_the_installation_kind_again()
    {
        var service = await DownloadedServiceAsync();
        _installer.Kind = InstallationKind.UnsupportedPlatform;

        var error = await Failure(service.InstallAsync());

        Assert.Equal("UPDATES_INSTALL_STATE", error.Code);
        Assert.Equal(UpdateMessages.InstallState, error.Message);
        Assert.Equal(InstallationKind.UnsupportedPlatform, service.Status.Installation);
        Assert.Empty(_installer.Installed);
    }

    // ---- Collegamento GitHub ----

    [Fact]
    public async Task Authenticate_connects_and_checks_with_the_returned_credential()
    {
        _credentials.Current = UpdateCredential.Missing(CredentialFailure.NotConnected);
        Serve([Release("1.1.0")]);
        var service = MakeService();
        await service.StartAsync();

        var status = await service.AuthenticateAsync();

        Assert.Equal(UpdatePhase.Available, status.Phase);
        Assert.Equal(UpdateAuthSource.GitHubApp, status.AuthSource);
        Assert.Equal(LinkedAccount, status.GitHubAccount);
        Assert.Equal((ReleasesPath, LinkedToken), Assert.Single(_transport.JsonCalls));
        Assert.Equal(1, _login.Calls);
        Assert.Equal(1, _returns);
        Assert.Contains(_statuses, s => s.Phase == UpdatePhase.Authenticating && s.Message == UpdateMessages.AuthInProgress);
        Assert.Contains(_statuses, s => s.Phase == UpdatePhase.Checking && s.Message == UpdateMessages.AuthSaving);
        AssertLogsHaveNoSecrets(LinkedToken, LinkedAccount);

        // Il login reale salva la sessione nello store: da li' la legge il controllo periodico, ora pianificato.
        _credentials.Save(LinkedToken, LinkedAccount);
        _time.Advance(UpdateService.CheckInterval);
        Assert.Equal(2, _transport.JsonCalls.Count);
        Assert.Equal((ReleasesPath, LinkedToken), _transport.JsonCalls[^1]);
    }

    [Fact]
    public async Task Authentication_can_be_cancelled_while_waiting_for_the_browser()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _login.Handler = async (_, cancellationToken) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { throw new UpdateException("UPDATES_AUTH_CANCELLED", UpdateMessages.AuthCancelled); }
            throw new InvalidOperationException("unreachable");
        };
        var service = MakeService();

        var authentication = service.AuthenticateAsync();
        await entered.Task;

        Assert.Equal(UpdatePhase.Authenticating, service.Status.Phase);
        var busyCheck = await Failure(service.CheckAsync());
        Assert.Equal(("UPDATES_BUSY", UpdateMessages.BusyAuth), (busyCheck.Code, busyCheck.Message));
        Assert.Equal("UPDATES_BUSY", (await Failure(service.AuthenticateAsync())).Code);
        Assert.Equal("UPDATES_BUSY", (await Failure(service.DisconnectAsync())).Code);

        var cancelled = await service.CancelAuthenticationAsync();
        Assert.Equal(UpdatePhase.Idle, cancelled.Phase);
        Assert.Equal(UpdateMessages.AuthCancelledNotice, cancelled.Message);
        Assert.Null(cancelled.ErrorCode);

        var result = await authentication;
        Assert.Equal(UpdatePhase.Idle, result.Phase);
        Assert.Empty(_transport.JsonCalls);
        Assert.Equal(0, _returns);
    }

    [Fact]
    public async Task Cancel_without_a_pending_authentication_changes_nothing()
    {
        var service = MakeService();
        var before = service.Status;

        var after = await service.CancelAuthenticationAsync();

        Assert.Same(before, after);
    }

    [Fact]
    public async Task A_failed_login_is_reported_as_an_error()
    {
        _login.Handler = (_, _) => throw new UpdateException("UPDATES_AUTH_GCM", UpdateMessages.GcmMissing);
        var service = MakeService();

        var error = await Failure(service.AuthenticateAsync());

        Assert.Equal("UPDATES_AUTH_GCM", error.Code);
        Assert.Equal(UpdateMessages.GcmMissing, error.Message);
        Assert.Equal(UpdatePhase.Error, service.Status.Phase);
        Assert.Empty(_transport.JsonCalls);
    }

    [Fact]
    public async Task Disconnect_removes_the_session_and_stops_automatic_checks()
    {
        Serve([Release("0.9.0")]);
        var service = MakeService();
        await service.StartAsync();

        var status = await service.DisconnectAsync();

        Assert.Equal(1, _credentials.Deletes);
        Assert.Equal(UpdatePhase.Idle, status.Phase);
        Assert.Equal(UpdateAuthSource.Anonymous, status.AuthSource);
        Assert.Null(status.GitHubAccount);
        Assert.Equal(UpdateMessages.Disconnected, status.Message);
        Assert.Null(status.ErrorCode);
        _time.Advance(TimeSpan.FromHours(7));
        Assert.Empty(_transport.JsonCalls);
        Assert.Equal("UPDATES_AUTH_REQUIRED", (await Failure(service.CheckAsync())).Code);
    }

    [Fact]
    public async Task Disconnect_failure_is_reported()
    {
        _credentials.DeleteFailure = new IOException("locked");
        var service = MakeService();

        var error = await Failure(service.DisconnectAsync());

        Assert.Equal("UPDATES_DISCONNECT", error.Code);
        Assert.Equal(UpdateMessages.DisconnectFailed, error.Message);
        var status = service.Status;
        Assert.Equal(UpdatePhase.Error, status.Phase);
        Assert.Equal(UpdateAuthSource.GitHubApp, status.AuthSource);
    }

    // ---- Preferenze, pulizia, concorrenza, chiusura ----

    [Fact]
    public async Task Preference_changes_update_the_status_and_reschedule_the_check()
    {
        Serve([Release("0.9.0")]);
        var service = MakeService();
        await service.StartAsync();

        service.PreferencesChanged(false);
        Assert.False(service.Status.AutoCheck);
        Assert.Contains(_statuses, s => !s.AutoCheck);
        _time.Advance(TimeSpan.FromHours(7));
        Assert.Empty(_transport.JsonCalls);

        var revision = service.Status.Revision;
        service.PreferencesChanged(false);
        Assert.Equal(revision, service.Status.Revision);

        service.PreferencesChanged(true);
        Assert.True(service.Status.AutoCheck);
        _time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Empty(_transport.JsonCalls);
        _time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Single(_transport.JsonCalls);
    }

    [Fact]
    public async Task Obsolete_downloads_are_removed_after_the_cleanup_delay()
    {
        _credentials.Current = UpdateCredential.Missing(CredentialFailure.NotConnected);
        Directory.CreateDirectory(Downloads);
        var older = DownloadFile($"AIUsageMonitor-0.9.0-{Guid.NewGuid():D}.exe", TimeSpan.FromHours(1));
        var current = DownloadFile($"AIUsageMonitor-1.0.0-{Guid.NewGuid():D}.exe.part", TimeSpan.FromHours(1));
        var newer = DownloadFile($"AIUsageMonitor-1.2.0-{Guid.NewGuid():D}.exe", TimeSpan.FromHours(1));
        var stale = DownloadFile($"AIUsageMonitor-1.2.0-{Guid.NewGuid():D}.exe.part", TimeSpan.FromDays(8));
        var foreign = DownloadFile("notes.txt", TimeSpan.FromDays(30));
        var leadingZero = DownloadFile($"AIUsageMonitor-01.2.0-{Guid.NewGuid():D}.exe", TimeSpan.FromDays(30));
        var service = MakeService();

        await service.StartAsync();
        _time.Advance(UpdateService.ObsoleteCleanupDelay - TimeSpan.FromSeconds(1));
        Assert.All(new[] { older, current, newer, stale, foreign, leadingZero }, path => Assert.True(File.Exists(path)));

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(File.Exists(older));
        Assert.False(File.Exists(current));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(newer));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(leadingZero));
    }

    [Fact]
    public async Task Cleanup_keeps_the_staged_download()
    {
        _autoCheck = false;
        var service = MakeService();
        await service.StartAsync();
        await CheckedServiceAsync(service: service);
        await service.DownloadAsync();
        var staged = Assert.Single(Directory.GetFiles(Downloads));
        File.SetLastWriteTimeUtc(staged, _time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(8));

        _time.Advance(UpdateService.ObsoleteCleanupDelay);

        Assert.True(File.Exists(staged));
        Assert.True(service.Status.CanInstall);
    }

    [Fact]
    public async Task Concurrent_checks_share_one_operation()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.BeforeJson = _ => gate.Task;
        Serve([Release("1.1.0")]);
        var service = MakeService();

        var first = service.CheckAsync();
        var second = service.CheckAsync();

        Assert.Same(first, second);
        Assert.Equal(UpdatePhase.Checking, service.Status.Phase);
        Assert.False(service.Status.CanDownload);
        Assert.Equal("UPDATES_BUSY", (await Failure(service.AuthenticateAsync())).Code);
        Assert.Equal("UPDATES_BUSY", (await Failure(service.DisconnectAsync())).Code);
        Assert.Equal("UPDATES_DOWNLOAD_STATE", (await Failure(service.DownloadAsync())).Code);

        gate.SetResult();
        var status = await first;

        Assert.Equal(UpdatePhase.Available, status.Phase);
        Assert.True(status.CanDownload);
        Assert.Same(status, await second);
        Assert.Single(_transport.JsonCalls);
    }

    [Fact]
    public async Task A_failing_subscriber_does_not_break_the_service()
    {
        Serve([Release("1.1.0")]);
        var service = MakeService();
        service.Changed += _ => throw new InvalidOperationException("subscriber");

        var status = await service.CheckAsync();

        Assert.Equal(UpdatePhase.Available, status.Phase);
        Assert.Contains(_logs, line => line.Contains("subscriber", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Dispose_stops_timers_is_idempotent_and_closes_the_service()
    {
        Serve([Release("0.9.0")]);
        var service = MakeService();
        await service.StartAsync();
        Assert.Equal(2, _time.ActiveTimers);

        service.Dispose();
        service.Dispose();

        Assert.Equal(0, _time.ActiveTimers);
        _time.Advance(TimeSpan.FromHours(7));
        Assert.Empty(_transport.JsonCalls);
        Assert.Equal("UPDATES_CLOSED", (await Failure(service.CheckAsync())).Code);
        Assert.Equal("UPDATES_CLOSED", (await Failure(service.AuthenticateAsync())).Code);
        Assert.Equal("UPDATES_CLOSED", (await Failure(service.DisconnectAsync())).Code);
        Assert.Equal("UPDATES_DOWNLOAD_STATE", (await Failure(service.DownloadAsync())).Code);
        Assert.Empty(_statuses);
    }

    [Fact]
    public async Task Dispose_cancels_the_active_download_and_removes_the_partial_file()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _transport.Download = async (path, _, _, cancellationToken) =>
        {
            await File.WriteAllBytesAsync(path, PackageBytes[..10], CancellationToken.None);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        };
        var service = await CheckedServiceAsync();
        var download = service.DownloadAsync();
        await started.Task;
        var events = _statuses.Count;

        service.Dispose();
        var error = await Failure(download);

        Assert.Equal("UPDATES_CLOSED", error.Code);
        Assert.Empty(Directory.GetFileSystemEntries(Downloads));
        Assert.Equal(events, _statuses.Count);
    }

    [Fact]
    public async Task Dispose_removes_the_staged_download()
    {
        var service = await DownloadedServiceAsync();
        var staged = Assert.Single(Directory.GetFiles(Downloads));

        service.Dispose();

        Assert.False(File.Exists(staged));
        Assert.False(service.Status.CanInstall);
    }

    [Fact]
    public async Task Dispose_keeps_the_staged_download_while_installing()
    {
        var installing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _installer.Handler = async (_, _) =>
        {
            installing.SetResult();
            await proceed.Task;
        };
        var service = await DownloadedServiceAsync();
        var install = service.InstallAsync();
        await installing.Task;
        var staged = _installer.Installed.Single().Path;

        service.Dispose();
        Assert.True(File.Exists(staged));

        proceed.SetResult();
        await install;
        Assert.True(File.Exists(staged));
    }

    // ---- Supporto ----

    private UpdateService MakeService()
    {
        var service = new UpdateService(new UpdateServiceOptions
        {
            Transport = _transport,
            Credentials = _credentials,
            Login = _login,
            Installer = _installer,
            DownloadsDirectory = Downloads,
            CurrentVersion = _currentVersion,
            Variant = PackageVariant.FrameworkDependent,
            AutoCheck = () => _autoCheck,
            Time = _time,
            QuitForInstall = () => Interlocked.Increment(ref _quits),
            ReturnToApp = () => Interlocked.Increment(ref _returns),
            LogInfo = message => { lock (_logs) _logs.Add("INFO " + message); },
            LogError = (message, error) => { lock (_logs) _logs.Add($"ERROR {message}: {error}"); }
        });
        service.Changed += status => { lock (_statuses) _statuses.Add(status); };
        _services.Add(service);
        return service;
    }

    /// <summary>Servizio che ha appena trovato <paramref name="latest"/> (default 1.1.0 con SHA256SUMS corretto).</summary>
    private async Task<UpdateService> CheckedServiceAsync(JsonObject? latest = null, JsonObject? exact = null, UpdateService? service = null)
    {
        latest ??= Release("1.1.0");
        Serve([latest], exact ?? (JsonObject)latest.DeepClone());
        if (!_transport.AssetConfigured) _transport.Asset = _ => Manifest(PackageSha256);
        service ??= MakeService();
        var status = await service.CheckAsync();
        Assert.Equal(UpdatePhase.Available, status.Phase);
        return service;
    }

    private async Task<UpdateService> DownloadedServiceAsync()
    {
        var service = await CheckedServiceAsync();
        var status = await service.DownloadAsync();
        Assert.Equal(UpdatePhase.Downloaded, status.Phase);
        return service;
    }

    private void Serve(JsonObject[] list, JsonObject? exact = null)
    {
        var listJson = new JsonArray(list.Select(release => release.DeepClone()).ToArray()).ToJsonString();
        var exactJson = (exact ?? list.LastOrDefault())?.ToJsonString() ?? "{}";
        _transport.Json = path => path.StartsWith("/releases?", StringComparison.Ordinal) ? listJson : exactJson;
    }

    private static JsonObject Release(string version, long id = 500, bool checksums = true, string? digest = null, long? packageId = null)
    {
        var assets = new JsonArray
        {
            Asset(packageId ?? id + 1, UpdateSource.AssetName(version, PackageVariant.FrameworkDependent), PackageSize, digest),
            Asset(id + 2, UpdateSource.AssetName(version, PackageVariant.SelfContained), PackageSize, null)
        };
        if (checksums) assets.Add(Asset(id + 3, UpdateSource.ChecksumManifestName, 200, null));
        return new JsonObject
        {
            ["id"] = id,
            ["draft"] = false,
            ["prerelease"] = false,
            ["tag_name"] = $"v{version}",
            ["published_at"] = "2026-09-01T10:00:00Z",
            ["body"] = $"Release {version}",
            ["assets"] = assets
        };
    }

    private static JsonObject Asset(long id, string name, long size, string? digest)
    {
        var asset = new JsonObject { ["id"] = id, ["state"] = "uploaded", ["name"] = name, ["size"] = size };
        if (digest is not null) asset["digest"] = "sha256:" + digest;
        return asset;
    }

    private static byte[] Manifest(string hash) =>
        Encoding.UTF8.GetBytes($"{hash}  AIUsageMonitor-1.1.0-win-x64.exe\n{hash}  AIUsageMonitor-1.1.0-win-x64-selfcontained.exe.sig\n0{new string('f', 63)}  SHA256SUMS.txt\n");

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string DownloadFile(string name, TimeSpan age)
    {
        var path = Path.Combine(Downloads, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, _time.GetUtcNow().UtcDateTime - age);
        return path;
    }

    private static async Task<UpdateException> Failure(Task task)
    {
        var error = await Record.ExceptionAsync(() => task);
        return Assert.IsAssignableFrom<UpdateException>(error);
    }

    private void AssertRevisionsIncrease()
    {
        List<long> revisions;
        lock (_statuses) revisions = _statuses.Select(s => s.Revision).ToList();
        Assert.Equal(revisions.Order().Distinct(), revisions);
    }

    private void AssertLogsHaveNoSecrets(params string[] secrets)
    {
        List<string> lines;
        lock (_logs) lines = [.. _logs];
        Assert.NotEmpty(lines);
        foreach (var secret in secrets)
            Assert.DoesNotContain(lines, line => line.Contains(secret, StringComparison.Ordinal));
    }

    private sealed class FakeTransport : IReleaseTransport
    {
        private readonly object _gate = new();
        private Func<long, byte[]>? _asset;

        public Func<string, string> Json { get; set; } = _ => "[]";
        public Func<string, Task>? BeforeJson { get; set; }
        public Exception? JsonFailure { get; set; }
        public bool AssetConfigured => _asset is not null;
        public Func<long, byte[]> Asset { get => _asset ?? (_ => throw new InvalidOperationException("asset not configured")); set => _asset = value; }
        public Func<string, long, IProgress<long>?, CancellationToken, Task<DownloadResult>>? Download { get; set; }
        public List<(string Path, string Token)> JsonCalls { get; } = [];
        public List<long> AssetCalls { get; } = [];
        public List<string> DownloadPaths { get; } = [];

        public async Task<JsonDocument> ReadJsonAsync(string relativePath, string token, CancellationToken cancellationToken)
        {
            lock (_gate) JsonCalls.Add((relativePath, token));
            if (BeforeJson is not null) await BeforeJson(relativePath);
            if (JsonFailure is not null) throw JsonFailure;
            return JsonDocument.Parse(Json(relativePath));
        }

        public Task<byte[]> ReadAssetBytesAsync(long assetId, string token, long maxBytes, CancellationToken cancellationToken)
        {
            lock (_gate) AssetCalls.Add(assetId);
            Assert.Equal(UpdateSource.MaxChecksumManifestBytes, maxBytes);
            return Task.FromResult(Asset(assetId));
        }

        public Task<DownloadResult> DownloadAssetAsync(long assetId, string destinationPath, long expectedSize, string token,
            IProgress<long>? progress, CancellationToken cancellationToken)
        {
            lock (_gate) DownloadPaths.Add(destinationPath);
            return Download is not null
                ? Download(destinationPath, expectedSize, progress, cancellationToken)
                : Write(destinationPath, PackageBytes, progress);
        }

        public static Task<DownloadResult> Write(string path, byte[] bytes, IProgress<long>? progress)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) stream.Write(bytes);
            progress?.Report(bytes.Length / 4);
            progress?.Report(bytes.Length);
            return Task.FromResult(new DownloadResult(Sha256(bytes), bytes.Length));
        }
    }

    private sealed class FakeCredentials : IUpdateCredentialStore
    {
        public UpdateCredential Current { get; set; } = UpdateCredential.Connected(Token, Account);
        public Exception? DeleteFailure { get; set; }
        public int Deletes { get; private set; }

        public UpdateCredential Read() => Current;

        public void Save(string token, string account) => Current = UpdateCredential.Connected(token, account);

        public void Delete()
        {
            if (DeleteFailure is not null) throw DeleteFailure;
            Deletes++;
            Current = UpdateCredential.Missing(CredentialFailure.NotConnected);
        }
    }

    private sealed class FakeLogin : IGitHubLogin
    {
        public Func<Action?, CancellationToken, Task<UpdateCredential>> Handler { get; set; } = (ready, _) =>
        {
            ready?.Invoke();
            return Task.FromResult(UpdateCredential.Connected(LinkedToken, LinkedAccount));
        };

        public int Calls { get; private set; }

        public Task<UpdateCredential> AuthenticateAsync(Action? credentialReady, CancellationToken cancellationToken)
        {
            Calls++;
            return Handler(credentialReady, cancellationToken);
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        public InstallationKind Kind { get; set; } = InstallationKind.Supported;
        public Func<StagedUpdate, CancellationToken, Task> Handler { get; set; } = (_, _) => Task.CompletedTask;
        public List<StagedUpdate> Installed { get; } = [];

        public InstallationKind DetectInstallation() => Kind;

        public Task InstallAsync(StagedUpdate staged, CancellationToken cancellationToken)
        {
            Installed.Add(staged);
            return Handler(staged, cancellationToken);
        }
    }
}
