namespace AIUsageMonitor.Core.Updates;

// CONTRATTO — implementazione assegnata all'agente "Credenziali" (IO di sistema del Core). Firme pubbliche da non cambiare.
/// <summary>
/// Sostituzione dell'eseguibile a file singolo: su NTFS un exe in esecuzione non si puo' sovrascrivere ne' cancellare ma
/// si puo' rinominare nella stessa cartella. <see cref="Replace"/> copia la nuova versione accanto al target
/// (<c>&lt;exe&gt;.new-&lt;guid&gt;</c>), ne verifica dimensione e SHA-256, rinomina il target in
/// <c>&lt;exe&gt;.old-&lt;guid&gt;</c> e mette la copia al suo posto; se un passo fallisce ripristina lo stato iniziale.
/// </summary>
public static class ExecutableSwap
{
    /// <exception cref="UpdateException">UPDATES_INSTALL se la sostituzione non riesce (stato originale ripristinato).</exception>
    public static ExecutableSwapResult Replace(string targetPath, string stagedPath, string expectedSha256, long expectedSize) => throw new NotImplementedException();

    /// <summary>Elimina i <c>&lt;exe&gt;.old-*</c> e i <c>&lt;exe&gt;.new-*</c> rimasti accanto al target; ignora i file bloccati.</summary>
    public static int CleanupLeftovers(string targetPath) => throw new NotImplementedException();

    /// <summary>Prova di scrittura (crea ed elimina un file temporaneo) nella cartella indicata.</summary>
    public static bool IsDirectoryWritable(string directory) => throw new NotImplementedException();
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

    /// <summary>Rimette il backup al posto del target (eliminando la nuova versione). Lancia se non riesce.</summary>
    public void Rollback() => throw new NotImplementedException();
}
