using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Core.Terminal;

/// <summary>
/// Invia una richiesta a wmux e restituisce il suo <c>result</c>, oppure null se wmux non gira, rifiuta la richiesta
/// o risponde <c>ok:false</c>. Non solleva eccezioni: wmux e' opzionale e chi chiama passa alla strategia successiva.
/// </summary>
public delegate Task<JsonElement?> WmuxTransport(string method, JsonObject parameters, CancellationToken cancellationToken);

/// <summary>
/// Porta il focus sul pane di wmux che ospita una sessione. L'unico riferimento stabile e' il <c>WMUX_PTY_ID</c>
/// registrato dall'hook: il workspace si ricava da <c>workspace.list</c> (ogni workspace elenca i suoi pty), il pane
/// e la tab da <c>pane.list</c>. Il workspace va messo a fuoco per primo, perche' <c>pane.focus</c> da solo segna il
/// pane come attivo ma non cambia lo schermo se il pane sta in un altro workspace.
/// </summary>
public sealed class WmuxClient
{
    private readonly WmuxTransport _send;
    private readonly Action<string>? _log;

    public WmuxClient(WmuxTransport send, Action<string>? log = null)
    {
        _send = send;
        _log = log;
    }

    /// <summary>True se il pane del pty e' diventato quello attivo di wmux.</summary>
    public async Task<bool> FocusPtyAsync(string ptyId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ptyId)) return false;

        var workspaces = await _send("workspace.list", [], cancellationToken).ConfigureAwait(false);
        var workspaceId = workspaces is { } list ? FindWorkspace(list, ptyId) : null;
        if (workspaceId is null)
        {
            _log?.Invoke(workspaces is null ? "wmux: nessuna risposta a workspace.list" : $"wmux: nessun workspace contiene il pty {ptyId}");
            return false;
        }
        if (await _send("workspace.focus", new JsonObject { ["id"] = workspaceId }, cancellationToken).ConfigureAwait(false) is null)
        {
            _log?.Invoke($"wmux: workspace.focus {workspaceId} rifiutato");
            return false;
        }

        var panes = await _send("pane.list", new JsonObject { ["workspaceId"] = workspaceId }, cancellationToken).ConfigureAwait(false);
        if ((panes is { } paneList ? FindPane(paneList, ptyId) : null) is not { } found)
        {
            _log?.Invoke($"wmux: nessun pane del workspace {workspaceId} ospita il pty {ptyId}");
            return false;
        }
        var (paneId, surfaceId) = found;
        if (await _send("pane.focus", new JsonObject { ["id"] = paneId }, cancellationToken).ConfigureAwait(false) is null)
        {
            _log?.Invoke($"wmux: pane.focus {paneId} rifiutato");
            return false;
        }
        // Un pane puo' avere piu' tab: la tab dell'agente e' nota solo se wmux l'ha riconosciuto come tale. Senza, il
        // pane resta sulla tab che aveva, che e' comunque il pane giusto.
        if (surfaceId is not null && await _send("surface.focus", new JsonObject { ["id"] = surfaceId }, cancellationToken).ConfigureAwait(false) is null)
            _log?.Invoke($"wmux: surface.focus {surfaceId} rifiutato, resta la tab corrente del pane");
        return true;
    }

    /// <summary>Id del workspace il cui <c>ptyIds</c> contiene il pty.</summary>
    private static string? FindWorkspace(JsonElement workspaces, string ptyId)
    {
        if (workspaces.ValueKind != JsonValueKind.Array) return null;
        foreach (var workspace in workspaces.EnumerateArray())
        {
            if (workspace.ValueKind == JsonValueKind.Object && Contains(workspace, "ptyIds", ptyId) && GetString(workspace, "id") is { } id)
                return id;
        }
        return null;
    }

    /// <summary>Pane il cui <c>surfacePtyIds</c> contiene il pty, con la tab presa dall'elenco degli agenti.</summary>
    private static (string PaneId, string? SurfaceId)? FindPane(JsonElement result, string ptyId)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("panes", out var panes) || panes.ValueKind != JsonValueKind.Array) return null;
        foreach (var pane in panes.EnumerateArray())
        {
            if (pane.ValueKind != JsonValueKind.Object || !Contains(pane, "surfacePtyIds", ptyId) || GetString(pane, "id") is not { } paneId) continue;
            string? surfaceId = null;
            if (pane.TryGetProperty("agents", out var agents) && agents.ValueKind == JsonValueKind.Array)
            {
                foreach (var agent in agents.EnumerateArray())
                {
                    if (agent.ValueKind == JsonValueKind.Object && GetString(agent, "ptyId") == ptyId) { surfaceId = GetString(agent, "surfaceId"); break; }
                }
            }
            return (paneId, surfaceId);
        }
        return null;
    }

    private static bool Contains(JsonElement obj, string arrayName, string value) =>
        obj.TryGetProperty(arrayName, out var array)
        && array.ValueKind == JsonValueKind.Array
        && array.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == value);

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
