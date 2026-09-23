namespace AIUsageMonitor.Core.Updates;

// CONTRATTO — implementazione assegnata all'agente "Credenziali". Firme pubbliche da non cambiare (i membri statici
// di supporto e il tipo dell'esito del parsing sono liberi).
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

    /// <param name="workingDirectory">Cartella dati dell'app, usata come cwd del processo GCM.</param>
    /// <param name="environment">Ambiente del processo corrente; null = <see cref="System.Environment.GetEnvironmentVariables()"/>.</param>
    /// <param name="fileExists">Sonda del filesystem per trovare git e GCM; null = <see cref="File.Exists"/>.</param>
    /// <param name="isWindows">null = <see cref="OperatingSystem.IsWindows"/>.</param>
    public GitCredentialManagerLogin(IUpdateCredentialStore store, ISecretProtector protector, ICredentialProcessRunner runner,
        string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null, Func<string, bool>? fileExists = null,
        bool? isWindows = null) => throw new NotImplementedException();

    public Task<UpdateCredential> AuthenticateAsync(Action? credentialReady, CancellationToken cancellationToken) => throw new NotImplementedException();
}

// CONTRATTO — implementazione assegnata all'agente "Credenziali".
/// <summary>Esecuzione reale con <c>System.Diagnostics.Process</c>: finestra nascosta, stderr scartato, stdout limitato.</summary>
public sealed class CredentialProcessRunner : ICredentialProcessRunner
{
    public Task<CredentialProcessResult> RunAsync(CredentialProcessRequest request, CancellationToken cancellationToken) => throw new NotImplementedException();
}
