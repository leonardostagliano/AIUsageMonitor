using System.Drawing.Imaging;
using System.IO;
using System.Security.Cryptography;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Tray;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Notifications;
using Microsoft.Toolkit.Uwp.Notifications;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AIUsageMonitor.App.Notifications;

/// <summary>Native Windows toasts with explicit branding and click-to-open activation.</summary>
public sealed class AppNotificationSender : IDisposable
{
    private readonly string _assetDirectory;
    private readonly FileLogger _log;
    private readonly Action _activate;
    private readonly bool _activationRegistered;
    private Uri? _logo;

    public AppNotificationSender(string localAppDataDirectory, FileLogger log, Action activate)
    {
        _assetDirectory = Path.Combine(localAppDataDirectory, "notifications");
        _log = log;
        _activate = activate;
        // Register the COM callback before sending any toast, including when launched by an old notification.
        try
        {
            ToastNotificationManagerCompat.OnActivated += OnActivated;
            _activationRegistered = true;
        }
        catch (Exception ex)
        {
            // Notification registration is optional: a Windows policy must not prevent monitoring or the tray.
            _log.Error("Windows notification registration failed", ex);
        }
    }

    public void Show(string title, string text)
    {
        try
        {
            _logo ??= ExportLogo();
            var document = new XmlDocument();
            document.LoadXml(NotificationPayload.Create(title, text, _logo));
            var toast = new ToastNotification(document)
            {
                Group = "sessions",
                ExpirationTime = DateTimeOffset.Now.AddHours(24)
            };
            toast.Failed += (_, args) => _log.Error("Windows notification failed", args.ErrorCode);
            var notifier = ToastNotificationManagerCompat.CreateToastNotifier();
            if (notifier.Setting != NotificationSetting.Enabled)
            {
                _log.Warn($"Windows notifications unavailable: {notifier.Setting}");
                return;
            }
            notifier.Show(toast);
            // No session names or message contents in diagnostics.
            _log.Info($"Windows notification sent with explicit logo {_logo.LocalPath}");
        }
        catch (Exception ex)
        {
            // A notification failure must not stop monitoring; do not silently revert to the stale balloon icon.
            _log.Error("Windows notification could not be sent", ex);
        }
    }

    private Uri ExportLogo()
    {
        using var rendered = TrayIconRenderer.Render(null, 128);
        using var bitmap = rendered.Icon.ToBitmap();
        using var buffer = new MemoryStream();
        bitmap.Save(buffer, ImageFormat.Png);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16];
        Directory.CreateDirectory(_assetDirectory);
        var path = Path.Combine(_assetDirectory, $"app-logo-{hash}.png");
        // A changed logo gets a new URI. Keep older files for notifications already in Action Center.
        if (!File.Exists(path) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
            File.WriteAllBytes(path, bytes);
        return new Uri(path);
    }

    private void OnActivated(ToastNotificationActivatedEventArgsCompat args)
    {
        if (args.Argument != "show-notch") return;
        _log.Info("Windows notification activated: show-notch");
        UiDispatcher.Post(_activate);
    }

    public void Dispose()
    {
        if (_activationRegistered) ToastNotificationManagerCompat.OnActivated -= OnActivated;
    }
}
