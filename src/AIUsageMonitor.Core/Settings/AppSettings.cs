namespace AIUsageMonitor.Core.Settings;

public sealed class AppSettings
{
    public bool ClaudeEnabled { get; set; } = true;
    public bool CodexEnabled { get; set; } = true;
    public int ClaudeRefreshSeconds { get; set; } = 60;
    public int CodexRefreshSeconds { get; set; } = 30;

    public int MonitorIndex { get; set; }
    public int VerticalOffset { get; set; }
    public int CollapseDelayMs { get; set; } = 400;
    public bool Compact { get; set; }
    public bool NotchVisible { get; set; } = true;

    public bool NotifyNeedsInput { get; set; } = true;
    public bool NotifyTurnCompleted { get; set; } = true;
    public bool NotifyError { get; set; } = true;
    public bool NotifyClaude { get; set; } = true;
    public bool NotifyCodex { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    public AppSettings Normalized()
    {
        ClaudeRefreshSeconds = Math.Clamp(ClaudeRefreshSeconds, 30, 600);
        CodexRefreshSeconds = Math.Clamp(CodexRefreshSeconds, 10, 600);
        CollapseDelayMs = Math.Clamp(CollapseDelayMs, 0, 5000);
        MonitorIndex = Math.Max(0, MonitorIndex);
        VerticalOffset = Math.Clamp(VerticalOffset, -5000, 5000);
        return this;
    }
}
