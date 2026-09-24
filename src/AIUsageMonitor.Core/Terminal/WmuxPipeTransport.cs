using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AIUsageMonitor.Core.Terminal;

/// <summary>
/// Canale verso wmux sulla sua named pipe di controllo (<c>\\.\pipe\wmux-&lt;utente&gt;</c>), la stessa che usa la CLI
/// <c>wmux</c>: una connessione per richiesta, una riga JSON <c>{id, method, params, token}</c> in uscita e righe JSON
/// in entrata fino a quella con lo stesso id. La CLI costa circa 2 s a comando (avvia Electron in modalita' node),
/// la pipe pochi millisecondi: al click conta. Il token sta in <c>%USERPROFILE%\.wmux-auth-token</c>, leggibile solo
/// dall'utente. La richiesta non porta un <c>clientName</c>: con un nome proprio wmux tratterebbe l'app come un
/// plugin da confermare e rifiuterebbe ogni metodo.
/// </summary>
public sealed class WmuxPipeTransport
{
    private readonly string _pipeName;
    private readonly string _tokenPath;
    private readonly TimeSpan _timeout;
    private readonly Action<string>? _log;

    public WmuxPipeTransport(string pipeName, string tokenPath, TimeSpan timeout, Action<string>? log = null)
    {
        _pipeName = pipeName;
        _tokenPath = tokenPath;
        _timeout = timeout;
        _log = log;
    }

    /// <summary>Pipe e token dell'utente corrente, con 2 s per richiesta: il click non deve restare appeso a wmux.</summary>
    public static WmuxPipeTransport ForCurrentUser(Action<string>? log = null) => new(
        $"wmux-{Environment.UserName}",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wmux-auth-token"),
        TimeSpan.FromSeconds(2),
        log);

    /// <inheritdoc cref="WmuxTransport"/>
    public async Task<JsonElement?> SendAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = (await File.ReadAllTextAsync(_tokenPath, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"wmux: token non leggibile ({ex.GetType().Name}), wmux non installato o non avviato");
            return null;
        }
        if (token.Length == 0) return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);

            var id = Guid.NewGuid().ToString();
            var request = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters.DeepClone(), ["token"] = token };
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), timeout.Token).ConfigureAwait(false);

            // Sulla stessa connessione possono arrivare righe che non sono la risposta (notifiche, altri id): si
            // scartano finche' non arriva quella con il nostro id o la pipe si chiude.
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
            {
                if (ParseResponse(line, id) is not { } response) continue;
                if (response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                {
                    // I comandi di focus possono rispondere senza "result": l'esito e' comunque positivo.
                    return response.TryGetProperty("result", out var result) ? result.Clone() : NullElement();
                }
                _log?.Invoke($"wmux: {method} rifiutato: {(response.TryGetProperty("error", out var error) ? error.ToString() : "nessun dettaglio")}");
                return null;
            }
            _log?.Invoke($"wmux: {method} senza risposta, connessione chiusa");
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke($"wmux: {method} scaduto dopo {_timeout.TotalMilliseconds:0} ms");
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            _log?.Invoke($"wmux: {method} fallito: {ex.Message}");
            return null;
        }
    }

    /// <summary>La risposta con l'id cercato, oppure null per righe vuote, non JSON o destinate ad altri.</summary>
    private static JsonElement? ParseResponse(string line, string id)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("id", out var responseId)
                && responseId.ValueKind == JsonValueKind.String
                && responseId.GetString() == id
                    ? root.Clone()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement NullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }
}
