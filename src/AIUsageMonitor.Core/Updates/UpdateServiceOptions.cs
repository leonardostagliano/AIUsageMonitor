namespace AIUsageMonitor.Core.Updates;

/// <summary>Dipendenze di <see cref="UpdateService"/>: tutte sostituibili nei test, cablate in <c>AppServices</c>.</summary>
public sealed class UpdateServiceOptions
{
    public required IReleaseTransport Transport { get; init; }
    public required IUpdateCredentialStore Credentials { get; init; }
    public required IGitHubLogin Login { get; init; }
    public required IUpdateInstaller Installer { get; init; }

    /// <summary>Cartella dei download (<c>AppPaths.UpdatesDir</c>), creata al primo download.</summary>
    public required string DownloadsDirectory { get; init; }

    /// <summary>Versione in esecuzione, gia' normalizzata con <see cref="UpdateSource.NormalizeVersion"/>.</summary>
    public required string CurrentVersion { get; init; }

    public required PackageVariant Variant { get; init; }

    /// <summary>Preferenza "controlla automaticamente" letta dalle impostazioni (<c>AppSettings.UpdatesAutoCheck</c>).</summary>
    public required Func<bool> AutoCheck { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Chiamato dopo che la nuova versione e' partita: l'app deve chiudersi subito per liberare l'istanza singola.</summary>
    public Action QuitForInstall { get; init; } = () => { };

    /// <summary>Riporta in primo piano la finestra dell'app dopo il login nel browser (solo per il collegamento esplicito).</summary>
    public Action ReturnToApp { get; init; } = () => { };

    public Action<string> LogInfo { get; init; } = _ => { };
    public Action<string, Exception?> LogError { get; init; } = (_, _) => { };
}
