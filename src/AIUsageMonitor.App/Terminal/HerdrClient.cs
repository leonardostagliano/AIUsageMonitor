using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace AIUsageMonitor.App.Terminal;

/// <summary>Un pane di Herdr: id del pane, della tab, del workspace e id della sessione dell'agente che ci gira.</summary>
public sealed record HerdrPane(string PaneId, string? TabId, string? WorkspaceId, string? AgentSessionId);

/// <summary>
/// Wrapper sulla CLI di Herdr. Ogni comando e' un processo figlio nascosto con timeout di 3 s: Herdr e' opzionale,
/// quindi qualsiasi errore (eseguibile assente, JSON inatteso, timeout) si traduce in un risultato nullo e il
/// chiamante passa alla strategia successiva. Non va mai chiamato dal thread della UI.
/// </summary>
public sealed class HerdrClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly string? _exe;

    /// <summary>Chiamato con la riga di log di ogni comando; lo imposta AppServices sul FileLogger.</summary>
    public Action<string>? OnLog { get; init; }

    public HerdrClient() => _exe = ResolveExecutable();

    /// <summary>True se l'eseguibile e' stato trovato al momento della costruzione (PATH o cartella di installazione).</summary>
    public bool IsAvailable => _exe is not null;

    /// <summary>Percorso dell'eseguibile risolto, per il log diagnostico.</summary>
    public string? ExecutablePath => _exe;

    /// <summary><c>herdr agent focus &lt;target&gt;</c>. Il target accetta anche i pane id "legacy" tipo w15:p1.</summary>
    public bool FocusAgent(string target) => !string.IsNullOrWhiteSpace(target) && Run("agent", "focus", target).ExitCode == 0;

    /// <summary><c>herdr tab focus &lt;tabId&gt;</c>: ripiego quando il focus dell'agente non va a buon fine.</summary>
    public bool FocusTab(string tabId) => !string.IsNullOrWhiteSpace(tabId) && Run("tab", "focus", tabId).ExitCode == 0;

    /// <summary><c>herdr pane get &lt;paneId&gt;</c> → pane, o null se il pane non esiste piu'.</summary>
    public HerdrPane? GetPane(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId)) return null;
        var (exitCode, stdout) = Run("pane", "get", paneId);
        if (exitCode != 0) return null;
        return TryParse(stdout, root =>
            root.TryGetProperty("result", out var result) && result.TryGetProperty("pane", out var pane)
                ? ReadPane(pane)
                : null);
    }

    /// <summary><c>herdr pane process-info --pane &lt;paneId&gt;</c> → pid della shell del pane, da cui parte la risalita.</summary>
    public int? ShellPid(string paneId)
    {
        if (string.IsNullOrWhiteSpace(paneId)) return null;
        var (exitCode, stdout) = Run("pane", "process-info", "--pane", paneId);
        if (exitCode != 0) return null;
        return TryParse<int?>(stdout, root =>
            root.TryGetProperty("result", out var result)
            && result.TryGetProperty("process_info", out var info)
            && info.TryGetProperty("shell_pid", out var pid)
            && pid.TryGetInt32(out var value)
            && value > 0
                ? value
                : null);
    }

    /// <summary>
    /// <c>herdr api snapshot</c> → primo pane il cui <c>agent_session.value</c> e' la sessione cercata. E' la via
    /// d'uscita per le sessioni iniziate prima di questa versione, che non hanno ancora un <c>host</c> negli eventi.
    /// </summary>
    public string? FindPaneBySession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        var (exitCode, stdout) = Run("api", "snapshot");
        if (exitCode != 0) return null;
        return TryParse(stdout, root =>
        {
            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("snapshot", out var snapshot)) return null;
            // "panes" descrive la disposizione corrente, "agents" solo quelli riconosciuti: si guardano entrambi.
            foreach (var name in (ReadOnlySpan<string>)["panes", "agents"])
            {
                if (!snapshot.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) continue;
                foreach (var element in array.EnumerateArray())
                {
                    var pane = ReadPane(element);
                    if (pane is not null && string.Equals(pane.AgentSessionId, sessionId, StringComparison.OrdinalIgnoreCase)) return pane.PaneId;
                }
            }
            return null;
        });
    }

    private static HerdrPane? ReadPane(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var paneId = Text(element, "pane_id");
        if (paneId is null) return null;
        string? session = null;
        if (element.TryGetProperty("agent_session", out var agentSession) && agentSession.ValueKind == JsonValueKind.Object)
            session = Text(agentSession, "value");
        return new HerdrPane(paneId, Text(element, "tab_id"), Text(element, "workspace_id"), session);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() is { Length: > 0 } text ? text : null
            : null;

    private T? TryParse<T>(string stdout, Func<JsonElement, T?> read)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            return read(document.RootElement);
        }
        catch (JsonException ex)
        {
            OnLog?.Invoke($"herdr: risposta non interpretabile ({ex.Message})");
            return default;
        }
    }

    /// <summary>Esegue la CLI nascosta e ne raccoglie lo stdout. Exit code -1 quando non parte, -2 quando scade.</summary>
    private (int ExitCode, string Stdout) Run(params string[] args)
    {
        if (_exe is null) return (-1, "");
        var info = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return (-1, "");
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                // Un comando appeso non deve trattenere un thread: si uccide l'albero e si prosegue.
                try { process.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
                OnLog?.Invoke($"herdr {string.Join(' ', args)}: timeout dopo {Timeout.TotalSeconds:0}s");
                return (-2, "");
            }
            // Dopo l'uscita del processo le pipe si chiudono: le attese qui sotto sono immediate.
            var output = Wait(stdout);
            var error = Wait(stderr);
            OnLog?.Invoke($"herdr {string.Join(' ', args)}: exit {process.ExitCode}{(process.ExitCode == 0 || error.Length == 0 ? "" : $" {Trim(error)}")}");
            return (process.ExitCode, output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            OnLog?.Invoke($"herdr {string.Join(' ', args)}: {ex.Message}");
            return (-1, "");
        }

        static string Wait(Task<string> task)
        {
            try { return task.Wait(Timeout) ? task.Result : ""; }
            catch (AggregateException) { return ""; }
        }

        static string Trim(string text) => text.Length <= 200 ? text.ReplaceLineEndings(" ").Trim() : text[..200].ReplaceLineEndings(" ").Trim();
    }

    /// <summary>
    /// herdr.exe sul PATH, altrimenti la cartella di installazione per utente. Il lanciatore senza estensione che
    /// accompagna l'eseguibile e' uno script di shell e <c>CreateProcess</c> non lo sa eseguire: si cerca solo l'exe.
    /// </summary>
    private static string? ResolveExecutable()
    {
        try
        {
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim('"'), "herdr.exe"); }
                catch (ArgumentException) { continue; } // voce del PATH con caratteri non validi
                if (File.Exists(candidate)) return candidate;
            }
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Herdr", "bin", "herdr.exe");
            return File.Exists(installed) ? installed : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
