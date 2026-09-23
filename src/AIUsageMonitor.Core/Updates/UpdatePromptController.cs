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

/// <summary>
/// Porting di ChessAdvisor <c>UpdatePromptController</c>: offre una versione alla volta, solo se il controllo
/// automatico e' attivo e l'installazione e' <see cref="InstallationKind.Supported"/>; "Piu' tardi" ignora quella
/// versione fino al riavvio; un solo consenso copre download e installazione. Legge solo lo stato locale: i controlli
/// di rete li fa il servizio.
/// </summary>
public sealed class UpdatePromptController : IDisposable
{
    private readonly IUpdateCommands _commands;
    private readonly object _gate = new();
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);

    // Protetti da _gate.
    private UpdatePromptState _state = UpdatePromptState.Empty;
    private bool _subscribed;
    private bool _disposed;

    public UpdatePromptController(IUpdateCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
    }

    /// <summary>
    /// Alzato da thread qualsiasi, fuori dal lock, con lo stato piu' recente al momento dell'evento. Chi lo gestisce su
    /// un altro thread (es. il Dispatcher) rilegga <see cref="State"/>: due eventi ravvicinati possono arrivare invertiti.
    /// </summary>
    public event Action<UpdatePromptState>? Changed;

    public UpdatePromptState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>Si abbona a <see cref="IUpdateCommands.Changed"/> e accetta lo stato corrente.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _subscribed) return;
            _subscribed = true;
            _commands.Changed += Accept;
        }
        UpdateStatus status;
        try { status = _commands.Status; }
        catch (Exception) { return; } // gli aggiornamenti sono facoltativi: non devono interrompere l'avvio
        Accept(status);
    }

    public void Dismiss()
    {
        lock (_gate)
        {
            if (_disposed || _state.Pending is not null || _state.Version is null) return;
            _dismissed.Add(_state.Version);
            _state = _state with { Version = null, Error = "" };
        }
        Raise();
    }

    /// <summary>Scarica (se serve) e installa la versione offerta; gli errori finiscono in <see cref="UpdatePromptState.Error"/>.</summary>
    public async Task ConfirmAsync()
    {
        UpdateStatus status;
        string version;
        lock (_gate)
        {
            var current = _state.Status;
            if (_disposed
                || _state.Pending is not null
                || _state.Version is null
                || current is null
                || current.Release?.Version != _state.Version
                || !current.AutoCheck
                || (!current.CanDownload && !current.CanInstall))
                return;
            status = current;
            version = _state.Version;
            _state = _state with { Pending = status.CanInstall ? UpdatePromptPending.Installing : UpdatePromptPending.Downloading, Error = "" };
        }
        Raise();
        try
        {
            if (!status.CanInstall)
            {
                var downloaded = await _commands.DownloadAsync().ConfigureAwait(false);
                if (IsDisposed()) return;
                Accept(downloaded);
            }
            bool ready;
            lock (_gate)
            {
                if (_disposed) return;
                var latest = _state.Status;
                ready = latest?.Release?.Version == version && latest.CanInstall;
                if (ready) _state = _state with { Pending = UpdatePromptPending.Installing };
            }
            if (!ready)
            {
                Fail(UpdateMessages.PromptNotReady);
                return;
            }
            Raise();
            var installing = await _commands.InstallAsync().ConfigureAwait(false);
            if (IsDisposed()) return;
            Accept(installing);
            // La conferma resta bloccata finche' l'app non si chiude per l'installazione.
        }
        catch (Exception failure)
        {
            // Le UpdateException portano un messaggio gia' pronto per l'utente; qualsiasi altra cosa no.
            Fail(failure is UpdateException && !string.IsNullOrWhiteSpace(failure.Message) ? failure.Message : UpdateMessages.PromptFailed);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_subscribed) _commands.Changed -= Accept;
        }
    }

    private void Accept(UpdateStatus? status)
    {
        if (status is null) return;
        lock (_gate)
        {
            if (_disposed || status.Revision < (_state.Status?.Revision ?? -1)) return;
            var version = _state.Version;
            if (_state.Pending is null)
            {
                if (!status.AutoCheck || status.Installation != InstallationKind.Supported)
                    version = null;
                else if (status.Release is not null
                         && (status.Phase is UpdatePhase.Available or UpdatePhase.Downloaded)
                         && (status.CanDownload || status.CanInstall)
                         && !_dismissed.Contains(status.Release.Version))
                    version = status.Release.Version;
                else if (status.Phase == UpdatePhase.UpToDate || (version is not null && status.Release?.Version != version))
                    version = null;
            }
            _state = _state with { Status = status, Version = version, Error = version != _state.Version ? "" : _state.Error };
        }
        Raise();
    }

    private void Fail(string error)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _state = _state with { Pending = null, Error = error };
        }
        Raise();
    }

    private bool IsDisposed()
    {
        lock (_gate) return _disposed;
    }

    private void Raise()
    {
        UpdatePromptState state;
        lock (_gate)
        {
            if (_disposed) return;
            state = _state;
        }
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<UpdatePromptState>>())
        {
            try { handler(state); }
            catch (Exception) { /* un gestore della UI che fallisce non deve bloccare il flusso dell'aggiornamento */ }
        }
    }
}
