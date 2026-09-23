using System.Reflection;
using AIUsageMonitor.Core.Updates;

namespace AIUsageMonitor.App.Updates;

/// <summary>
/// Identita' della build in esecuzione, letta una volta dagli attributi dell'assembly: versione (quella passata a
/// <c>dotnet publish -p:Version=…</c>, senza il metadata di build), variante pubblicata e forma dell'eseguibile.
/// </summary>
public static class BuildInfo
{
    /// <summary>Chiave dell'<c>AssemblyMetadata</c> scritta dal csproj dell'App con il valore di <c>$(SelfContained)</c>.</summary>
    public const string SelfContainedMetadataKey = "AIUsageMonitor.SelfContained";

    private static readonly Assembly EntryAssembly = Assembly.GetEntryAssembly() ?? typeof(App).Assembly;

    /// <summary>Versione normalizzata con <see cref="UpdateSource.NormalizeVersion"/> (es. <c>1.2.3</c>).</summary>
    public static string CurrentVersion { get; } =
        UpdateSource.NormalizeVersion(EntryAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>La variante dell'exe in esecuzione: l'updater scarica sempre la stessa.</summary>
    public static PackageVariant Variant { get; } = ReadVariant();

    // IL3000 (analizzatore single-file del publish) avvisa proprio che Location e' vuota nel bundle: qui e' la sonda voluta.
#pragma warning disable IL3000
    /// <summary>
    /// True nell'exe pubblicato a file singolo: li' gli assembly vengono caricati dal bundle e
    /// <see cref="Assembly.Location"/> e' vuota. Un avvio da <c>dotnet run</c> o dalla cartella bin ha un percorso.
    /// </summary>
    public static bool IsSingleFile { get; } = string.IsNullOrEmpty(typeof(App).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>Etichetta breve della variante per le Impostazioni.</summary>
    public static string VariantLabel(PackageVariant variant) =>
        variant == PackageVariant.SelfContained ? "self-contained" : "framework-dependent";

    private static PackageVariant ReadVariant()
    {
        var value = EntryAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, SelfContainedMetadataKey, StringComparison.Ordinal))?.Value;
        return string.Equals(value?.Trim(), "true", StringComparison.OrdinalIgnoreCase)
            ? PackageVariant.SelfContained
            : PackageVariant.FrameworkDependent;
    }
}
