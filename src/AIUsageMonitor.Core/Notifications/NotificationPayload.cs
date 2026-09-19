using System.Xml.Linq;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>Toast content with an explicit local app logo; never relies on the legacy tray icon cache.</summary>
public static class NotificationPayload
{
    public static string Create(string title, string text, Uri logoUri)
    {
        if (!logoUri.IsAbsoluteUri || !logoUri.IsFile)
            throw new ArgumentException("The notification logo must be an absolute local file URI.", nameof(logoUri));

        return new XElement("toast", new XAttribute("launch", "show-notch"),
            new XElement("visual",
                new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("image", new XAttribute("placement", "appLogoOverride"),
                        new XAttribute("src", logoUri.AbsoluteUri), new XAttribute("alt", "AIUsageMonitor")),
                    new XElement("text", title), new XElement("text", text))))
            .ToString(SaveOptions.DisableFormatting);
    }
}
