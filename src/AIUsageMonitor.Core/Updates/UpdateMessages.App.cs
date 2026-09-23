namespace AIUsageMonitor.Core.Updates;

// Messaggi dell'installazione lato App (SelfReplaceInstaller), tenuti fuori dal file comune come previsto dal contratto.
public static partial class UpdateMessages
{
    public const string InstallRollbackFailed = "Windows non ha avviato la nuova versione e non è stato possibile ripristinare quella in uso. Chiudi l'app e avviala di nuovo dallo stesso eseguibile.";
    public const string InstallUnavailable = "L'eseguibile in esecuzione non può essere sostituito da questa build: scarica la nuova versione dalla pagina della release.";
}
