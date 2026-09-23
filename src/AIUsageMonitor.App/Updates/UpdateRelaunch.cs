using System.Diagnostics;
using System.Globalization;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.App.Updates;

/// <summary>
/// Lato "nuova versione" dell'installazione: l'istanza avviata da <see cref="SelfReplaceInstaller"/> con
/// <c>--updated &lt;pid&gt;</c> aspetta che la versione precedente esca prima di prendere l'istanza singola, e ogni
/// avvio elimina i file <c>.old-*</c>/<c>.new-*</c> rimasti accanto all'eseguibile.
/// </summary>
public static class UpdateRelaunch
{
    /// <summary>Attesa massima dell'uscita della versione precedente.</summary>
    public static readonly TimeSpan PreviousExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Per quanto riprovare l'istanza singola dopo l'uscita: il mutex si libera quando il processo e' chiuso del tutto.</summary>
    public static readonly TimeSpan AcquireRetry = TimeSpan.FromSeconds(5);

    /// <summary>Ritardo della pulizia dei residui: l'eseguibile rinominato si libera solo dopo l'uscita della versione precedente.</summary>
    public static readonly TimeSpan CleanupDelay = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan AcquirePollInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Pid passato con <c>--updated</c>, o null se l'argomento manca o non e' un pid valido.</summary>
    public static int? PreviousProcessId(IReadOnlyList<string> args)
    {
        for (var i = 0; i + 1 < args.Count; i++)
        {
            if (!string.Equals(args[i], SelfReplaceInstaller.UpdatedArgument, StringComparison.OrdinalIgnoreCase)) continue;
            return int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
        }
        return null;
    }

    /// <summary>
    /// Aspetta al massimo <paramref name="timeout"/> che la versione precedente esca. Non lancia mai: gira prima che
    /// l'app abbia log e istanza singola, quindi restituisce l'esito da scrivere nel log appena possibile.
    /// </summary>
    public static string WaitForPreviousInstance(int pid, TimeSpan timeout)
    {
        if (pid == Environment.ProcessId) return "pid del processo corrente, nessuna attesa";
        Process previous;
        try
        {
            previous = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return "versione precedente gia' terminata";
        }
        catch (Exception ex)
        {
            return $"versione precedente non leggibile ({ex.GetType().Name})";
        }

        using (previous)
        {
            try
            {
                // Pid riciclato: la versione precedente ha avviato questo processo, quindi e' partita prima di lui. Un
                // processo nato dopo e' un altro programma che ha ricevuto lo stesso pid, e non va aspettato.
                using var self = Process.GetCurrentProcess();
                if (previous.StartTime > self.StartTime) return "pid riassegnato a un altro processo, nessuna attesa";
            }
            catch (Exception)
            {
                // Ora di avvio non leggibile (accesso negato, processo appena uscito): si aspetta comunque, con il tetto.
            }

            try
            {
                return previous.WaitForExit(timeout)
                    ? "versione precedente terminata"
                    : $"versione precedente ancora attiva dopo {timeout.TotalSeconds:0} s";
            }
            catch (Exception ex)
            {
                return $"attesa della versione precedente non riuscita ({ex.GetType().Name})";
            }
        }
    }

    /// <summary>
    /// Prende l'istanza singola riprovando per <paramref name="retryFor"/>: il processo precedente puo' risultare uscito
    /// un istante prima che il sistema chiuda il suo handle del mutex. Null se un'altra istanza la tiene davvero.
    /// </summary>
    public static SingleInstance? AcquireSingleInstance(TimeSpan retryFor)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            var single = SingleInstance.TryAcquire();
            if (single is not null || elapsed.Elapsed >= retryFor) return single;
            Thread.Sleep(AcquirePollInterval);
        }
    }

    /// <summary>
    /// Elimina in background, dopo <see cref="CleanupDelay"/>, i residui della sostituzione dell'eseguibile. Gira a ogni
    /// avvio dell'exe pubblicato (non solo dopo un aggiornamento: un avvio precedente puo' non esserci riuscito perche'
    /// il file era ancora bloccato). Gli errori finiscono solo nel log.
    /// </summary>
    public static void ScheduleLeftoverCleanup(Action<string> logInfo, Action<string, Exception> logError)
    {
        var target = Environment.ProcessPath;
        // Da dotnet run o dalla cartella bin non c'e' mai stata una sostituzione, e ProcessPath puo' essere dotnet.exe.
        if (!BuildInfo.IsSingleFile || string.IsNullOrEmpty(target)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(CleanupDelay).ConfigureAwait(false);
                var removed = ExecutableSwap.CleanupLeftovers(target);
                if (removed > 0) logInfo($"Updater: eliminati {removed} file residui della sostituzione dell'eseguibile");
            }
            catch (Exception ex)
            {
                logError("Updater: pulizia dei file residui della sostituzione non riuscita", ex);
            }
        });
    }
}
