using System.Xml.Linq;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>Toast content with an explicit local app logo; never relies on the legacy tray icon cache.</summary>
public static class NotificationPayload
{
    /// <summary>Argomento di attivazione di default: il click sulla notifica apre il notch.</summary>
    public const string ShowNotchLaunch = "show-notch";

    /// <summary>Argomento di attivazione della notifica "aggiornamento disponibile": apre la conferma.</summary>
    public const string ShowUpdateLaunch = "show-update";

    /// <param name="launch">
    /// Argomento restituito all'app quando l'utente clicca la notifica (attributo <c>launch</c> del toast). Il default
    /// lascia invariato il payload delle notifiche delle sessioni.
    /// </param>
    public static string Create(string title, string text, Uri logoUri, string launch = ShowNotchLaunch)
    {
        if (!logoUri.IsAbsoluteUri || !logoUri.IsFile)
            throw new ArgumentException("The notification logo must be an absolute local file URI.", nameof(logoUri));
        ArgumentException.ThrowIfNullOrWhiteSpace(launch);

        return new XElement("toast", new XAttribute("launch", launch),
            new XElement("visual",
                new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("image", new XAttribute("placement", "appLogoOverride"),
                        new XAttribute("src", logoUri.AbsoluteUri), new XAttribute("alt", "AIUsageMonitor")),
                    new XElement("text", title), new XElement("text", text))))
            .ToString(SaveOptions.DisableFormatting);
    }
}
