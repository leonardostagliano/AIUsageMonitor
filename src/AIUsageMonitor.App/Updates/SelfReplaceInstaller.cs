using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.App.Updates;

/// <summary>
/// Installazione per l'exe a file singolo, senza installer: sostituisce l'eseguibile in esecuzione con quello scaricato
/// (<see cref="ExecutableSwap"/>, rinomina + copia verificata nella stessa cartella) e avvia la nuova versione con
/// <c>--updated &lt;pid&gt;</c>, che aspetta l'uscita di questo processo prima di prendere l'istanza singola. Se la
/// nuova versione non parte, rimette al suo posto l'eseguibile originale.
/// </summary>
public sealed class SelfReplaceInstaller : IUpdateInstaller
{
    /// <summary>Argomento con cui la nuova versione viene avviata, seguito dal pid del processo che si chiude.</summary>
    public const string UpdatedArgument = "--updated";

    private readonly Action<string> _logInfo;
    private readonly Action<string, Exception?> _logError;

    public SelfReplaceInstaller(Action<string>? logInfo = null, Action<string, Exception?>? logError = null)
    {
        // Il log non deve mai interrompere la sostituzione a meta' strada (tra Replace e l'avvio o il ripristino).
        _logInfo = message =>
        {
            try { logInfo?.Invoke(message); }
            catch (Exception) { /* diagnostica facoltativa */ }
        };
        _logError = (message, ex) =>
        {
            try { logError?.Invoke(message, ex); }
            catch (Exception) { /* diagnostica facoltativa */ }
        };
    }

    public InstallationKind DetectInstallation(bool probeWritable)
    {
        var path = Environment.ProcessPath;
        if (!BuildInfo.IsSingleFile || string.IsNullOrEmpty(path)) return InstallationKind.Development;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return InstallationKind.UnsupportedPlatform;
        // All'avvio niente file sonda accanto all'exe: con "Accesso controllato alle cartelle" e l'exe sul Desktop o in
        // Documenti Windows Security mostrerebbe un avviso a ogni accesso, anche a chi non usa gli aggiornamenti.
        if (!probeWritable) return InstallationKind.Supported;
        var directory = Path.GetDirectoryName(path);
        try
        {
            if (string.IsNullOrEmpty(directory) || !ExecutableSwap.IsDirectoryWritable(directory)) return InstallationKind.ReadOnlyLocation;
        }
        catch (Exception ex)
        {
            // La sonda non dovrebbe lanciare; se lo fa, la cartella va trattata come non scrivibile e il controllo prosegue.
            _logError("Updater: prova di scrittura nella cartella dell'eseguibile non riuscita", ex);
            return InstallationKind.ReadOnlyLocation;
        }
        return InstallationKind.Supported;
    }

    /// <summary>
    /// Gira su un thread del pool: la copia verificata di un eseguibile da decine di MB non deve fermare la UI. Ogni
    /// errore esce come <see cref="UpdateException"/>, mai come eccezione di sistema o di cancellazione.
    /// </summary>
    public Task InstallAsync(StagedUpdate staged, CancellationToken cancellationToken) =>
        // Niente token a Task.Run: un token gia' cancellato darebbe un TaskCanceledException invece di UpdateException.
        Task.Run(() => Install(staged, cancellationToken));

    private void Install(StagedUpdate? staged, CancellationToken cancellationToken)
    {
        if (staged is null) throw new UpdateException("UPDATES_INSTALL_STATE", UpdateMessages.InstallState);
        var target = Environment.ProcessPath;
        if (!OperatingSystem.IsWindows() || !BuildInfo.IsSingleFile || string.IsNullOrEmpty(target))
            throw new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallUnavailable);
        var directory = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(directory)) throw new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallUnavailable);

        EnsureStagedVersion(staged);
        // Ultimo punto in cui fermarsi senza conseguenze: dopo la sostituzione si arriva sempre all'avvio o al ripristino.
        if (cancellationToken.IsCancellationRequested) throw new UpdateException("UPDATES_CLOSED", UpdateMessages.Closing);

        ExecutableSwapResult swap;
        try
        {
            // Replace arriva sempre alla fine (sostituzione o ripristino): UpdateService.Dispose aspetta questo task
            // prima di lasciar uscire il processo, cosi' un "Esci" a meta' non lascia il percorso dell'exe vuoto.
            swap = ExecutableSwap.Replace(target, staged.Path, staged.Sha256, staged.Size);
        }
        catch (UpdateException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logError("Updater: sostituzione dell'eseguibile non riuscita", ex);
            throw new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallReplaceFailed);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            // L'app si sta chiudendo per scelta dell'utente (Esci, fine sessione) mentre la sostituzione era in corso: la
            // nuova versione resta al suo posto e partira' al prossimo avvio, senza riaprire l'app contro la sua scelta.
            _logInfo($"Updater: eseguibile sostituito con la versione {staged.Version}; l'app si sta chiudendo, nessun riavvio");
            return;
        }
        _logInfo($"Updater: eseguibile sostituito con la versione {staged.Version}, avvio della nuova versione");

        try
        {
            var start = new ProcessStartInfo(target)
            {
                UseShellExecute = false,
                WorkingDirectory = directory
            };
            start.ArgumentList.Add(UpdatedArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex)
        {
            _logError("Updater: avvio della nuova versione non riuscito", ex);
            try
            {
                swap.Rollback();
                _logInfo("Updater: eseguibile precedente ripristinato");
            }
            catch (Exception rollback)
            {
                _logError("Updater: ripristino dell'eseguibile precedente non riuscito", rollback);
                throw new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallRollbackFailed);
            }
            throw new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallNotStarted);
        }
    }

    /// <summary>
    /// La versione scritta nelle risorse del PE (ProductVersion = InformationalVersion del publish) deve essere quella
    /// della release: un asset con il nome giusto ma il contenuto di un'altra versione non va installato.
    /// </summary>
    private void EnsureStagedVersion(StagedUpdate staged)
    {
        string? productVersion;
        try
        {
            productVersion = FileVersionInfo.GetVersionInfo(staged.Path).ProductVersion;
        }
        catch (Exception ex)
        {
            _logError("Updater: lettura della versione dell'eseguibile scaricato non riuscita", ex);
            throw new UpdateException("UPDATES_INSTALL", UpdateMessages.LocalPackageChanged);
        }
        var written = UpdateSource.NormalizeVersion(productVersion);
        if (!string.Equals(written, staged.Version, StringComparison.Ordinal))
        {
            // Solo versioni, nessun percorso: la cartella dei download sta nel profilo dell'utente.
            _logInfo($"Updater: versione nel PE '{Truncate(written, 40)}' diversa dalla release {staged.Version}");
            throw new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallVersionMismatch);
        }
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}
