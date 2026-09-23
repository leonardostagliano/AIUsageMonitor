using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AIUsageMonitor.Core.Updates;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class GitCredentialManagerLoginTests
{
    private const string Token = "gho_fake_token_123";
    private const string Account = "octocat";
    private const string ValidOutput = "protocol=https\nhost=github.com\nusername=octocat\npassword=gho_fake_token_123\n";

    // Percorsi finti ma assoluti su ogni sistema: il filesystem e' sostituito dalla sonda fileExists.
    private static readonly string FakeRoot = Path.Combine(Path.GetTempPath(), "aium-fake-install");
    private static readonly string GitDir = Path.Combine(FakeRoot, "Git", "cmd");
    private static readonly string GitExe = Path.Combine(GitDir, "git.exe");
    private static readonly string GcmExe = Path.Combine(GitDir, GitCredentialManagerLogin.ManagerFileName);
    private static readonly string OtherDir = Path.Combine(FakeRoot, "other", "bin");

    // ---- ParseCredential ----

    [Fact]
    public void Parse_accepts_a_complete_https_github_credential()
    {
        var result = GitCredentialManagerLogin.ParseCredential(Encoding.UTF8.GetBytes(ValidOutput));

        Assert.True(result.Ok);
        Assert.Null(result.Failure);
        Assert.Equal(Token, result.Token);
        Assert.Equal(Account, result.Account);
        Assert.DoesNotContain(Token, result.ToString());
    }

    [Fact]
    public void Parse_accepts_crlf_a_bom_and_a_response_without_host_or_protocol()
    {
        var crlf = GitCredentialManagerLogin.ParseCredential(Encoding.UTF8.GetBytes(ValidOutput.Replace("\n", "\r\n")));
        var bom = GitCredentialManagerLogin.ParseCredential([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(ValidOutput)]);
        var bare = GitCredentialManagerLogin.ParseCredential("username=octocat\npassword=gho_fake_token_123"u8);
        var emptyPath = GitCredentialManagerLogin.ParseCredential(Encoding.UTF8.GetBytes("path=\n" + ValidOutput));

        foreach (var result in new[] { crlf, bom, bare, emptyPath })
        {
            Assert.True(result.Ok);
            Assert.Equal(Token, result.Token);
            Assert.Equal(Account, result.Account);
        }
    }

    [Fact]
    public void Parse_ignores_lines_without_a_key_and_keeps_the_last_value()
    {
        var result = GitCredentialManagerLogin.ParseCredential(
            "=ignored\nnoequals\nusername=first\npassword=gho_first\nusername=octocat\npassword=a=b\n"u8);

        Assert.True(result.Ok);
        Assert.Equal("a=b", result.Token);
        Assert.Equal(Account, result.Account);
    }

    [Theory]
    [InlineData("protocol=https\nhost=github.example.com\nusername=octocat\npassword=gho_token\n")]
    [InlineData("protocol=https\nhost=GitHub.com\nusername=octocat\npassword=gho_token\n")]
    [InlineData("protocol=http\nhost=github.com\nusername=octocat\npassword=gho_token\n")]
    [InlineData("protocol=https\nhost=github.com\npath=some/repo.git\nusername=octocat\npassword=gho_token\n")]
    [InlineData("protocol=https\nhost=\nusername=octocat\npassword=gho_token\n")]
    public void Parse_rejects_another_host_protocol_or_a_path_scoped_credential(string output)
    {
        var result = GitCredentialManagerLogin.ParseCredential(Encoding.UTF8.GetBytes(output));

        Assert.False(result.Ok);
        Assert.Equal(CredentialParseFailure.InvalidScope, result.Failure);
        Assert.Null(result.Token);
    }

    [Theory]
    [InlineData("protocol=https\nhost=github.com\nusername=octocat\n", CredentialParseFailure.MissingToken)]
    [InlineData("protocol=https\nhost=github.com\nusername=octocat\npassword=\n", CredentialParseFailure.MissingToken)]
    [InlineData("protocol=https\nhost=github.com\nusername=octocat\npassword=gho token\n", CredentialParseFailure.MissingToken)]
    [InlineData("protocol=https\nhost=github.com\nusername=octocat\npassword=gho\u0007token\n", CredentialParseFailure.MissingToken)]
    [InlineData("protocol=https\nhost=github.com\npassword=gho_token\n", CredentialParseFailure.MissingAccount)]
    [InlineData("protocol=https\nhost=github.com\nusername=octo cat\npassword=gho_token\n", CredentialParseFailure.MissingAccount)]
    [InlineData("protocol=https\nhost=github.com\nusername=octo.cat\npassword=gho_token\n", CredentialParseFailure.MissingAccount)]
    [InlineData("", CredentialParseFailure.MissingToken)]
    public void Parse_requires_a_valid_token_then_a_valid_account(string output, CredentialParseFailure expected)
    {
        var result = GitCredentialManagerLogin.ParseCredential(Encoding.UTF8.GetBytes(output));

        Assert.Equal(expected, result.Failure);
        Assert.Null(result.Token);
        Assert.Null(result.Account);
    }

    // ---- OAuthEnvironment ----

    [Fact]
    public void OAuth_environment_scrubs_tracing_injected_config_and_desktop_variables_in_any_case()
    {
        var input = new Dictionary<string, string?>
        {
            ["git_trace"] = "1", ["GIT_TRACE2_EVENT"] = "/tmp/trace", ["Gcm_Trace"] = "1", ["GCM_TRACE_SECRETS"] = "1",
            ["GIT_CURL_VERBOSE"] = "1", ["GIT_CONFIG_COUNT"] = "1", ["git_config_key_0"] = "credential.helper",
            ["GIT_CONFIG_VALUE_0"] = "store", ["GIT_CONFIG_PARAMETERS"] = "'credential.helper=store'",
            ["DESKTOP_USERNAME"] = "someone", ["desktop_endpoint"] = "https://api.github.com", ["GIT_DIR"] = "/repo/.git",
            ["git_common_dir"] = "/repo/.git", ["GIT_WORK_TREE"] = "/repo", ["GIT_INDEX_FILE"] = "/repo/index",
            ["GIT_EXEC_PATH"] = "/evil", ["gcm_namespace"] = "shared", ["GCM_CREDENTIAL_STORE"] = "plaintext",
            ["gcm_provider"] = "generic", ["GCM_GITHUB_AUTHMODES"] = "pat", ["Gcm_Debug"] = "1",
            ["Path"] = "C:\\Windows", ["USERPROFILE"] = "C:\\Users\\me", ["GIT_DIRECTORY_HINT"] = "kept", ["GCM_DEBUGGER"] = "kept",
            ["GIT_ASKPASS"] = "askpass.exe", ["gcm_interactive"] = "never"
        };

        var env = GitCredentialManagerLogin.OAuthEnvironment(input);

        var expected = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_ASKPASS"] = "", ["SSH_ASKPASS"] = "", ["GCM_INTERACTIVE"] = "1",
            ["GCM_GUI_PROMPT"] = "1", ["GCM_PROVIDER"] = "github", ["GCM_GITHUB_AUTHMODES"] = "browser", ["GCM_TRACE"] = "0",
            ["GCM_TRACE_SECRETS"] = "0", ["GCM_TRACE_MSAUTH"] = "0", ["GCM_DEBUG"] = "0", ["GCM_CREDENTIAL_STORE"] = "wincredman",
            ["Path"] = "C:\\Windows", ["USERPROFILE"] = "C:\\Users\\me", ["GIT_DIRECTORY_HINT"] = "kept", ["GCM_DEBUGGER"] = "kept"
        };
        foreach (var (key, value) in expected)
        {
            // Una sola chiave per nome, qualunque sia la forma maiuscola/minuscola dell'originale.
            var matches = env.Where(pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.Single(matches);
            Assert.Equal(value, matches[0].Value);
        }
        var nameSpace = Assert.Single(env, pair => string.Equals(pair.Key, "GCM_NAMESPACE", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith(GitCredentialManagerLogin.NamespacePrefix, nameSpace.Value);
        Assert.True(Guid.TryParse(nameSpace.Value![GitCredentialManagerLogin.NamespacePrefix.Length..], out _));
        Assert.Equal(expected.Count + 1, env.Count);
    }

    [Fact]
    public void Each_login_uses_a_fresh_gcm_namespace()
    {
        var first = GitCredentialManagerLogin.OAuthEnvironment(new Dictionary<string, string?>())["GCM_NAMESPACE"];
        var second = GitCredentialManagerLogin.OAuthEnvironment(new Dictionary<string, string?>())["GCM_NAMESPACE"];

        Assert.NotEqual(first, second);
    }

    // ---- FindGitExe / CredentialManagerPath ----

    [Fact]
    public void Git_on_path_wins_over_the_standard_installation_folders()
    {
        var files = Files(Path.Combine(OtherDir, "git.exe"), GitExe, Path.Combine(FakeRoot, "Program Files", "Git", "cmd", "git.exe"));
        var env = Env(("PATH", Join(OtherDir, GitDir)), ("ProgramFiles", Path.Combine(FakeRoot, "Program Files")));

        Assert.Equal(Path.Combine(OtherDir, "git.exe"), GitCredentialManagerLogin.FindGitExe(env, files.Contains));
    }

    [Fact]
    public void Path_entries_are_trimmed_and_unquoted_and_the_variable_name_is_case_insensitive()
    {
        var files = Files(GitExe);
        var env = Env(("Path", $"  \"{OtherDir}\"  {Path.PathSeparator}{Path.PathSeparator} \"{GitDir}\" "));

        Assert.Equal(GitExe, GitCredentialManagerLogin.FindGitExe(env, files.Contains));
    }

    [Fact]
    public void Standard_installation_folders_are_tried_in_order()
    {
        var w6432 = Path.Combine(FakeRoot, "W6432");
        var programFiles = Path.Combine(FakeRoot, "ProgramFiles");
        var x86 = Path.Combine(FakeRoot, "x86");
        var local = Path.Combine(FakeRoot, "Local");
        var env = Env(("PATH", OtherDir), ("ProgramW6432", w6432), ("ProgramFiles", programFiles), ("ProgramFiles(x86)", x86),
            ("LOCALAPPDATA", local));
        var candidates = new[]
        {
            Path.Combine(w6432, "Git", "cmd", "git.exe"),
            Path.Combine(programFiles, "Git", "cmd", "git.exe"),
            Path.Combine(x86, "Git", "cmd", "git.exe"),
            Path.Combine(local, "Programs", "Git", "cmd", "git.exe")
        };

        for (var i = 0; i < candidates.Length; i++)
        {
            var files = Files(candidates[i..]);
            Assert.Equal(candidates[i], GitCredentialManagerLogin.FindGitExe(env, files.Contains));
        }
        Assert.Null(GitCredentialManagerLogin.FindGitExe(env, Files().Contains));
    }

    [Fact]
    public void Relative_candidates_and_non_exe_paths_are_never_probed()
    {
        var probed = new List<string>();
        var env = Env(("PATH", Join(".", "bin", "")), ("ProgramFiles", "relative"), ("LOCALAPPDATA", ""));

        var found = GitCredentialManagerLogin.FindGitExe(env, path =>
        {
            probed.Add(path);
            return true;
        });

        Assert.Null(found);
        Assert.Empty(probed);
        Assert.Null(GitCredentialManagerLogin.CredentialManagerPath("git", _ => true));
        Assert.Null(GitCredentialManagerLogin.CredentialManagerPath(Path.Combine(GitDir, "git"), _ => true));
        Assert.Null(GitCredentialManagerLogin.CredentialManagerPath(null, _ => true));
    }

    [Fact]
    public void Duplicate_candidates_are_probed_once_and_a_throwing_probe_counts_as_missing()
    {
        var probed = new List<string>();
        var env = Env(("PATH", Join(GitDir, GitDir.ToUpperInvariant(), OtherDir)));

        var found = GitCredentialManagerLogin.FindGitExe(env, path =>
        {
            probed.Add(path);
            if (path == GitExe) throw new IOException("probe failed");
            return path == Path.Combine(OtherDir, "git.exe");
        });

        Assert.Equal(Path.Combine(OtherDir, "git.exe"), found);
        Assert.Equal([GitExe, Path.Combine(OtherDir, "git.exe")], probed);
    }

    [Fact]
    public void Credential_manager_is_searched_only_inside_the_same_git_installation()
    {
        var mingwBin = Path.Combine(FakeRoot, "Git", "mingw64", "bin", GitCredentialManagerLogin.ManagerFileName);
        var libexec = Path.Combine(FakeRoot, "Git", "mingw64", "libexec", "git-core", GitCredentialManagerLogin.ManagerFileName);

        Assert.Equal(GcmExe, GitCredentialManagerLogin.CredentialManagerPath(GitExe, Files(GcmExe, mingwBin, libexec).Contains));
        Assert.Equal(mingwBin, GitCredentialManagerLogin.CredentialManagerPath(GitExe, Files(mingwBin, libexec).Contains));
        Assert.Equal(libexec, GitCredentialManagerLogin.CredentialManagerPath(GitExe, Files(libexec).Contains));
        Assert.Null(GitCredentialManagerLogin.CredentialManagerPath(GitExe, Files(Path.Combine(OtherDir, GitCredentialManagerLogin.ManagerFileName)).Contains));
    }

    // ---- AuthenticateAsync ----

    [Fact]
    public async Task Authenticate_runs_gcm_get_saves_the_session_and_returns_the_account()
    {
        using var dir = new TempDir();
        var workingDirectory = Path.Combine(dir.Path, "AIUsageMonitor");
        var output = Encoding.UTF8.GetBytes(ValidOutput);
        var runner = new FakeRunner(_ => new CredentialProcessResult(CredentialProcessOutcome.Exited, 0, output));
        var store = new FakeStore();
        var events = store.Events;
        var login = Login(store, runner, workingDirectory: workingDirectory);

        var credential = await login.AuthenticateAsync(() => events.Add("ready"), CancellationToken.None);

        Assert.Equal(UpdateCredential.Connected(Token, Account), credential);
        Assert.Equal(["ready", $"save:{Token}:{Account}"], events);
        var request = Assert.Single(runner.Requests);
        Assert.Equal(GcmExe, request.FileName);
        Assert.Equal(["get"], request.Arguments);
        Assert.Equal("protocol=https\nhost=github.com\n\n", request.StandardInput);
        Assert.Equal(32 * 1024, request.MaxStandardOutputBytes);
        Assert.Equal(TimeSpan.FromMinutes(3), request.Timeout);
        Assert.Equal(workingDirectory, request.WorkingDirectory);
        Assert.True(Directory.Exists(workingDirectory));
        Assert.Equal("github", request.Environment["GCM_PROVIDER"]);
        Assert.Equal("browser", request.Environment["GCM_GITHUB_AUTHMODES"]);
        Assert.StartsWith(GitCredentialManagerLogin.NamespacePrefix, request.Environment["GCM_NAMESPACE"]);
        Assert.False(request.Environment.ContainsKey("GIT_TRACE"));
        // Lo stdout con il token viene azzerato dopo il parsing.
        Assert.All(output, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Authenticate_with_the_real_store_leaves_a_readable_encrypted_session()
    {
        using var dir = new TempDir();
        var protector = new XorProtector();
        var store = new UpdateCredentialStore(Path.Combine(dir.Path, "updates-auth.json"), protector);
        var runner = new FakeRunner(_ => new CredentialProcessResult(CredentialProcessOutcome.Exited, 0, Encoding.UTF8.GetBytes(ValidOutput)));
        var login = new GitCredentialManagerLogin(store, protector, runner, dir.Path, Env(("PATH", GitDir)), Files(GitExe, GcmExe).Contains, isWindows: true);

        await login.AuthenticateAsync(null, CancellationToken.None);

        Assert.Equal(UpdateCredential.Connected(Token, Account), store.Read());
        Assert.DoesNotContain(Token, File.ReadAllText(Path.Combine(dir.Path, "updates-auth.json")));
    }

    [Fact]
    public async Task Authenticate_fails_fast_before_running_anything()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var runner = new FakeRunner(_ => throw new InvalidOperationException("must not run"));

        await AssertFails("UPDATES_AUTH_CANCELLED", UpdateMessages.AuthCancelled, Login(runner: runner), cancelled.Token);
        await AssertFails("UPDATES_AUTH_UNAVAILABLE", UpdateMessages.AuthPlatform, Login(runner: runner, isWindows: false));
        await AssertFails("UPDATES_AUTH_SAVE", UpdateMessages.AuthStorageUnavailable, Login(runner: runner, protector: new XorProtector { Available = false }));
        await AssertFails("UPDATES_AUTH_UNAVAILABLE", UpdateMessages.GcmMissing, Login(runner: runner, files: Files(GitExe)));
        await AssertFails("UPDATES_AUTH_UNAVAILABLE", UpdateMessages.GcmMissing, Login(runner: runner, files: Files(GcmExe)));
        Assert.Empty(runner.Requests);
    }

    public static TheoryData<CredentialProcessOutcome, int?, string, string, string> ProcessFailures() => new()
    {
        { CredentialProcessOutcome.Cancelled, null, "", "UPDATES_AUTH_CANCELLED", UpdateMessages.AuthCancelled },
        { CredentialProcessOutcome.TimedOut, null, "", "UPDATES_AUTH_TIMEOUT", UpdateMessages.AuthTimeout },
        { CredentialProcessOutcome.SpawnFailed, null, "", "UPDATES_AUTH_UNAVAILABLE", UpdateMessages.AuthSpawnFailed },
        { CredentialProcessOutcome.Exited, 3, ValidOutput, "UPDATES_AUTH_PROCESS", UpdateMessages.AuthProcessFailed(3) },
        { CredentialProcessOutcome.Exited, null, ValidOutput, "UPDATES_AUTH_PROCESS", UpdateMessages.AuthProcessFailed(null) },
        { CredentialProcessOutcome.Oversize, null, "", "UPDATES_AUTH_CREDENTIAL", UpdateMessages.AuthCredentialInvalid(UpdateMessages.DetailOversize) },
        {
            CredentialProcessOutcome.Exited, 0, "protocol=https\nhost=github.com\nusername=octocat\n",
            "UPDATES_AUTH_CREDENTIAL", UpdateMessages.AuthCredentialInvalid(UpdateMessages.DetailMissingToken)
        },
        {
            CredentialProcessOutcome.Exited, 0, "protocol=https\nhost=github.com\npassword=gho_fake_token_123\n",
            "UPDATES_AUTH_CREDENTIAL", UpdateMessages.AuthCredentialInvalid(UpdateMessages.DetailMissingAccount)
        },
        {
            CredentialProcessOutcome.Exited, 0, "protocol=https\nhost=gitlab.com\nusername=octocat\npassword=gho_fake_token_123\n",
            "UPDATES_AUTH_CREDENTIAL", UpdateMessages.AuthCredentialInvalid(UpdateMessages.DetailInvalidScope)
        }
    };

    [Theory]
    [MemberData(nameof(ProcessFailures))]
    public async Task Authenticate_maps_every_process_outcome(CredentialProcessOutcome outcome, int? exitCode, string output, string code, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(output);
        var store = new FakeStore();
        var ready = 0;
        var login = Login(store, new FakeRunner(_ => new CredentialProcessResult(outcome, exitCode, bytes)));

        var error = await Assert.ThrowsAsync<UpdateException>(() => login.AuthenticateAsync(() => ready++, CancellationToken.None));

        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
        Assert.DoesNotContain(Token, error.Message);
        Assert.DoesNotContain(Account, error.Message);
        Assert.Equal(0, ready);
        Assert.Empty(store.Events);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Cancelling_while_gcm_runs_reports_cancelled_even_if_it_returned_a_credential()
    {
        using var cts = new CancellationTokenSource();
        var store = new FakeStore();
        var bytes = Encoding.UTF8.GetBytes(ValidOutput);
        var login = Login(store, new FakeRunner(_ =>
        {
            cts.Cancel();
            return new CredentialProcessResult(CredentialProcessOutcome.Exited, 0, bytes);
        }));

        var error = await Assert.ThrowsAsync<UpdateException>(() => login.AuthenticateAsync(null, cts.Token));

        Assert.Equal("UPDATES_AUTH_CANCELLED", error.Code);
        Assert.Empty(store.Events);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task A_runner_that_waits_for_cancellation_or_throws_is_mapped()
    {
        using var cts = new CancellationTokenSource();
        var waiting = Login(runner: new FakeRunner(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new CredentialProcessResult(CredentialProcessOutcome.Exited, 0, []);
        }));
        var pending = waiting.AuthenticateAsync(null, cts.Token);
        cts.CancelAfter(50);
        Assert.Equal("UPDATES_AUTH_CANCELLED", (await Assert.ThrowsAsync<UpdateException>(() => pending)).Code);

        var throwing = Login(runner: new FakeRunner(_ => throw new InvalidOperationException("boom")));
        var error = await Assert.ThrowsAsync<UpdateException>(() => throwing.AuthenticateAsync(null, CancellationToken.None));
        Assert.Equal("UPDATES_AUTH_UNAVAILABLE", error.Code);
        Assert.Equal(UpdateMessages.AuthSpawnFailed, error.Message);
    }

    [Fact]
    public async Task Cancelling_after_the_credential_is_ready_does_not_save_it()
    {
        using var cts = new CancellationTokenSource();
        var store = new FakeStore();
        var login = Login(store, new FakeRunner(_ => new CredentialProcessResult(CredentialProcessOutcome.Exited, 0, Encoding.UTF8.GetBytes(ValidOutput))));

        var error = await Assert.ThrowsAsync<UpdateException>(() => login.AuthenticateAsync(() => cts.Cancel(), cts.Token));

        Assert.Equal("UPDATES_AUTH_CANCELLED", error.Code);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task Store_failures_surface_as_auth_save_errors()
    {
        var runner = new FakeRunner(_ => new CredentialProcessResult(CredentialProcessOutcome.Exited, 0, Encoding.UTF8.GetBytes(ValidOutput)));

        var fromStore = await Assert.ThrowsAsync<UpdateException>(() =>
            Login(new FakeStore { Throw = new UpdateException("UPDATES_AUTH_SAVE", UpdateMessages.AuthStorageUnavailable) }, runner)
                .AuthenticateAsync(null, CancellationToken.None));
        var unexpected = await Assert.ThrowsAsync<UpdateException>(() =>
            Login(new FakeStore { Throw = new IOException("disk full") }, runner).AuthenticateAsync(null, CancellationToken.None));

        Assert.Equal(UpdateMessages.AuthStorageUnavailable, fromStore.Message);
        Assert.Equal("UPDATES_AUTH_SAVE", unexpected.Code);
        Assert.Equal(UpdateMessages.AuthSaveFailed, unexpected.Message);
    }

    // ---- CredentialProcessRunner (processi reali: /bin/sh, saltati dove non c'e') ----

    private static bool HasShell => !OperatingSystem.IsWindows() && File.Exists("/bin/sh");

    [Fact]
    public async Task Runner_writes_stdin_closes_it_and_returns_stdout_and_exit_code()
    {
        if (!HasShell) return;
        using var dir = new TempDir();

        var echoed = await Run(dir.Path, "cat; exit 0", stdin: "protocol=https\nhost=github.com\n\n");
        var failed = await Run(dir.Path, "exit 3");

        Assert.Equal(CredentialProcessOutcome.Exited, echoed.Outcome);
        Assert.Equal(0, echoed.ExitCode);
        Assert.Equal("protocol=https\nhost=github.com\n\n", Encoding.UTF8.GetString(echoed.StandardOutput));
        Assert.Equal(CredentialProcessOutcome.Exited, failed.Outcome);
        Assert.Equal(3, failed.ExitCode);
    }

    [Fact]
    public async Task Runner_replaces_the_environment_and_uses_the_working_directory()
    {
        if (!HasShell) return;
        using var dir = new TempDir();
        dir.File("marker.txt", "cwd-ok");

        var result = await Run(dir.Path, "printf '%s|%s|' \"$FOO\" \"$HOME\"; cat marker.txt",
            environment: new Dictionary<string, string?> { ["FOO"] = "bar", ["DROPPED"] = null });

        Assert.Equal(CredentialProcessOutcome.Exited, result.Outcome);
        Assert.Equal("bar||cwd-ok", Encoding.UTF8.GetString(result.StandardOutput));
    }

    [Fact]
    public async Task Runner_drains_a_large_stderr_without_deadlocking()
    {
        if (!HasShell) return;
        using var dir = new TempDir();

        var result = await Run(dir.Path,
            "i=0; while [ $i -lt 4000 ]; do echo 'stderr stderr stderr stderr stderr stderr stderr stderr' >&2; i=$((i+1)); done; echo ok",
            timeout: TimeSpan.FromSeconds(30));

        Assert.Equal(CredentialProcessOutcome.Exited, result.Outcome);
        Assert.Equal("ok\n", Encoding.UTF8.GetString(result.StandardOutput));
    }

    [Fact]
    public async Task Runner_accepts_stdout_up_to_the_limit_and_stops_beyond_it()
    {
        if (!HasShell) return;
        using var dir = new TempDir();

        var exact = await Run(dir.Path, "printf '0123456789abcdef'", maxOutput: 16);
        var over = await Run(dir.Path, "printf '0123456789abcdefX'; exec sleep 30", maxOutput: 16);
        var huge = await Run(dir.Path, "while :; do echo xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx; done", maxOutput: 32 * 1024);
        // Piu' blocchi del buffer iniziale ma entro il limite: tutto arriva, nell'ordine.
        var large = await Run(dir.Path, "i=0; while [ $i -lt 200 ]; do printf '%03d%096d\\n' $i 0; i=$((i+1)); done", maxOutput: 32 * 1024);

        Assert.Equal(CredentialProcessOutcome.Exited, exact.Outcome);
        Assert.Equal("0123456789abcdef", Encoding.UTF8.GetString(exact.StandardOutput));
        Assert.Equal(CredentialProcessOutcome.Exited, large.Outcome);
        var lines = Encoding.UTF8.GetString(large.StandardOutput).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(20_000, large.StandardOutput.Length);
        Assert.Equal(Enumerable.Range(0, 200).Select(i => i.ToString("000") + new string('0', 96)), lines);
        Assert.Equal(CredentialProcessOutcome.Oversize, over.Outcome);
        Assert.Empty(over.StandardOutput);
        Assert.Equal(CredentialProcessOutcome.Oversize, huge.Outcome);
    }

    [Fact]
    public async Task Runner_times_out_and_is_cancelled_without_waiting_for_the_process()
    {
        if (!HasShell) return;
        using var dir = new TempDir();
        var watch = Stopwatch.StartNew();

        var timedOut = await Run(dir.Path, "exec sleep 30", timeout: TimeSpan.FromMilliseconds(300));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var cancelled = await Run(dir.Path, "exec sleep 30", cancellationToken: cts.Token);

        Assert.Equal(CredentialProcessOutcome.TimedOut, timedOut.Outcome);
        Assert.Equal(CredentialProcessOutcome.Cancelled, cancelled.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Runner_reports_spawn_failures_and_does_not_start_when_already_cancelled()
    {
        if (!HasShell) return;
        using var dir = new TempDir();
        var runner = new CredentialProcessRunner();

        var missingExe = await runner.RunAsync(Request(Path.Combine(dir.Path, "missing", "git-credential-manager.exe"), [], dir.Path), CancellationToken.None);
        var missingCwd = await runner.RunAsync(Request("/bin/sh", ["-c", "exit 0"], Path.Combine(dir.Path, "missing")), CancellationToken.None);
        var marker = Path.Combine(dir.Path, "started");
        var alreadyCancelled = await runner.RunAsync(Request("/bin/sh", ["-c", $"touch '{marker}'"], dir.Path), new CancellationToken(canceled: true));

        Assert.Equal(CredentialProcessOutcome.SpawnFailed, missingExe.Outcome);
        Assert.Equal(CredentialProcessOutcome.SpawnFailed, missingCwd.Outcome);
        Assert.Equal(CredentialProcessOutcome.Cancelled, alreadyCancelled.Outcome);
        Assert.False(File.Exists(marker));
    }

    private static Task<CredentialProcessResult> Run(string workingDirectory, string script, string stdin = "",
        IReadOnlyDictionary<string, string?>? environment = null, int maxOutput = 4096, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var env = environment ?? new Dictionary<string, string?> { ["PATH"] = Environment.GetEnvironmentVariable("PATH") };
        if (!env.ContainsKey("PATH"))
            env = new Dictionary<string, string?>(env) { ["PATH"] = Environment.GetEnvironmentVariable("PATH") };
        var request = new CredentialProcessRequest("/bin/sh", ["-c", script], env, workingDirectory, stdin, maxOutput,
            timeout ?? TimeSpan.FromSeconds(20));
        return new CredentialProcessRunner().RunAsync(request, cancellationToken);
    }

    private static CredentialProcessRequest Request(string fileName, string[] arguments, string workingDirectory) =>
        new(fileName, arguments, new Dictionary<string, string?>(), workingDirectory, "", 4096, TimeSpan.FromSeconds(20));

    // ---- helper ----

    private static GitCredentialManagerLogin Login(FakeStore? store = null, FakeRunner? runner = null, XorProtector? protector = null,
        HashSet<string>? files = null, bool isWindows = true, string? workingDirectory = null) =>
        new(store ?? new FakeStore(), protector ?? new XorProtector(),
            runner ?? new FakeRunner(_ => new CredentialProcessResult(CredentialProcessOutcome.Exited, 0, Encoding.UTF8.GetBytes(ValidOutput))),
            workingDirectory ?? Path.Combine(Path.GetTempPath(), "aium-tests", "gcm-cwd"),
            Env(("PATH", GitDir), ("GIT_TRACE", "1")), (files ?? Files(GitExe, GcmExe)).Contains, isWindows);

    private static async Task AssertFails(string code, string message, GitCredentialManagerLogin login, CancellationToken token = default)
    {
        var error = await Assert.ThrowsAsync<UpdateException>(() => login.AuthenticateAsync(null, token));
        Assert.Equal(code, error.Code);
        Assert.Equal(message, error.Message);
    }

    private static Dictionary<string, string?> Env(params (string Key, string? Value)[] pairs) =>
        pairs.ToDictionary(pair => pair.Key, pair => pair.Value);

    private static HashSet<string> Files(params string[] paths) => new(paths, StringComparer.Ordinal);

    private static string Join(params string[] entries) => string.Join(Path.PathSeparator, entries);

    private sealed class FakeRunner : ICredentialProcessRunner
    {
        private readonly Func<CredentialProcessRequest, CancellationToken, Task<CredentialProcessResult>> _run;

        public FakeRunner(Func<CredentialProcessRequest, CredentialProcessResult> run) => _run = (request, _) => Task.FromResult(run(request));

        public FakeRunner(Func<CredentialProcessRequest, CancellationToken, Task<CredentialProcessResult>> run) => _run = run;

        public List<CredentialProcessRequest> Requests { get; } = [];

        public Task<CredentialProcessResult> RunAsync(CredentialProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _run(request, cancellationToken);
        }
    }

    private sealed class FakeStore : IUpdateCredentialStore
    {
        public List<string> Events { get; } = [];
        public Exception? Throw { get; init; }

        public UpdateCredential Read() => UpdateCredential.Missing(CredentialFailure.NotConnected);

        public void Save(string token, string account)
        {
            if (Throw is not null) throw Throw;
            Events.Add($"save:{token}:{account}");
        }

        public void Delete() => Events.Add("delete");
    }

    private sealed class XorProtector : ISecretProtector
    {
        public bool Available { get; init; } = true;
        public string CipherName => "test-xor";
        public bool IsAvailable => Available;
        public byte[] Protect(byte[] plaintext) => plaintext.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] ciphertext) => ciphertext.Select(b => (byte)(b ^ 0x5A)).ToArray();
    }
}
