namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Errore dell'updater con un codice stabile (<c>UPDATES_*</c>) e un messaggio gia' pronto per l'utente. Il messaggio
/// non contiene mai token, URL firmati ne' payload di GitHub.
/// </summary>
public class UpdateException : Exception
{
    public UpdateException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}

public enum UpdateAccessReason
{
    Credentials,
    Sso,
    OAuthPolicy,
    Permissions,
    NotFound,
    Forbidden
}

/// <summary>GitHub ha rifiutato l'accesso alle release (401/403/404 dall'API).</summary>
public sealed class UpdateAccessException : UpdateException
{
    public UpdateAccessException(int status, UpdateAccessReason reason, string detail)
        : base("UPDATES_ACCESS", UpdateMessages.AccessPrefix(status, detail))
    {
        Status = status;
        Reason = reason;
    }

    public int Status { get; }
    public UpdateAccessReason Reason { get; }
}
