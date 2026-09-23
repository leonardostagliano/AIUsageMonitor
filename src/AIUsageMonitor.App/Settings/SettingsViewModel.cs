using System.Diagnostics;
using System.IO;
using System.Windows.Input;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.App.Updates;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Settings;

namespace AIUsageMonitor.App.Settings;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _services;
    private AppSettings _draft;
    private bool _autoStart;
    private string _claudeHookStatus = "";
    private string _codexHookStatus = "";

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        _draft = services.Settings.Current.Clone();
        _autoStart = AutoStart.IsEnabled();
        // Stessa enumerazione di NotchWindow.Reposition (primario per primo): MonitorIndex e' un indice in QUELLA lista,
        // non in Screen.AllScreens, che Windows non garantisce inizi dal monitor primario. Usare AllScreens qui
        // metterebbe "(principale)" sulla riga sbagliata e ancorerebbe la notch a un monitor diverso da quello scelto.
        Monitors = NotchWindow.EnumerateScreens().Select((s, i) => $"{i + 1}: {s.DeviceName.TrimStart('\\', '.')} {s.Bounds.Width}x{s.Bounds.Height}{(s.Primary ? " (principale)" : "")}").ToList();

        SaveCommand = new RelayCommand(Save);
        InstallClaudeHooksCommand = new RelayCommand(() => Install(AgentKind.Claude));
        RemoveClaudeHooksCommand = new RelayCommand(() => Remove(AgentKind.Claude));
        InstallCodexHooksCommand = new RelayCommand(() => Install(AgentKind.Codex));
        RemoveCodexHooksCommand = new RelayCommand(() => Remove(AgentKind.Codex));
        OpenEventsFolderCommand = new RelayCommand(() => OpenFolder(services.Paths.MonitorDir));
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(services.Paths.LogsDir));
        Updates = new UpdatesViewModel(services.Updates, services.Log);
        RefreshHookStatus();
    }

    public event Action? Saved;

    /// <summary>Gruppo AGGIORNAMENTI: stato e comandi dal vivo, fuori dalla bozza (solo la preferenza passa da Salva).</summary>
    public UpdatesViewModel Updates { get; }

    public IReadOnlyList<string> Monitors { get; }
    public ICommand SaveCommand { get; }
    public ICommand InstallClaudeHooksCommand { get; }
    public ICommand RemoveClaudeHooksCommand { get; }
    public ICommand InstallCodexHooksCommand { get; }
    public ICommand RemoveCodexHooksCommand { get; }
    public ICommand OpenEventsFolderCommand { get; }
    public ICommand OpenLogsFolderCommand { get; }

    public bool ClaudeEnabled { get => _draft.ClaudeEnabled; set { _draft.ClaudeEnabled = value; Raise(); } }
    public bool CodexEnabled { get => _draft.CodexEnabled; set { _draft.CodexEnabled = value; Raise(); } }
    public int ClaudeRefreshSeconds { get => _draft.ClaudeRefreshSeconds; set { _draft.ClaudeRefreshSeconds = value; Raise(); } }
    public int CodexRefreshSeconds { get => _draft.CodexRefreshSeconds; set { _draft.CodexRefreshSeconds = value; Raise(); } }
    public bool CodexSubagentFallback { get => _draft.CodexSubagentFallback; set { _draft.CodexSubagentFallback = value; Raise(); } }
    public int MonitorIndex { get => Math.Min(_draft.MonitorIndex, Math.Max(0, Monitors.Count - 1)); set { _draft.MonitorIndex = value; Raise(); } }
    public int VerticalOffset { get => _draft.VerticalOffset; set { _draft.VerticalOffset = value; Raise(); } }
    public int CollapseDelayMs { get => _draft.CollapseDelayMs; set { _draft.CollapseDelayMs = value; Raise(); } }
    public bool Compact { get => _draft.Compact; set { _draft.Compact = value; Raise(); } }
    public bool NotifyNeedsInput { get => _draft.NotifyNeedsInput; set { _draft.NotifyNeedsInput = value; Raise(); } }
    public bool NotifyTurnCompleted { get => _draft.NotifyTurnCompleted; set { _draft.NotifyTurnCompleted = value; Raise(); } }
    public bool NotifyError { get => _draft.NotifyError; set { _draft.NotifyError = value; Raise(); } }
    public bool NotifyClaude { get => _draft.NotifyClaude; set { _draft.NotifyClaude = value; Raise(); } }
    public bool NotifyCodex { get => _draft.NotifyCodex; set { _draft.NotifyCodex = value; Raise(); } }
    public bool UpdatesAutoCheck { get => _draft.UpdatesAutoCheck; set { _draft.UpdatesAutoCheck = value; Raise(); } }
    public bool AutoStartEnabled { get => _autoStart; set => Set(ref _autoStart, value); }
    public string ClaudeHookStatus { get => _claudeHookStatus; private set => Set(ref _claudeHookStatus, value); }
    public string CodexHookStatus { get => _codexHookStatus; private set => Set(ref _codexHookStatus, value); }
    public string EventsFile => _services.Paths.EventsFile;

    private void Save()
    {
        // NotchVisible non e' esposto qui: il menu tray puo' averlo cambiato mentre la finestra era aperta,
        // quindi si riprende il valore corrente invece di riscrivere quello catturato all'apertura.
        _draft.NotchVisible = _services.Settings.Current.NotchVisible;
        _services.Settings.Save(_draft);
        try { AutoStart.SetEnabled(_autoStart); }
        catch (Exception ex) { _services.Log.Error("AutoStart change failed", ex); }
        Saved?.Invoke();
    }

    // Install passa da AppServices.InstallHooks: unico punto che logga, invalida la cache dello stato e alza il Notice
    // (indispensabile per Codex, che esegue i gruppi solo dopo l'approvazione con /hooks).
    private void Install(AgentKind agent)
    {
        _services.InstallHooks(agent);
        RefreshHookStatus();
    }

    private void Remove(AgentKind agent)
    {
        try { _services.Hooks.Remove(agent); }
        catch (Exception ex) { _services.Log.Error($"Hook remove {agent} failed", ex); }
        _services.InvalidateHookStatus();
        RefreshHookStatus();
    }

    private void RefreshHookStatus()
    {
        ClaudeHookStatus = Describe(_services.HookStatus(AgentKind.Claude));
        CodexHookStatus = Describe(_services.HookStatus(AgentKind.Codex));
    }

    private static string Describe(HookStatusReport report) => report.Status switch
    {
        HookStatus.Installed => $"Installati ({report.Detail})",
        HookStatus.Partial => $"Parziali ({report.Detail})",
        HookStatus.NotInstalled => $"Non installati ({report.Detail})",
        HookStatus.ConfigMissing => "File di configurazione assente: verrà creato all'installazione",
        HookStatus.ConfigInvalid => $"Attenzione: {report.Detail}",
        _ => report.Detail
    };

    public void Dispose() => Updates.Dispose();

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception) { /* best effort */ }
    }
}
