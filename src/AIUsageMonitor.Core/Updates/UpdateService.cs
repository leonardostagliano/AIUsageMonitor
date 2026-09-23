namespace AIUsageMonitor.Core.Updates;

// CONTRATTO — implementazione assegnata all'agente "UpdateService". Firme pubbliche da non cambiare.
/// <summary>
/// Macchina a stati dell'updater (porting di ChessAdvisor <c>AppUpdateService</c>). Controllo automatico opzionale
/// (15 s dopo l'avvio, poi ogni 6 ore) solo con una sessione GitHub dell'app; download e installazione sono comandi
/// espliciti separati. Thread-safe: i metodi possono essere chiamati da qualsiasi thread, <see cref="Changed"/> viene
/// alzato da thread qualsiasi.
/// </summary>
public sealed class UpdateService : IUpdateCommands, IDisposable
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ObsoleteCleanupDelay = TimeSpan.FromSeconds(30);

    public UpdateService(UpdateServiceOptions options) => throw new NotImplementedException();

    public event Action<UpdateStatus>? Changed;

    public UpdateStatus Status => throw new NotImplementedException();

    /// <summary>Legge la sessione salvata, pianifica la pulizia dei vecchi download e il primo controllo automatico.</summary>
    public Task StartAsync() => throw new NotImplementedException();

    /// <summary>La preferenza e' cambiata nelle impostazioni: aggiorna lo stato e ripianifica.</summary>
    public void PreferencesChanged(bool autoCheck) => throw new NotImplementedException();

    public Task<UpdateStatus> CheckAsync() => throw new NotImplementedException();

    /// <summary>Solo il comando esplicito della UI entra nel login interattivo, poi verifica le release.</summary>
    public Task<UpdateStatus> AuthenticateAsync() => throw new NotImplementedException();

    public Task<UpdateStatus> CancelAuthenticationAsync() => throw new NotImplementedException();

    public Task<UpdateStatus> DownloadAsync() => throw new NotImplementedException();

    public Task<UpdateStatus> InstallAsync() => throw new NotImplementedException();

    /// <summary>Elimina la sessione GitHub salvata dall'app (rifiutato durante un'operazione in corso).</summary>
    public Task<UpdateStatus> DisconnectAsync() => throw new NotImplementedException();

    /// <summary>Pagina della release proposta, o l'elenco delle release del repository.</summary>
    public string ReleaseUrl() => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}
