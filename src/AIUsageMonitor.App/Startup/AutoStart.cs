using Microsoft.Win32;

namespace AIUsageMonitor.App.Startup;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AIUsageMonitor";
    public const string StartupArgument = "--startup";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey) ?? throw new InvalidOperationException("Cannot open the Run key");
        if (enabled)
        {
            var processPath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(processPath)) throw new InvalidOperationException("Cannot determine the application path");
            key.SetValue(ValueName, $"\"{processPath}\" {StartupArgument}");
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>Upgrades an existing Run entry without enabling auto-start for users who disabled it.</summary>
    public static void EnsureStartupArgument()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not string command || string.IsNullOrWhiteSpace(command)) return;
        if (command.Contains(StartupArgument, StringComparison.OrdinalIgnoreCase)) return;
        key.SetValue(ValueName, $"{command} {StartupArgument}");
    }
}
