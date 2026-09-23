using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.Tests;

public sealed class UpdatePromptControllerTests
{
    private readonly FakeCommands _commands = new();
    private readonly List<UpdatePromptState> _states = [];

    [Fact]
    public void Start_offers_an_available_version()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();

        controller.Start();

        Assert.Equal("1.1.0", controller.State.Version);
        Assert.Same(_commands.Status, controller.State.Status);
        Assert.Null(controller.State.Pending);
        Assert.Equal("", controller.State.Error);
        Assert.Single(_states);
        Assert.Equal(1, _commands.Subscribers);
    }

    [Fact]
    public void A_downloaded_version_ready_to_install_is_offered()
    {
        _commands.Status = Status(1, UpdatePhase.Downloaded, canInstall: true);
        using var controller = Make();

        controller.Start();

        Assert.Equal("1.1.0", controller.State.Version);
    }

    [Fact]
    public void Nothing_is_offered_when_automatic_check_is_off()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true, autoCheck: false);
        using var controller = Make();

        controller.Start();

        Assert.Null(controller.State.Version);
        Assert.NotNull(controller.State.Status);
    }

    [Theory]
    [InlineData(InstallationKind.Development)]
    [InlineData(InstallationKind.ReadOnlyLocation)]
    [InlineData(InstallationKind.UnsupportedPlatform)]
    public void Nothing_is_offered_when_the_executable_cannot_update_itself(InstallationKind installation)
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true, installation: installation);
        using var controller = Make();

        controller.Start();

        Assert.Null(controller.State.Version);
    }

    [Theory]
    [InlineData(UpdatePhase.Checking)]
    [InlineData(UpdatePhase.Downloading)]
    [InlineData(UpdatePhase.Error)]
    [InlineData(UpdatePhase.Idle)]
    public void Nothing_is_offered_outside_available_or_downloaded(UpdatePhase phase)
    {
        _commands.Status = Status(1, phase, canDownload: true);
        using var controller = Make();

        controller.Start();

        Assert.Null(controller.State.Version);
    }

    [Fact]
    public void Nothing_is_offered_when_neither_download_nor_install_is_possible()
    {
        _commands.Status = Status(1, UpdatePhase.Available);
        using var controller = Make();

        controller.Start();

        Assert.Null(controller.State.Version);
    }

    [Fact]
    public void Up_to_date_withdraws_the_offer_and_other_phases_keep_it()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();

        _commands.Emit(Status(2, UpdatePhase.Checking));
        Assert.Equal("1.1.0", controller.State.Version);

        _commands.Emit(Status(3, UpdatePhase.Error));
        Assert.Equal("1.1.0", controller.State.Version);

        _commands.Emit(Status(4, UpdatePhase.UpToDate, version: "1.1.0"));
        Assert.Null(controller.State.Version);
    }

    [Fact]
    public void A_different_release_withdraws_the_offer()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();

        _commands.Emit(Status(2, UpdatePhase.Error, version: "1.2.0"));

        Assert.Null(controller.State.Version);
        Assert.Equal("1.2.0", controller.State.Status?.Release?.Version);
    }

    [Fact]
    public void Turning_automatic_check_off_withdraws_the_offer()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();

        _commands.Emit(Status(2, UpdatePhase.Available, canDownload: true, autoCheck: false));

        Assert.Null(controller.State.Version);
    }

    [Fact]
    public void Older_revisions_are_ignored()
    {
        _commands.Status = Status(5, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();
        var events = _states.Count;

        _commands.Emit(Status(4, UpdatePhase.UpToDate));

        Assert.Equal("1.1.0", controller.State.Version);
        Assert.Equal(5, controller.State.Status?.Revision);
        Assert.Equal(events, _states.Count);
    }

    [Fact]
    public void Dismiss_ignores_that_version_but_offers_a_newer_one()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();

        controller.Dismiss();
        Assert.Null(controller.State.Version);

        _commands.Emit(Status(2, UpdatePhase.Available, canDownload: true));
        Assert.Null(controller.State.Version);

        _commands.Emit(Status(3, UpdatePhase.Available, version: "1.2.0", canDownload: true));
        Assert.Equal("1.2.0", controller.State.Version);
    }

    [Fact]
    public void Dismiss_without_an_offer_does_nothing()
    {
        using var controller = Make();
        controller.Start();
        var events = _states.Count;

        controller.Dismiss();

        Assert.Equal(events, _states.Count);
    }

    [Fact]
    public async Task Confirm_downloads_then_installs_with_one_consent()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();
        UpdatePromptPending? pendingWhileDownloading = null;
        UpdatePromptPending? pendingWhileInstalling = null;
        _commands.Download = () =>
        {
            pendingWhileDownloading = controller.State.Pending;
            return Task.FromResult(Status(2, UpdatePhase.Downloaded, canInstall: true));
        };
        _commands.Install = () =>
        {
            pendingWhileInstalling = controller.State.Pending;
            return Task.FromResult(Status(3, UpdatePhase.Installing));
        };

        await controller.ConfirmAsync();

        Assert.Equal(new[] { "download", "install" }, _commands.Calls);
        Assert.Equal(UpdatePromptPending.Downloading, pendingWhileDownloading);
        Assert.Equal(UpdatePromptPending.Installing, pendingWhileInstalling);
        // La conferma resta bloccata finche' l'app non si chiude.
        Assert.Equal(UpdatePromptPending.Installing, controller.State.Pending);
        Assert.Equal("1.1.0", controller.State.Version);
        Assert.Equal(3, controller.State.Status?.Revision);
        Assert.Equal("", controller.State.Error);
    }

    [Fact]
    public async Task Confirm_installs_directly_when_the_version_is_already_downloaded()
    {
        _commands.Status = Status(1, UpdatePhase.Downloaded, canInstall: true);
        using var controller = Make();
        controller.Start();
        _commands.Install = () => Task.FromResult(Status(2, UpdatePhase.Installing));

        await controller.ConfirmAsync();

        Assert.Equal(new[] { "install" }, _commands.Calls);
        Assert.Equal(UpdatePromptPending.Installing, controller.State.Pending);
    }

    [Fact]
    public async Task Confirm_without_an_offer_does_nothing()
    {
        _commands.Status = Status(1, UpdatePhase.UpToDate);
        using var controller = Make();
        controller.Start();

        await controller.ConfirmAsync();

        Assert.Empty(_commands.Calls);
        Assert.Null(controller.State.Pending);
    }

    [Fact]
    public async Task A_download_failure_shows_its_message_and_unlocks_the_prompt()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();
        _commands.Download = () => throw new UpdateException("UPDATES_INTEGRITY", UpdateMessages.IntegrityMismatch);

        await controller.ConfirmAsync();

        Assert.Equal(UpdateMessages.IntegrityMismatch, controller.State.Error);
        Assert.Null(controller.State.Pending);
        Assert.Equal("1.1.0", controller.State.Version);
        Assert.Equal(new[] { "download" }, _commands.Calls);
    }

    [Fact]
    public async Task An_install_failure_shows_its_message_and_keeps_the_offer()
    {
        _commands.Status = Status(1, UpdatePhase.Downloaded, canInstall: true);
        using var controller = Make();
        controller.Start();
        _commands.Install = () => Task.FromException<UpdateStatus>(new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallReplaceFailed));

        await controller.ConfirmAsync();

        Assert.Equal(UpdateMessages.InstallReplaceFailed, controller.State.Error);
        Assert.Null(controller.State.Pending);

        // Lo stato di errore del servizio non cancella il messaggio ne' l'offerta.
        _commands.Emit(Status(2, UpdatePhase.Error));
        Assert.Equal(UpdateMessages.InstallReplaceFailed, controller.State.Error);
        Assert.Equal("1.1.0", controller.State.Version);
    }

    [Fact]
    public async Task An_unexpected_exception_shows_the_generic_message()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();
        _commands.Download = () => Task.FromException<UpdateStatus>(new InvalidOperationException("internal detail"));

        await controller.ConfirmAsync();

        Assert.Equal(UpdateMessages.PromptFailed, controller.State.Error);
        Assert.Null(controller.State.Pending);
    }

    [Theory]
    [InlineData("1.1.0", false)]
    [InlineData("1.2.0", true)]
    public async Task Confirm_does_not_install_a_download_that_is_not_ready(string downloadedVersion, bool canInstall)
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();
        _commands.Download = () => Task.FromResult(Status(2, UpdatePhase.Downloaded, version: downloadedVersion, canInstall: canInstall));

        await controller.ConfirmAsync();

        Assert.Equal(new[] { "download" }, _commands.Calls);
        Assert.Equal(UpdateMessages.PromptNotReady, controller.State.Error);
        Assert.Null(controller.State.Pending);
    }

    [Fact]
    public async Task While_pending_the_offer_is_locked()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();
        var download = new TaskCompletionSource<UpdateStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Download = () => download.Task;
        _commands.Install = () => Task.FromResult(Status(4, UpdatePhase.Installing));

        var confirm = controller.ConfirmAsync();
        _commands.Emit(Status(2, UpdatePhase.UpToDate));
        controller.Dismiss();
        await controller.ConfirmAsync();

        Assert.Equal("1.1.0", controller.State.Version);
        Assert.Equal(UpdatePromptPending.Downloading, controller.State.Pending);
        Assert.Equal(new[] { "download" }, _commands.Calls);

        download.SetResult(Status(3, UpdatePhase.Downloaded, canInstall: true));
        await confirm;

        Assert.Equal(new[] { "download", "install" }, _commands.Calls);
        Assert.Equal(UpdatePromptPending.Installing, controller.State.Pending);
    }

    [Fact]
    public async Task The_error_is_cleared_when_the_offered_version_changes()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        controller.Start();
        _commands.Download = () => throw new UpdateException("UPDATES_ACCESS", "negato");
        await controller.ConfirmAsync();
        Assert.Equal("negato", controller.State.Error);

        _commands.Emit(Status(2, UpdatePhase.Available, version: "1.2.0", canDownload: true));

        Assert.Equal("1.2.0", controller.State.Version);
        Assert.Equal("", controller.State.Error);
    }

    [Fact]
    public void Dispose_unsubscribes_and_silences_the_controller()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        var controller = Make();
        controller.Start();
        var events = _states.Count;

        controller.Dispose();
        controller.Dispose();
        _commands.Emit(Status(2, UpdatePhase.UpToDate));
        controller.Dismiss();

        Assert.Equal(0, _commands.Subscribers);
        Assert.Equal(events, _states.Count);
        Assert.Equal(1, controller.State.Status?.Revision);
    }

    [Fact]
    public void Start_after_dispose_does_not_subscribe()
    {
        var controller = Make();
        controller.Dispose();

        controller.Start();

        Assert.Equal(0, _commands.Subscribers);
        Assert.Same(UpdatePromptState.Empty, controller.State);
    }

    [Fact]
    public void Start_tolerates_a_failing_status_read()
    {
        _commands.StatusFailure = new InvalidOperationException("not ready");
        using var controller = Make();

        controller.Start();

        Assert.Same(UpdatePromptState.Empty, controller.State);
        Assert.Equal(1, _commands.Subscribers);
    }

    [Fact]
    public void Handlers_may_read_the_state_and_a_failing_handler_does_not_stop_the_others()
    {
        _commands.Status = Status(1, UpdatePhase.Available, canDownload: true);
        using var controller = Make();
        string? seen = null;
        controller.Changed += _ => throw new InvalidOperationException("ui");
        controller.Changed += _ => seen = controller.State.Version;

        controller.Start();

        Assert.Equal("1.1.0", seen);
    }

    private UpdatePromptController Make()
    {
        var controller = new UpdatePromptController(_commands);
        controller.Changed += state => { lock (_states) _states.Add(state); };
        return controller;
    }

    private static UpdateStatus Status(long revision, UpdatePhase phase, string? version = "1.1.0", bool canDownload = false,
        bool canInstall = false, bool autoCheck = true, InstallationKind installation = InstallationKind.Supported) =>
        new(revision, phase, "1.0.0", installation, PackageVariant.FrameworkDependent, UpdateSource.Repository, UpdateSource.RepositoryUrl,
            autoCheck, null, UpdateAuthSource.GitHubApp, "octocat",
            version is null
                ? null
                : new UpdateRelease(version, "v" + version, "2026-09-01T10:00:00Z", $"{UpdateSource.RepositoryUrl}/releases/tag/v{version}", "",
                    UpdateSource.AssetName(version, PackageVariant.FrameworkDependent), 1024, ChecksumSource.Sha256Sums),
            null, canDownload, canInstall, "", null);

    private sealed class FakeCommands : IUpdateCommands
    {
        private UpdateStatus _status = Status(0, UpdatePhase.Idle, version: null);

        public UpdateStatus Status
        {
            get => StatusFailure is not null ? throw StatusFailure : _status;
            set => _status = value;
        }

        public Exception? StatusFailure { get; set; }
        public event Action<UpdateStatus>? Changed;
        public int Subscribers => Changed?.GetInvocationList().Length ?? 0;
        public List<string> Calls { get; } = [];
        public Func<Task<UpdateStatus>> Download { get; set; } = () => throw new InvalidOperationException("download not configured");
        public Func<Task<UpdateStatus>> Install { get; set; } = () => throw new InvalidOperationException("install not configured");

        public void Emit(UpdateStatus status)
        {
            _status = status;
            Changed?.Invoke(status);
        }

        public Task<UpdateStatus> DownloadAsync()
        {
            Calls.Add("download");
            return Download();
        }

        public Task<UpdateStatus> InstallAsync()
        {
            Calls.Add("install");
            return Install();
        }
    }
}
