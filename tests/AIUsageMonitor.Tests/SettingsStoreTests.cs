using AIUsageMonitor.Core.Settings;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Defaults_when_file_is_missing()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(Path.Combine(dir.Path, "settings.json"));
        var s = store.Current;
        Assert.True(s.ClaudeEnabled);
        Assert.True(s.CodexEnabled);
        Assert.Equal(60, s.ClaudeRefreshSeconds);
        Assert.Equal(30, s.CodexRefreshSeconds);
        Assert.Equal(400, s.CollapseDelayMs);
        Assert.True(s.NotchVisible);
        Assert.True(s.NotifyNeedsInput && s.NotifyTurnCompleted && s.NotifyError && s.NotifyClaude && s.NotifyCodex);
        // Il fallback sui rollout di Codex resta attivo di default: serve finche' i gruppi hook di Codex non sono
        // approvati con /hooks, ed e' comunque disattivato per le sessioni i cui hook riportano i subagenti.
        Assert.True(s.CodexSubagentFallback);
    }

    [Fact]
    public void Codex_subagent_fallback_can_be_switched_off_and_survives_a_reload()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new SettingsStore(path);
        var edited = store.Current.Clone();
        edited.CodexSubagentFallback = false;
        store.Save(edited);

        Assert.False(store.Current.CodexSubagentFallback);
        Assert.False(new SettingsStore(path).Current.CodexSubagentFallback);
    }

    [Fact]
    public void Save_persists_raises_changed_and_normalizes()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "settings.json");
        var store = new SettingsStore(path);
        AppSettings? raised = null;
        store.Changed += s => raised = s;

        var edited = store.Current.Clone();
        edited.ClaudeRefreshSeconds = 5;      // below minimum 30
        edited.CodexRefreshSeconds = 9999;    // above maximum 600
        edited.MonitorIndex = -3;
        edited.NotifyCodex = false;
        store.Save(edited);

        Assert.Equal(30, store.Current.ClaudeRefreshSeconds);
        Assert.Equal(600, store.Current.CodexRefreshSeconds);
        Assert.Equal(0, store.Current.MonitorIndex);
        Assert.False(store.Current.NotifyCodex);
        Assert.False(raised!.NotifyCodex);
        Assert.False(new SettingsStore(path).Current.NotifyCodex);
    }

    [Fact]
    public void Corrupt_file_falls_back_to_defaults()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json", "{{{");
        Assert.True(new SettingsStore(path).Current.ClaudeEnabled);
    }
}
