using System.Security;
using System.Security.Cryptography;

namespace AIUsageMonitor.Core.Updates;

/// <summary>
/// Sostituzione dell'eseguibile a file singolo: su NTFS un exe in esecuzione non si puo' sovrascrivere ne' cancellare ma
/// si puo' rinominare nella stessa cartella. <see cref="Replace"/> copia la nuova versione accanto al target
/// (<c>&lt;exe&gt;.new-&lt;guid&gt;</c>), ne verifica dimensione e SHA-256, rinomina il target in
/// <c>&lt;exe&gt;.old-&lt;guid&gt;</c> e mette la copia al suo posto; se un passo fallisce ripristina lo stato iniziale.
/// </summary>
public static class ExecutableSwap
{
    private const string BackupMarker = ".old-";
    private const string CopyMarker = ".new-";
    private const int GuidLength = 32;

    // Antivirus e indicizzatore aprono per un attimo l'exe appena copiato: una rinomina respinta per condivisione si
    // ritenta, come la scrittura atomica di ChessAdvisor (20..400 ms, meno di un secondo in tutto).
    private static readonly int[] RetryDelaysMs = [20, 50, 100, 200, 400];

    /// <exception cref="UpdateException">
    /// UPDATES_INTEGRITY se la copia non corrisponde a <paramref name="expectedSha256"/>/<paramref name="expectedSize"/>
    /// (copia eliminata, target intatto); UPDATES_INSTALL se la sostituzione non riesce (stato originale ripristinato, o
    /// <see cref="UpdateMessages.InstallRollbackFailed"/> se nemmeno il ripristino e' possibile).
    /// </exception>
    public static ExecutableSwapResult Replace(string targetPath, string stagedPath, string expectedSha256, long expectedSize)
    {
        string target;
        string staged;
        try
        {
            target = Path.GetFullPath(targetPath);
            staged = Path.GetFullPath(stagedPath);
        }
        catch (Exception)
        {
            throw ReplaceFailed();
        }
        if (!IsSha256Hex(expectedSha256) || expectedSize < 0 || !File.Exists(staged)) throw IntegrityChanged();

        var id = Guid.NewGuid().ToString("N");
        var copy = target + CopyMarker + id;
        var backup = target + BackupMarker + id;

        // 1. Copia nella cartella del target: le rinomine successive restano sullo stesso volume, quindi atomiche.
        try
        {
            File.Copy(staged, copy, overwrite: false);
        }
        catch (Exception)
        {
            TryDelete(copy);
            throw ReplaceFailed();
        }

        // 2. Si verifica la copia, non il file scaricato: e' lei che diventera' l'eseguibile.
        bool matches;
        try
        {
            matches = Matches(copy, expectedSha256, expectedSize);
        }
        catch (Exception)
        {
            TryDelete(copy);
            throw ReplaceFailed();
        }
        if (!matches)
        {
            TryDelete(copy);
            throw IntegrityChanged();
        }

        // 3. L'exe in esecuzione si sposta di lato (consentito anche mentre gira).
        try
        {
            MoveWithRetry(target, backup, overwrite: false);
        }
        catch (Exception)
        {
            TryDelete(copy);
            throw ReplaceFailed();
        }

        // 4. La copia verificata prende il suo posto; altrimenti il backup torna dov'era.
        try
        {
            MoveWithRetry(copy, target, overwrite: false);
        }
        catch (Exception)
        {
            try
            {
                MoveWithRetry(backup, target, overwrite: false);
            }
            catch (Exception)
            {
                // Target assente: la copia verificata e il backup restano accanto al percorso originale.
                throw new UpdateException("UPDATES_INSTALL", UpdateMessages.InstallRollbackFailed);
            }
            TryDelete(copy);
            throw ReplaceFailed();
        }
        return new ExecutableSwapResult(target, backup);
    }

