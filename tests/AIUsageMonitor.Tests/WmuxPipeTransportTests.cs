using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIUsageMonitor.Core.Terminal;

namespace AIUsageMonitor.Tests;

public sealed class WmuxPipeTransportTests : IDisposable
{
    private readonly string _pipeName = $"wmux-test-{Guid.NewGuid():N}";
    private readonly string _tokenPath = Path.Combine(Path.GetTempPath(), $"wmux-test-token-{Guid.NewGuid():N}");

    public WmuxPipeTransportTests() => File.WriteAllText(_tokenPath, "secret-token\n");

    public void Dispose() => File.Delete(_tokenPath);

    private WmuxPipeTransport Transport(int timeoutMs = 2000) => new(_pipeName, _tokenPath, TimeSpan.FromMilliseconds(timeoutMs));

    /// <summary>Server wmux finto: accetta una connessione, legge la richiesta e risponde con le righe prodotte da <paramref name="reply"/>.</summary>
    private Task<JsonObject> ServeOnce(Func<string, string> reply) => Task.Run(async () =>
    {
        await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
        var request = JsonNode.Parse((await reader.ReadLineAsync())!)!.AsObject();
        var bytes = Encoding.UTF8.GetBytes(reply((string)request["id"]!));
        await server.WriteAsync(bytes);
        await server.FlushAsync();
        // Si chiude solo quando il client riattacca (fine dello stream), cosi' la risposta non si perde per strada.
        await reader.ReadLineAsync();
        return request;
    });

    [Fact]
    public async Task Sends_one_json_line_with_the_token_and_returns_the_result_of_the_matching_response()
    {
        var served = ServeOnce(id =>
            "\n" +
            "not json\n" +
            """{"id":"someone-else","ok":true,"result":"wrong"}""" + "\n" +
            new JsonObject { ["id"] = id, ["ok"] = true, ["result"] = new JsonObject { ["panes"] = new JsonArray() } }.ToJsonString() + "\n");

        var result = await Transport().SendAsync("pane.list", new JsonObject { ["workspaceId"] = "ws-1" }, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("""{"panes":[]}""", result!.Value.GetRawText());
        var request = await served;
        Assert.Equal("pane.list", (string)request["method"]!);
        Assert.Equal("ws-1", (string)request["params"]!["workspaceId"]!);
        Assert.Equal("secret-token", (string)request["token"]!);
        Assert.False(string.IsNullOrWhiteSpace((string)request["id"]!));
    }

    [Fact]
    public async Task A_refused_request_returns_null()
    {
        var served = ServeOnce(id => new JsonObject { ["id"] = id, ["ok"] = false, ["error"] = "unauthorized" }.ToJsonString() + "\n");

        Assert.Null(await Transport().SendAsync("workspace.list", [], CancellationToken.None));
        await served;
    }

    [Fact]
    public async Task No_wmux_running_returns_null_within_the_timeout()
    {
        var started = DateTime.UtcNow;

        Assert.Null(await Transport(timeoutMs: 300).SendAsync("workspace.list", [], CancellationToken.None));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_server_that_never_answers_returns_null_after_the_timeout()
    {
        var served = ServeOnce(_ => "");

        Assert.Null(await Transport(timeoutMs: 300).SendAsync("workspace.list", [], CancellationToken.None));
        await served;
    }

    [Fact]
    public async Task Without_the_token_file_nothing_is_sent()
    {
        File.Delete(_tokenPath);

        Assert.Null(await Transport(timeoutMs: 300).SendAsync("workspace.list", [], CancellationToken.None));
    }
}
