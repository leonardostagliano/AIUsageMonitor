namespace AIUsageMonitor.Core.Updates;

// Messaggi dell'area "credenziali e sostituzione dell'eseguibile" che non esistono nel file comune.
public static partial class UpdateMessages
{
    /// <summary>
    /// Ne' la sostituzione ne' il ripristino sono riusciti (ExecutableSwap o SelfReplaceInstaller). Il percorso
    /// dell'eseguibile non resta mai vuoto: contiene la versione precedente o quella nuova.
    /// </summary>
    public const string InstallRollbackFailed = "Aggiornamento non riuscito e non è stato possibile ripristinare con certezza l'eseguibile precedente. Chiudi l'app e avviala di nuovo dallo stesso percorso; se non parte, scaricala dalla pagina delle release su GitHub.";
}
