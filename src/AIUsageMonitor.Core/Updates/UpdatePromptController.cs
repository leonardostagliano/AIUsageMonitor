namespace AIUsageMonitor.Core.Updates;

public enum UpdatePromptPending
{
    Downloading,
    Installing
}

/// <summary>Stato della conferma "scarica e riavvia": <see cref="Version"/> e' la versione offerta, null se nessuna.</summary>
public sealed record UpdatePromptState(UpdateStatus? Status, string? Version, UpdatePromptPending? Pending, string Error)
{
    public static UpdatePromptState Empty { get; } = new(null, null, null, "");
}

// CONTRATTO — implementazione assegnata all'agente "UpdateService". Firme pubbliche da non cambiare.
/// <summary>
/// Porting di ChessAdvisor <c>UpdatePromptController</c>: offre una versione alla volta, solo se il controllo
/// automatico e' attivo e l'installazione e' <see cref="InstallationKind.Supported"/>; "Piu' tardi" ignora quella
/// versione fino al riavvio; un solo consenso copre download e installazione. Legge solo lo stato locale: i controlli
/// di rete li fa il servizio.
/// </summary>
public sealed class UpdatePromptController : IDisposable
{
    public UpdatePromptController(IUpdateCommands commands) => throw new NotImplementedException();

    /// <summary>Alzato da thread qualsiasi.</summary>
    public event Action<UpdatePromptState>? Changed;

    public UpdatePromptState State => throw new NotImplementedException();

    /// <summary>Si abbona a <see cref="IUpdateCommands.Changed"/> e accetta lo stato corrente.</summary>
    public void Start() => throw new NotImplementedException();

    public void Dismiss() => throw new NotImplementedException();

    /// <summary>Scarica (se serve) e installa la versione offerta; gli errori finiscono in <see cref="UpdatePromptState.Error"/>.</summary>
    public Task ConfirmAsync() => throw new NotImplementedException();

    public void Dispose() => throw new NotImplementedException();
}
