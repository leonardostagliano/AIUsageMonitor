namespace AIUsageMonitor.Core.Hooks;

/// <summary>The Node hook bridge, embedded in the assembly so the installer can write it next to the events file.</summary>
public static class HookScript
{
    public const string FileName = "hook.cjs";

    public static string Content { get; } = Load();

    private static string Load()
    {
        using var stream = typeof(HookScript).Assembly.GetManifestResourceStream(FileName)
            ?? throw new InvalidOperationException("Embedded resource hook.cjs is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
