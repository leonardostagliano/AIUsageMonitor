namespace AIUsageMonitor.Core.Updates;

// CONTRATTO — implementazione assegnata all'agente "Credenziali". Firme pubbliche da non cambiare.
/// <summary>
/// Sessione GitHub dell'app in <c>%LOCALAPPDATA%\AIUsageMonitor\updates-auth.json</c>:
/// <c>{"version":1,"cipher":"&lt;CipherName&gt;","data":"&lt;base64&gt;"}</c>, dove data e' il JSON
/// <c>{"token":…,"account":…}</c> cifrato con <see cref="ISecretProtector"/>. Mai token o account nei log.
/// </summary>
public sealed class UpdateCredentialStore : IUpdateCredentialStore
{
    public UpdateCredentialStore(string path, ISecretProtector protector) => throw new NotImplementedException();

    public UpdateCredential Read() => throw new NotImplementedException();

    public void Save(string token, string account) => throw new NotImplementedException();

    public void Delete() => throw new NotImplementedException();
}

// CONTRATTO — implementazione assegnata all'agente "Credenziali".
/// <summary>DPAPI con ambito utente corrente (CryptProtectData via P/Invoke, nessun pacchetto NuGet). Solo Windows.</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    public const string Name = "dpapi-current-user";

    public string CipherName => Name;

    public bool IsAvailable => throw new NotImplementedException();

    public byte[] Protect(byte[] plaintext) => throw new NotImplementedException();

    public byte[] Unprotect(byte[] ciphertext) => throw new NotImplementedException();
}
