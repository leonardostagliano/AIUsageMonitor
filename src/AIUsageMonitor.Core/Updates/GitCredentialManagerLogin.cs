using System.Collections;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Porting di ChessAdvisor <c>credentials.ts</c>: trova git.exe (PATH, poi le installazioni standard di Git for
/// Windows) e il Git Credential Manager della stessa installazione, lo esegue con <c>get</c> in un namespace GCM nuovo
/// e con l'ambiente ripulito, OAuth solo nel browser, e salva la credenziale restituita nello store dell'app. Non usa mai
/// PAT, account di GitHub Desktop o credential helper configurati; non chiama mai <c>store</c>.
/// </summary>
public sealed class GitCredentialManagerLogin : IGitHubLogin
{
    public static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(3);
    public const int MaxOutputBytes = 32 * 1024;

    public const string ManagerFileName = "git-credential-manager.exe";
    public const string NamespacePrefix = "aiusagemonitor-updates-auth-";

    /// <summary>Richiesta del protocollo credential helper: solo protocollo e host, mai un percorso di repository.</summary>
    public const string CredentialRequest = "protocol=https\nhost=github.com\n\n";

    // Variabili che potrebbero far tracciare segreti, puntare GCM a un altro repository/configurazione o riusare
    // credenziali di altri strumenti (GitHub Desktop). Confronto senza distinzione di maiuscole, come l'ambiente Windows.
    private static readonly string[] ScrubbedPrefixes =
    [
        "GIT_TRACE", "GCM_TRACE", "GIT_CURL_VERBOSE", "GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_", "GIT_CONFIG_VALUE_",
        "GIT_CONFIG_PARAMETERS", "DESKTOP_"
    ];

    private static readonly string[] ScrubbedNames =
    [
        "GIT_DIR", "GIT_COMMON_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_EXEC_PATH", "GCM_NAMESPACE",
        "GCM_CREDENTIAL_STORE", "GCM_PROVIDER", "GCM_GITHUB_AUTHMODES", "GCM_DEBUG"
    ];

    private static readonly (string Key, string Value)[] OAuthOverrides =
    [
        ("GIT_TERMINAL_PROMPT", "0"),
        ("GIT_ASKPASS", ""),
        ("SSH_ASKPASS", ""),
        ("GCM_INTERACTIVE", "1"),
        ("GCM_GUI_PROMPT", "1"),
        ("GCM_PROVIDER", "github"),
        ("GCM_GITHUB_AUTHMODES", "browser"),
        ("GCM_TRACE", "0"),
        ("GCM_TRACE_SECRETS", "0"),
        ("GCM_TRACE_MSAUTH", "0"),
        ("GCM_DEBUG", "0"),
        ("GCM_CREDENTIAL_STORE", "wincredman")
    ];

    private readonly IUpdateCredentialStore _store;
    private readonly ISecretProtector _protector;
    private readonly ICredentialProcessRunner _runner;
    private readonly string _workingDirectory;
    private readonly IReadOnlyDictionary<string, string?>? _environment;
    private readonly Func<string, bool> _fileExists;
    private readonly bool _isWindows;

    /// <param name="workingDirectory">Cartella dati dell'app, usata come cwd del processo GCM.</param>
    /// <param name="environment">Ambiente del processo corrente; null = <see cref="System.Environment.GetEnvironmentVariables()"/>.</param>
    /// <param name="fileExists">Sonda del filesystem per trovare git e GCM; null = <see cref="File.Exists"/>.</param>
    /// <param name="isWindows">null = <see cref="OperatingSystem.IsWindows"/>.</param>
    public GitCredentialManagerLogin(IUpdateCredentialStore store, ISecretProtector protector, ICredentialProcessRunner runner,
        string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null, Func<string, bool>? fileExists = null,
        bool? isWindows = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrEmpty(workingDirectory);
        _store = store;
        _protector = protector;
        _runner = runner;
        _workingDirectory = workingDirectory;
        _environment = environment;
        _fileExists = fileExists ?? File.Exists;
        _isWindows = isWindows ?? OperatingSystem.IsWindows();
    }

    public async Task<UpdateCredential> AuthenticateAsync(Action? credentialReady, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) throw Cancelled();
        if (!_isWindows) throw new UpdateException("UPDATES_AUTH_UNAVAILABLE", UpdateMessages.AuthPlatform);
        if (!ProtectorAvailable()) throw new UpdateException("UPDATES_AUTH_SAVE", UpdateMessages.AuthStorageUnavailable);

        var environment = _environment ?? CurrentEnvironment();
        var manager = CredentialManagerPath(FindGitExe(environment, _fileExists), _fileExists);
        if (manager is null) throw new UpdateException("UPDATES_AUTH_UNAVAILABLE", UpdateMessages.GcmMissing);

        // La cwd deve esistere o Windows rifiuta di avviare il processo; al primo avvio la cartella dati puo' mancare.
        try
        {
            Directory.CreateDirectory(_workingDirectory);
        }
        catch (Exception)
        {
            // Se non si puo' creare, l'avvio fallira' con AuthSpawnFailed.
        }

        var request = new CredentialProcessRequest(manager, ["get"], OAuthEnvironment(environment), _workingDirectory,
            CredentialRequest, MaxOutputBytes, LoginTimeout);
        var result = await RunAsync(request, cancellationToken).ConfigureAwait(false);

        ParsedCredential credential;
        try
        {
            if (cancellationToken.IsCancellationRequested || result.Outcome == CredentialProcessOutcome.Cancelled) throw Cancelled();
            credential = result.Outcome switch
            {
                CredentialProcessOutcome.TimedOut => throw new UpdateException("UPDATES_AUTH_TIMEOUT", UpdateMessages.AuthTimeout),
                CredentialProcessOutcome.SpawnFailed => throw new UpdateException("UPDATES_AUTH_UNAVAILABLE", UpdateMessages.AuthSpawnFailed),
                CredentialProcessOutcome.Oversize => throw CredentialInvalid(UpdateMessages.DetailOversize),
                CredentialProcessOutcome.Exited when result.ExitCode == 0 => ParseCredential(result.StandardOutput ?? []),
                _ => throw new UpdateException("UPDATES_AUTH_PROCESS", UpdateMessages.AuthProcessFailed(result.ExitCode))
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(result.StandardOutput);
        }

        if (credential.Failure is { } failure)
        {
            throw CredentialInvalid(failure switch
            {
                CredentialParseFailure.InvalidScope => UpdateMessages.DetailInvalidScope,
                CredentialParseFailure.MissingToken => UpdateMessages.DetailMissingToken,
                _ => UpdateMessages.DetailMissingAccount
            });
        }

        credentialReady?.Invoke();
        if (cancellationToken.IsCancellationRequested) throw Cancelled();
        try
        {
            _store.Save(credential.Token!, credential.Account!);
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new UpdateException("UPDATES_AUTH_SAVE", UpdateMessages.AuthSaveFailed);
        }
        return UpdateCredential.Connected(credential.Token!, credential.Account!);
    }

    /// <summary>
    /// ChessAdvisor non ha un percorso di git configurabile: prima il PATH, poi le cartelle standard di Git for Windows,
    /// perche' un processo desktop puo' aver ereditato un PATH vecchio. Solo percorsi assoluti che finiscono in .exe.
    /// </summary>
    public static string? FindGitExe(IReadOnlyDictionary<string, string?> environment, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fileExists);
        var candidates = new List<string>();
        foreach (var raw in (Lookup(environment, "PATH") ?? "").Split(Path.PathSeparator))
        {
            var entry = raw.Trim();
            if (entry.StartsWith('"')) entry = entry[1..];
            if (entry.EndsWith('"')) entry = entry[..^1];
            if (entry.Length > 0) candidates.Add(Path.Combine(entry, "git.exe"));
        }
        foreach (var root in new[] { "ProgramW6432", "ProgramFiles", "ProgramFiles(x86)" })
        {
            var value = Lookup(environment, root);
            if (!string.IsNullOrEmpty(value)) candidates.Add(Path.Combine(value, "Git", "cmd", "git.exe"));
        }
        var localAppData = Lookup(environment, "LOCALAPPDATA");
        if (!string.IsNullOrEmpty(localAppData)) candidates.Add(Path.Combine(localAppData, "Programs", "Git", "cmd", "git.exe"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in candidates)
        {
            if (!seen.Add(path) || !IsAbsoluteExe(path)) continue;
            if (Probe(fileExists, path)) return path;
        }
        return null;
    }

    /// <summary>Solo il GCM che appartiene all'installazione di Git trovata: accanto a git.exe o in <c>..\mingw64</c>.</summary>
    public static string? CredentialManagerPath(string? gitPath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (gitPath is null || !IsAbsoluteExe(gitPath)) return null;
        var directory = Path.GetDirectoryName(gitPath);
        if (string.IsNullOrEmpty(directory)) return null;
        string[] candidates;
        try
        {
            candidates =
            [
                Path.Combine(directory, ManagerFileName),
                Path.GetFullPath(Path.Combine(directory, "..", "mingw64", "bin", ManagerFileName)),
                Path.GetFullPath(Path.Combine(directory, "..", "mingw64", "libexec", "git-core", ManagerFileName))
            ];
        }
        catch (Exception)
        {
            return null;
        }
        return candidates.FirstOrDefault(path => Probe(fileExists, path));
    }

    /// <summary>
    /// Ambiente del processo GCM: quello corrente senza tracce, configurazioni iniettate e variabili di GitHub Desktop,
    /// piu' le impostazioni che forzano OAuth nel browser in un namespace GCM mai usato (nessun account in cache da
    /// scegliere). Con GitHub il <c>get</c> nel browser restituisce il token senza salvarlo e l'app non chiama mai
    /// <c>store</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> OAuthEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in environment)
        {
            if (string.IsNullOrEmpty(key) || IsScrubbed(key)) continue;
            result[key] = value;
        }
        foreach (var (key, value) in OAuthOverrides) result[key] = value;
        result["GCM_NAMESPACE"] = NamespacePrefix + Guid.NewGuid().ToString("D");
        return result;
    }

    /// <summary>
    /// Risposta del protocollo credential helper (<c>key=value</c> per riga, LF o CRLF, BOM facoltativo). Rifiuta host
    /// diversi da github.com, protocolli diversi da https e credenziali legate a un percorso; poi richiede un token e un
    /// nome account validi. Non modifica <paramref name="output"/>: lo azzera il chiamante.
    /// </summary>
    public static ParsedCredential ParseCredential(ReadOnlySpan<byte> output)
    {
        var text = Encoding.UTF8.GetString(output);
        if (text.StartsWith('﻿')) text = text[1..];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            var at = line.IndexOf('=');
            if (at > 0) fields[line[..at]] = line[(at + 1)..];
        }
        if ((fields.TryGetValue("host", out var host) && host != "github.com")
            || (fields.TryGetValue("protocol", out var protocol) && protocol != "https")
            || (fields.TryGetValue("path", out var path) && path.Length > 0))
            return ParsedCredential.Failed(CredentialParseFailure.InvalidScope);
        fields.TryGetValue("password", out var token);
        fields.TryGetValue("username", out var account);
        if (!UpdateCredentialRules.IsValidToken(token)) return ParsedCredential.Failed(CredentialParseFailure.MissingToken);
        if (!UpdateCredentialRules.IsValidAccount(account)) return ParsedCredential.Failed(CredentialParseFailure.MissingAccount);
        return ParsedCredential.Succeeded(token!, account!);
    }

    private async Task<CredentialProcessResult> RunAsync(CredentialProcessRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await _runner.RunAsync(request, cancellationToken).ConfigureAwait(false)
                ?? new CredentialProcessResult(CredentialProcessOutcome.SpawnFailed, null, []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw Cancelled();
        }
        catch (Exception)
        {
            // Il runner reale non lancia: un'eccezione qui e' un avvio non riuscito, non un errore da propagare cosi' com'e'.
            throw new UpdateException("UPDATES_AUTH_UNAVAILABLE", UpdateMessages.AuthSpawnFailed);
        }
    }

    private bool ProtectorAvailable()
    {
        try
        {
            return _protector.IsAvailable;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<string, string?> CurrentEnvironment()
    {
        var result = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key) result[key] = entry.Value as string;
        }
        return result;
    }

    /// <summary>Valore di una variabile: prima il nome esatto, poi senza distinzione di maiuscole (Windows usa "Path").</summary>
    private static string? Lookup(IReadOnlyDictionary<string, string?> environment, string name)
    {
        if (environment.TryGetValue(name, out var exact)) return exact;
        foreach (var (key, value) in environment)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

    private static bool IsScrubbed(string key)
    {
        foreach (var prefix in ScrubbedPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        foreach (var name in ScrubbedNames)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsAbsoluteExe(string path) =>
        Path.IsPathFullyQualified(path) && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool Probe(Func<string, bool> fileExists, string path)
    {
        try
        {
            return fileExists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static UpdateException Cancelled() => new("UPDATES_AUTH_CANCELLED", UpdateMessages.AuthCancelled);

    private static UpdateException CredentialInvalid(string detail) =>
        new("UPDATES_AUTH_CREDENTIAL", UpdateMessages.AuthCredentialInvalid(detail));
}

/// <summary>Perche' la risposta di GCM non e' una sessione GitHub utilizzabile.</summary>
public enum CredentialParseFailure
{
    InvalidScope,
    MissingToken,
    MissingAccount
}

/// <summary>
/// Esito di <see cref="GitCredentialManagerLogin.ParseCredential"/>. Non e' un record di proposito: il
/// <see cref="object.ToString"/> generato includerebbe il token.
/// </summary>
public sealed class ParsedCredential
{
    private ParsedCredential(string? token, string? account, CredentialParseFailure? failure)
    {
        Token = token;
        Account = account;
        Failure = failure;
    }

    public string? Token { get; }
    public string? Account { get; }
    public CredentialParseFailure? Failure { get; }
    public bool Ok => Failure is null;

    internal static ParsedCredential Succeeded(string token, string account) => new(token, account, null);
    internal static ParsedCredential Failed(CredentialParseFailure failure) => new(null, null, failure);

    public override string ToString() => Failure is { } failure ? $"ParsedCredential {{ Failure = {failure} }}" : "ParsedCredential { Ok }";
}

/// <summary>Esecuzione reale con <c>System.Diagnostics.Process</c>: finestra nascosta, stderr scartato, stdout limitato.</summary>
/// <remarks>
/// Non lancia: ogni esito e' un <see cref="CredentialProcessOutcome"/>. Su timeout, cancellazione o stdout oltre il limite
/// termina il processo avviato e ritorna subito, senza aspettare la chiusura delle pipe. Termina solo il processo avviato,
/// non i discendenti: GCM apre il browser di sistema come proprio figlio quando il browser non era gia' aperto, e
/// chiudere l'albero chiuderebbe anche il browser dell'utente (come <c>child.kill()</c> in ChessAdvisor).
/// </remarks>
public sealed class CredentialProcessRunner : ICredentialProcessRunner
{
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(24);

    public async Task<CredentialProcessResult> RunAsync(CredentialProcessRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested) return Result(CredentialProcessOutcome.Cancelled);

        using var process = new Process();
        try
        {
            process.StartInfo = StartInfo(request);
            if (!process.Start()) return Result(CredentialProcessOutcome.SpawnFailed);
        }
        catch (Exception)
        {
            // Win32Exception (eseguibile o cwd mancanti), richiesta o ambiente non validi, ecc.
            return Result(CredentialProcessOutcome.SpawnFailed);
        }

        var output = new BoundedOutput(request.MaxStandardOutputBytes);
        var reading = output.ReadAsync(process.StandardOutput.BaseStream);
        _ = DrainAsync(process.StandardError.BaseStream);
        _ = WriteInputAsync(process.StandardInput, request.StandardInput);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.Timeout != Timeout.InfiniteTimeSpan)
            limit.CancelAfter(request.Timeout < TimeSpan.Zero ? TimeSpan.Zero : request.Timeout > MaxTimeout ? MaxTimeout : request.Timeout);
        var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = limit.Token.Register(() => aborted.TrySetResult());

        // Come l'evento 'close' di Node: prima la fine di stdout, poi l'uscita del processo.
        if (await Task.WhenAny(reading, aborted.Task).ConfigureAwait(false) != reading) return Abort(process, output, cancellationToken);
        if (!await reading.ConfigureAwait(false))
        {
            Kill(process);
            output.Discard();
            return Result(CredentialProcessOutcome.Oversize);
        }
        var exited = process.WaitForExitAsync(CancellationToken.None);
        if (await Task.WhenAny(exited, aborted.Task).ConfigureAwait(false) != exited) return Abort(process, output, cancellationToken);

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        catch (Exception)
        {
            output.Discard();
            return Result(CredentialProcessOutcome.SpawnFailed);
        }
        return new CredentialProcessResult(CredentialProcessOutcome.Exited, exitCode, output.Take());
    }

    private static ProcessStartInfo StartInfo(CredentialProcessRequest request)
    {
        var info = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory,
            // Niente console per GCM; la sua eventuale interfaccia grafica (GCM_GUI_PROMPT=1) resta visibile, quindi
            // nessun WindowStyle nascosto.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        foreach (var argument in request.Arguments) info.ArgumentList.Add(argument);
        info.Environment.Clear();
        foreach (var (key, value) in request.Environment)
        {
            if (!string.IsNullOrEmpty(key) && value is not null) info.Environment[key] = value;
        }
        return info;
    }

    private static CredentialProcessResult Abort(Process process, BoundedOutput output, CancellationToken cancellationToken)
    {
        Kill(process);
        output.Discard();
        return Result(cancellationToken.IsCancellationRequested ? CredentialProcessOutcome.Cancelled : CredentialProcessOutcome.TimedOut);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: false);
        }
        catch (Exception)
        {
            // Gia' uscito, o accesso negato: non c'e' altro da fare.
        }
    }

    private static CredentialProcessResult Result(CredentialProcessOutcome outcome) => new(outcome, null, []);

    private static async Task WriteInputAsync(StreamWriter input, string text)
    {
        try
        {
            await input.WriteAsync(text).ConfigureAwait(false);
            await input.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Il processo ha chiuso stdin o e' gia' uscito: l'esito lo decide lui.
        }
        finally
        {
            try
            {
                input.Close();
            }
            catch (Exception)
            {
                // Pipe gia' rotta.
            }
        }
    }

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096];
        try
        {
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception)
        {
            // stderr non viene mai letto ne' registrato: serve solo a non bloccare il processo su una pipe piena.
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// Stdout letto a blocchi in un buffer di dimensione fissa. Dopo <see cref="Take"/> o <see cref="Discard"/> il buffer e'
    /// azzerato e una lettura ancora in corso (pipe tenuta aperta da un discendente) non vi scrive piu'.
    /// </summary>
    private sealed class BoundedOutput(int limit)
    {
        private const int InitialCapacity = 4096;

        private readonly object _gate = new();
        private readonly int _limit = Math.Max(0, limit);
        private byte[] _buffer = new byte[Math.Min(Math.Max(0, limit), InitialCapacity)];
        private int _count;
        private bool _closed;

        /// <summary>true a fine stream (o errore di lettura) entro il limite, false se il limite e' stato superato.</summary>
        public async Task<bool> ReadAsync(Stream stream)
        {
            var chunk = new byte[4096];
            try
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        return true;
                    }
                    if (read == 0) return true;
                    lock (_gate)
                    {
                        if (_closed) return true;
                        if (read > _limit - _count) return false;
                        if (_count + read > _buffer.Length) Grow(_count + read);
                        chunk.AsSpan(0, read).CopyTo(_buffer.AsSpan(_count));
                        _count += read;
                    }
                    CryptographicOperations.ZeroMemory(chunk.AsSpan(0, read));
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(chunk);
            }
        }

        public byte[] Take()
        {
            lock (_gate)
            {
                var result = _buffer.AsSpan(0, _count).ToArray();
                Close();
                return result;
            }
        }

        public void Discard()
        {
            lock (_gate) Close();
        }

        /// <summary>Raddoppia fino al limite; il buffer precedente viene azzerato prima di essere abbandonato.</summary>
        private void Grow(int required)
        {
            var size = (int)Math.Min(_limit, Math.Max(required, (long)_buffer.Length * 2));
            var larger = new byte[size];
            _buffer.AsSpan(0, _count).CopyTo(larger);
            CryptographicOperations.ZeroMemory(_buffer);
            _buffer = larger;
        }

        private void Close()
        {
            CryptographicOperations.ZeroMemory(_buffer);
            _count = 0;
            _closed = true;
        }
    }
}