    /// <summary>
    /// Elimina i <c>&lt;exe&gt;.old-*</c> e i <c>&lt;exe&gt;.new-*</c> rimasti accanto al target; ignora i file bloccati.
    /// Solo il formato esatto <c>&lt;nome exe&gt;.old-&lt;32 esadecimali minuscoli&gt;</c> (o <c>.new-</c>): nient'altro
    /// nella cartella viene toccato. Ritorna quanti file ha eliminato.
    /// </summary>
    public static int CleanupLeftovers(string targetPath)
    {
        string directory;
        string name;
        try
        {
            var full = Path.GetFullPath(targetPath);
            directory = Path.GetDirectoryName(full) ?? "";
            name = Path.GetFileName(full);
        }
        catch (Exception)
        {
            return 0;
        }
        if (directory.Length == 0 || name.Length == 0) return 0;

        var removed = 0;
        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
                MatchType = MatchType.Simple,
                MatchCasing = MatchCasing.PlatformDefault
            };
            foreach (var path in Directory.EnumerateFiles(directory, name + ".*", options))
            {
                if (!IsLeftover(Path.GetFileName(path), name)) continue;
                try
                {
                    File.Delete(path);
                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Ancora in uso (es. la versione precedente non e' ancora uscita): ci si riprova al prossimo avvio.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException)
        {
            // Cartella sparita o illeggibile: la pulizia e' facoltativa.
        }
        return removed;
    }

    /// <summary>Prova di scrittura (crea ed elimina un file temporaneo) nella cartella indicata.</summary>
    public static bool IsDirectoryWritable(string directory)
    {
        try
        {
            // GetFullPath: una stringa vuota e' un errore, non la cartella corrente.
            var probe = Path.Combine(Path.GetFullPath(directory), $".aiusagemonitor-write-probe-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or NotSupportedException or SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// <see cref="File.Move(string, string, bool)"/> che ritenta brevemente quando Windows respinge la rinomina per un
    /// handle aperto da un altro processo (condivisione, blocco, accesso negato temporaneo).
    /// </summary>
    internal static void MoveWithRetry(string source, string destination, bool overwrite)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite);
                return;
            }
            catch (Exception ex) when (attempt < RetryDelaysMs.Length && IsTransient(ex))
            {
                Thread.Sleep(RetryDelaysMs[attempt]);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        const int sharingViolation = 32;
        const int lockViolation = 33;
        if (ex is UnauthorizedAccessException) return true;
        if (ex is not IOException || ex is FileNotFoundException or DirectoryNotFoundException) return false;
        var code = ex.HResult & 0xFFFF;
        return code is sharingViolation or lockViolation;
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Rimane un file .new-/.old- che CleanupLeftovers eliminera' al prossimo avvio.
        }
    }

    private static bool Matches(string path, string expectedSha256, long expectedSize)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        if (stream.Length != expectedSize) return false;
        var actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSha256Hex(string? value)
    {
        if (value is null || value.Length != 64) return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiHexDigit(c)) return false;
        }
        return true;
    }

    /// <summary><c>&lt;name&gt;.old-&lt;32 hex minuscoli&gt;</c> o <c>&lt;name&gt;.new-&lt;32 hex minuscoli&gt;</c>, nient'altro.</summary>
    private static bool IsLeftover(string fileName, string name)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fileName.Length != name.Length + BackupMarker.Length + GuidLength || !fileName.StartsWith(name, comparison)) return false;
        var marker = fileName.Substring(name.Length, BackupMarker.Length);
        if (marker != BackupMarker && marker != CopyMarker) return false;
        foreach (var c in fileName.AsSpan(name.Length + BackupMarker.Length))
        {
            if (c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        }
        return true;
    }

    private static UpdateException ReplaceFailed() => new("UPDATES_INSTALL", UpdateMessages.InstallReplaceFailed);

    private static UpdateException IntegrityChanged() => new("UPDATES_INTEGRITY", UpdateMessages.IntegrityChangedLocally);
}

/// <summary>Esito di <see cref="ExecutableSwap.Replace"/>: permette di tornare alla versione precedente se il riavvio fallisce.</summary>
public sealed class ExecutableSwapResult
{
    public ExecutableSwapResult(string targetPath, string backupPath)
    {
        TargetPath = targetPath;
        BackupPath = backupPath;
    }

    public string TargetPath { get; }
    public string BackupPath { get; }

    /// <summary>
    /// Rimette il backup al posto del target (eliminando la nuova versione). Lancia <see cref="UpdateException"/>
    /// (UPDATES_INSTALL) se non riesce. La nuova versione viene prima spostata di lato e poi eliminata: se il backup non
    /// puo' tornare al suo posto, torna lei e il target non resta mai vuoto; se e' ancora in esecuzione e non si puo'
    /// eliminare resta come <c>.new-*</c> per <see cref="ExecutableSwap.CleanupLeftovers"/>.
    /// </summary>
    public void Rollback()
    {
        if (!File.Exists(BackupPath)) throw RollbackFailed();
        var discarded = TargetPath + ".new-" + Guid.NewGuid().ToString("N");
        var movedAside = false;
        try
        {
            if (File.Exists(TargetPath))
            {
                ExecutableSwap.MoveWithRetry(TargetPath, discarded, overwrite: false);
                movedAside = true;
            }
            ExecutableSwap.MoveWithRetry(BackupPath, TargetPath, overwrite: false);
        }
        catch (Exception)
        {
            if (movedAside && !File.Exists(TargetPath))
            {
                try
                {
                    ExecutableSwap.MoveWithRetry(discarded, TargetPath, overwrite: false);
                }
                catch (Exception)
                {
                    // Il messaggio sotto dice gia' che l'eseguibile non e' stato ripristinato.
                }
            }
            throw RollbackFailed();
        }
        if (movedAside) ExecutableSwap.TryDelete(discarded);
    }

    private static UpdateException RollbackFailed() => new("UPDATES_INSTALL", UpdateMessages.InstallRollbackFailed);
}
