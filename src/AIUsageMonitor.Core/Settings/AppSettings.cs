namespace AIUsageMonitor.Core.Settings;

public sealed class AppSettings
{
    public bool ClaudeEnabled { get; set; } = true;
    public bool CodexEnabled { get; set; } = true;
    public int ClaudeRefreshSeconds { get; set; } = 60;
    public int CodexRefreshSeconds { get; set; } = 30;

    /// <summary>
    /// Fallback sui rollout per i thread figli di Codex. Resta attivo di default perche' i gruppi hook di Codex
    /// funzionano solo dopo l'approvazione con <c>/hooks</c>, e finche' non arriva nessun <c>SubagentStart</c> i
    /// rollout sono l'unica fonte. Per le sessioni i cui hook riportano davvero i subagenti il fallback si spegne
    /// da solo (vedi <c>HookEventPump.CodexHookGrace</c>); questa opzione lo disattiva del tutto.
    /// </summary>
    public bool CodexSubagentFallback { get; set; } = true;

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

    /// <summary>
    /// Controllo automatico delle release (15 s dopo l'avvio, poi ogni 6 ore). Parte solo dopo che l'utente ha collegato
    /// l'account GitHub dalle impostazioni: senza sessione un controllo potrebbe solo fallire.
    /// </summary>
    public bool UpdatesAutoCheck { get; set; } = true;

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
