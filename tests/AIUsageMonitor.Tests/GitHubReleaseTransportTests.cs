using System.Net;
using System.Security.Cryptography;
using System.Text;
using AIUsageMonitor.Core.Updates;
using AIUsageMonitor.Tests.Helpers;

namespace AIUsageMonitor.Tests;

public class GitHubReleaseTransportTests
{
    private const string Token = "gho_test_token_123";
    private const string Api = UpdateSource.ApiRoot;
    private const string Cdn = "https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sp=r&sig=SECRET_SIGNATURE";

    // ---- Infrastruttura -------------------------------------------------------------------------------------------

    /// <summary>Richiesta registrata al momento dell'invio (header copiati: la richiesta viene poi eliminata).</summary>
    private sealed record Sent(Uri Url, Dictionary<string, string> Headers)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Handler finto che risponde in modo asincrono e registra ogni passaggio, redirect compresi.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;
        private readonly List<Sent> _sent = new();

        public StubHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public StubHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
            : this((request, index, _) => Task.FromResult(respond(request, index))) { }

        public bool Disposed { get; private set; }

        public IReadOnlyList<Sent> Sent
        {
            get { lock (_sent) return _sent.ToList(); }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int index;
            lock (_sent)
            {
                index = _sent.Count;
                _sent.Add(new Sent(request.RequestUri!,
                    request.Headers.NonValidated.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase)));
            }
            return _respond(request, index, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>Corpo a blocchi, non posizionabile (niente Content-Length), con pause, errori o blocchi infiniti.</summary>
    private sealed class ChunkStream : Stream
    {
        private readonly Queue<byte[]> _chunks;
        private readonly Exception? _failAtEnd;
        private readonly bool _stallAtEnd;
        private byte[] _current = [];
        private int _offset;

        public ChunkStream(IEnumerable<byte[]> chunks, Exception? failAtEnd = null, bool stallAtEnd = false)
        {
            _chunks = new Queue<byte[]>(chunks);
            _failAtEnd = failAtEnd;
            _stallAtEnd = stallAtEnd;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length == 0) return 0;
            if (_offset == _current.Length)
            {
                if (_chunks.Count == 0)
                {
                    if (_stallAtEnd) await Task.Delay(Timeout.Infinite, cancellationToken);
                    if (_failAtEnd is not null) throw _failAtEnd;
                    return 0;
                }
                _current = _chunks.Dequeue();
                _offset = 0;
            }
            // Un blocco piu' grande del buffer del lettore viene consegnato in piu' letture, come farebbe un socket.
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static GitHubReleaseTransport Transport(StubHandler handler, TimeSpan? request = null, TimeSpan? download = null,
        TimeSpan? idle = null, TimeSpan? errorBody = null) =>
        new(handler)
        {
            RequestTimeout = request ?? TimeSpan.FromSeconds(30),
            DownloadTimeout = download ?? TimeSpan.FromMinutes(10),
            IdleTimeout = idle ?? TimeSpan.FromSeconds(30),
            ErrorBodyTimeout = errorBody ?? TimeSpan.FromSeconds(2)
        };

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };

    /// <summary>200 senza Content-Length (trasferimento a blocchi), eventualmente con un Content-Length dichiarato a mano.</summary>
    private static HttpResponseMessage Chunked(ChunkStream stream, long? declaredLength = null)
    {
        var content = new StreamContent(stream);
        if (declaredLength is not null) content.Headers.ContentLength = declaredLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(string? location, HttpStatusCode status = HttpStatusCode.Found)
    {
        var response = new HttpResponseMessage(status);
        if (location is not null) response.Headers.TryAddWithoutValidation("Location", location);
        return response;
    }

    private static HttpResponseMessage Status(int status, string? body = null, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (body is not null) response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private static byte[] Payload(int size)
    {
        var bytes = new byte[size];
        for (var i = 0; i < size; i++) bytes[i] = (byte)(i * 31 + 7);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        return bytes;
    }

    private static IEnumerable<byte[]> Split(byte[] bytes, int chunk)
    {
        for (var offset = 0; offset < bytes.Length; offset += chunk)
            yield return bytes[offset..Math.Min(bytes.Length, offset + chunk)];
    }

    private static async Task<UpdateException> Fails(Func<Task> action)
    {
        var error = await Record.ExceptionAsync(action);
        Assert.NotNull(error);
        return Assert.IsAssignableFrom<UpdateException>(error);
    }

    private static void AssertNoSecrets(Exception error)
    {
        Assert.DoesNotContain(Token, error.Message);
        Assert.DoesNotContain("SECRET", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("://", error.Message);
    }

    // ---- Header, token e redirect -------------------------------------------------------------------------------

    [Fact]
    public async Task ReadJson_sends_the_github_api_headers_and_the_token_to_the_api()
    {
        var handler = new StubHandler((_, _) => Ok("""[{"id":1}]"""));
        using var transport = Transport(handler);

        using var document = await transport.ReadJsonAsync("/releases?per_page=100&page=1", Token, CancellationToken.None);

        Assert.Equal(1, document.RootElement[0].GetProperty("id").GetInt32());
        var sent = Assert.Single(handler.Sent);
        Assert.Equal(new Uri(Api + "/releases?per_page=100&page=1"), sent.Url);
        Assert.Equal("AIUsageMonitor-Updater", sent.Header("User-Agent"));
        Assert.Equal("application/vnd.github+json", sent.Header("Accept"));
        Assert.Equal("identity", sent.Header("Accept-Encoding"));
        Assert.Equal(GitHubReleaseTransport.ApiVersion, sent.Header("X-GitHub-Api-Version"));
        Assert.Equal("Bearer " + Token, sent.Header("Authorization"));
    }

    [Fact]
    public async Task Without_a_token_no_authorization_header_is_sent()
    {
        var handler = new StubHandler((_, _) => Ok("[]"));
        using var transport = Transport(handler);

        using var _ = await transport.ReadJsonAsync("/releases/5", "", CancellationToken.None);

        var sent = Assert.Single(handler.Sent);
        Assert.Null(sent.Header("Authorization"));
        Assert.Equal(GitHubReleaseTransport.ApiVersion, sent.Header("X-GitHub-Api-Version"));
    }

    [Fact]
    public async Task The_token_never_follows_a_redirect_to_the_cdn()
    {
        var payload = Payload(4096);
        var handler = new StubHandler((request, index) => index == 0 ? Redirect(Cdn) : Ok(payload));
        using var transport = Transport(handler);

        var bytes = await transport.ReadAssetBytesAsync(42, Token, 1024 * 1024, CancellationToken.None);

        Assert.Equal(payload, bytes);
        Assert.Equal(2, handler.Sent.Count);
        var api = handler.Sent[0];
        Assert.Equal(new Uri(Api + "/releases/assets/42"), api.Url);
        Assert.Equal("application/octet-stream", api.Header("Accept"));
        Assert.Equal("Bearer " + Token, api.Header("Authorization"));
        var cdn = handler.Sent[1];
        Assert.Equal(new Uri(Cdn), cdn.Url);
        Assert.Null(cdn.Header("Authorization"));
        Assert.Null(cdn.Header("X-GitHub-Api-Version"));
        Assert.Equal("AIUsageMonitor-Updater", cdn.Header("User-Agent"));
        Assert.Equal("application/octet-stream", cdn.Header("Accept"));
        Assert.Equal("identity", cdn.Header("Accept-Encoding"));
    }

    [Fact]
    public async Task A_relative_location_is_resolved_on_the_current_url_and_keeps_the_token_on_the_api()
    {
        var handler = new StubHandler((request, index) => index == 0
            ? Redirect("/repos/leonardostagliano/AIUsageMonitor/releases/assets/99", HttpStatusCode.TemporaryRedirect)
            : Ok(new byte[] { 1, 2, 3 }));
        using var transport = Transport(handler);

        var bytes = await transport.ReadAssetBytesAsync(42, Token, 10, CancellationToken.None);

        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.Equal(new Uri(Api + "/releases/assets/99"), handler.Sent[1].Url);
        Assert.Equal("Bearer " + Token, handler.Sent[1].Header("Authorization"));
    }

    [Theory]
    [InlineData(301)]
    [InlineData(302)]
    [InlineData(303)]
    [InlineData(307)]
    [InlineData(308)]
    public async Task Every_redirect_status_is_followed(int status)
    {
        var handler = new StubHandler((_, index) => index == 0 ? Redirect(Cdn, (HttpStatusCode)status) : Ok(new byte[] { 9 }));
        using var transport = Transport(handler);

        Assert.Equal(new byte[] { 9 }, await transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));
        Assert.Equal(2, handler.Sent.Count);
    }

    [Fact]
    public async Task A_github_download_url_is_followed_for_assets()
    {
        var download = "https://github.com/leonardostagliano/AIUsageMonitor/releases/download/v1.2.3/AIUsageMonitor-1.2.3-win-x64.exe";
        var handler = new StubHandler((_, index) => index switch
        {
            0 => Redirect(download),
            1 => Redirect(Cdn),
            _ => Ok(new byte[] { 7 })
        });
        using var transport = Transport(handler);

        Assert.Equal(new byte[] { 7 }, await transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));
        Assert.Null(handler.Sent[1].Header("Authorization"));
        Assert.Null(handler.Sent[2].Header("Authorization"));
    }

    [Fact]
    public async Task Five_redirects_are_followed_and_a_sixth_is_refused()
    {
        var five = new StubHandler((_, index) => index < 5 ? Redirect($"{Api}/releases/assets/{index + 100}") : Ok(new byte[] { 1 }));
        using (var transport = Transport(five))
            Assert.Equal(new byte[] { 1 }, await transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));
        Assert.Equal(6, five.Sent.Count);

        var endless = new StubHandler((_, index) => Redirect($"{Api}/releases/assets/{index + 100}"));
        using (var transport = Transport(endless))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));
            Assert.Equal("UPDATES_URL", error.Code);
            Assert.Equal(UpdateMessages.UrlNotAllowed, error.Message);
        }
        Assert.Equal(6, endless.Sent.Count);
    }

    [Theory]
    [InlineData("https://evil.example.com/steal")]
    [InlineData("http://release-assets.githubusercontent.com/x")]
    [InlineData("//evil.example.com/x")]
    [InlineData("https://api.github.com/repos/someone/other/releases/assets/1")]
    [InlineData("https://api.github.com/repos/leonardostagliano/AIUsageMonitor/releases/%2e%2e/%2e%2e/other/releases")]
    [InlineData("https://api.github.com.evil/repos/leonardostagliano/AIUsageMonitor/releases/assets/1")]
    public async Task A_redirect_outside_the_policy_is_refused_without_contacting_it(string location)
    {
        var handler = new StubHandler((_, index) => index == 0 ? Redirect(location) : Ok(new byte[] { 1 }));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));

        Assert.Equal("UPDATES_URL", error.Code);
        Assert.Single(handler.Sent);
        AssertNoSecrets(error);
    }

    [Fact]
    public async Task Metadata_requests_do_not_follow_a_redirect_to_the_cdn()
    {
        var handler = new StubHandler((_, index) => index == 0 ? Redirect(Cdn) : Ok("[]"));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        Assert.Equal("UPDATES_URL", error.Code);
        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task A_redirect_without_location_or_with_an_invalid_one_is_refused()
    {
        using (var transport = Transport(new StubHandler((_, _) => Redirect(null))))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));
            Assert.Equal("UPDATES_URL", error.Code);
            Assert.Equal(UpdateMessages.RedirectWithoutTarget, error.Message);
        }
        using (var transport = Transport(new StubHandler((_, _) => Redirect("   "))))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));
            Assert.Equal(UpdateMessages.RedirectWithoutTarget, error.Message);
        }
        using (var transport = Transport(new StubHandler((_, _) => Redirect("https://[not-a-host"))))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));
            Assert.Equal("UPDATES_URL", error.Code);
            Assert.Equal(UpdateMessages.RedirectInvalid, error.Message);
        }
    }

    [Theory]
    [InlineData("releases")]
    [InlineData("")]
    [InlineData("/../../../user")]
    [InlineData("/%2e%2e/%2e%2e/other/releases")]
    [InlineData("/../AIUsageMonitor-fork/releases")]
    [InlineData("@evil.example.com/releases")]
    public async Task ReadJson_refuses_paths_outside_the_release_api(string relativePath)
    {
        var handler = new StubHandler((_, _) => Ok("[]"));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadJsonAsync(relativePath, Token, CancellationToken.None));

        Assert.Equal("UPDATES_URL", error.Code);
        Assert.Empty(handler.Sent);
    }

    [Theory]
    [InlineData("tok\r\nX-Injected: 1")]
    [InlineData("tok en")]
    [InlineData("tok\u00E8")]
    public async Task A_malformed_token_is_never_sent(string token)
    {
        var handler = new StubHandler((_, _) => Ok("[]"));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadJsonAsync("/releases", token, CancellationToken.None));

        Assert.Equal("UPDATES_AUTH_REQUIRED", error.Code);
        Assert.Empty(handler.Sent);
        Assert.DoesNotContain("tok", error.Message);
    }

    // ---- Classificazione degli errori ---------------------------------------------------------------------------

    [Fact]
    public async Task Status_429_is_a_rate_limit_on_any_host()
    {
        using (var transport = Transport(new StubHandler((_, _) => Status(429))))
        {
            var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));
            Assert.Equal("UPDATES_RATE_LIMIT", error.Code);
            Assert.Equal(UpdateMessages.RateLimit(429), error.Message);
        }
        using (var transport = Transport(new StubHandler((_, index) => index == 0 ? Redirect(Cdn) : Status(429))))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));
            Assert.Equal("UPDATES_RATE_LIMIT", error.Code);
        }
    }

    [Fact]
    public async Task Status_403_with_no_remaining_requests_is_a_rate_limit()
    {
        var handler = new StubHandler((_, _) => Status(403, """{"message":"Forbidden"}""", ("X-RateLimit-Remaining", "0")));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        Assert.Equal("UPDATES_RATE_LIMIT", error.Code);
        Assert.Equal(UpdateMessages.RateLimit(403), error.Message);
    }

    [Fact]
    public async Task Status_401_from_the_api_is_a_credentials_problem()
    {
        using var transport = Transport(new StubHandler((_, _) => Status(401, """{"message":"Bad credentials"}""")));

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        var access = Assert.IsType<UpdateAccessException>(error);
        Assert.Equal("UPDATES_ACCESS", access.Code);
        Assert.Equal(401, access.Status);
        Assert.Equal(UpdateAccessReason.Credentials, access.Reason);
        Assert.Equal(UpdateMessages.AccessPrefix(401, UpdateMessages.AccessCredentials), access.Message);
        Assert.DoesNotContain("Bad credentials", access.Message);
    }

    [Fact]
    public async Task Status_404_from_the_api_is_not_found()
    {
        using var transport = Transport(new StubHandler((_, _) => Status(404, """{"message":"Not Found"}""")));

        var error = await Fails(() => transport.ReadJsonAsync("/releases/5", Token, CancellationToken.None));

        var access = Assert.IsType<UpdateAccessException>(error);
        Assert.Equal(404, access.Status);
        Assert.Equal(UpdateAccessReason.NotFound, access.Reason);
    }

    [Theory]
    [InlineData("required; url=https://github.com/orgs/acme/sso?authorization_request=SECRET")]
    [InlineData("REQUIRED")]
    [InlineData("required")]
    public async Task Status_403_with_the_sso_header_is_an_sso_problem(string header)
    {
        var handler = new StubHandler((_, _) => Status(403, """{"message":"Resource protected by organization SAML enforcement."}""", ("X-GitHub-SSO", header)));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        var access = Assert.IsType<UpdateAccessException>(error);
        Assert.Equal(UpdateAccessReason.Sso, access.Reason);
        Assert.Equal(403, access.Status);
        AssertNoSecrets(access);
    }

    [Fact]
    public async Task An_sso_header_that_is_not_required_falls_back_to_the_body()
    {
        var handler = new StubHandler((_, _) => Status(403, """{"message":"denied"}""", ("X-GitHub-SSO", "partial-results; organizations=1")));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        Assert.Equal(UpdateAccessReason.Forbidden, Assert.IsType<UpdateAccessException>(error).Reason);
    }

    [Theory]
    [InlineData("You have exceeded a secondary rate limit. Please wait a few minutes.", null)]
    [InlineData("API rate limit exceeded for user ID 1.", null)]
    [InlineData("You have triggered an abuse detection mechanism.", null)]
    [InlineData("Although you appear to have the correct authorization credentials, the `acme` organization has enabled OAuth App access restrictions.", UpdateAccessReason.OAuthPolicy)]
    [InlineData("The organization has enabled OAuth Application access restrictions", UpdateAccessReason.OAuthPolicy)]
    [InlineData("Blocked by third-party application restrictions", UpdateAccessReason.OAuthPolicy)]
    [InlineData("Blocked by third party application restrictions", UpdateAccessReason.OAuthPolicy)]
    [InlineData("Resource not accessible by personal access token", UpdateAccessReason.Permissions)]
    [InlineData("Resource not accessible by integration", UpdateAccessReason.Permissions)]
    [InlineData("Must have admin rights to Repository.", UpdateAccessReason.Forbidden)]
    public async Task Status_403_is_classified_from_the_json_message(string message, UpdateAccessReason? reason)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { message, documentation_url = "https://docs.github.com/SECRET" });
        using var transport = Transport(new StubHandler((_, _) => Status(403, body)));

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        if (reason is null)
        {
            Assert.Equal("UPDATES_RATE_LIMIT", error.Code);
        }
        else
        {
            var access = Assert.IsType<UpdateAccessException>(error);
            Assert.Equal(reason.Value, access.Reason);
            Assert.Equal(403, access.Status);
        }
        Assert.DoesNotContain(message, error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoSecrets(error);
    }

    [Fact]
    public async Task Status_403_bodies_that_cannot_be_read_are_simply_forbidden()
    {
        var huge = "{\"message\":\"secondary rate limit\",\"padding\":\"" + new string('x', 17 * 1024) + "\"}";
        var cases = new Func<HttpResponseMessage>[]
        {
            () => Status(403, "not json at all: secondary rate limit"),
            () => Status(403, """{"message":42}"""),
            () => Status(403, """["secondary rate limit"]"""),
            () => Status(403),
            // Oltre 16 KB il messaggio non viene nemmeno letto, con o senza Content-Length.
            () => Status(403, huge),
            () => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StreamContent(new ChunkStream(Split(Encoding.UTF8.GetBytes(huge), 1000)))
            }
        };
        foreach (var response in cases)
        {
            using var transport = Transport(new StubHandler((_, _) => response()));
            var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));
            Assert.Equal(UpdateAccessReason.Forbidden, Assert.IsType<UpdateAccessException>(error).Reason);
        }
    }

    [Fact]
    public async Task A_403_body_that_never_ends_is_abandoned_after_the_error_timeout()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StreamContent(new ChunkStream([Encoding.UTF8.GetBytes("""{"message":"secondary""")], stallAtEnd: true))
        });
        using var transport = Transport(handler, errorBody: TimeSpan.FromMilliseconds(100));

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        Assert.Equal(UpdateAccessReason.Forbidden, Assert.IsType<UpdateAccessException>(error).Reason);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    public async Task Access_statuses_from_the_cdn_are_plain_http_errors(int status)
    {
        var handler = new StubHandler((_, index) => index == 0 ? Redirect(Cdn) : Status(status, """{"message":"Resource not accessible by integration"}"""));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 10, CancellationToken.None));

        Assert.IsNotType<UpdateAccessException>(error);
        Assert.Equal("UPDATES_HTTP", error.Code);
        Assert.Equal(UpdateMessages.HttpFailed(status), error.Message);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(204)]
    [InlineData(206)]
    [InlineData(304)]
    [InlineData(400)]
    public async Task Other_statuses_are_http_errors_without_the_body(int status)
    {
        using var transport = Transport(new StubHandler((_, _) => Status(status, """{"message":"SECRET internal detail"}""")));

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        Assert.Equal("UPDATES_HTTP", error.Code);
        Assert.Equal(UpdateMessages.HttpFailed(status), error.Message);
        AssertNoSecrets(error);
    }

    // ---- Rete, tempi e chiusura ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_connection_failure_is_a_network_error()
    {
        var handler = new StubHandler((_, _, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("No such host is known. (api.github.com:443)")));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        Assert.Equal("UPDATES_NETWORK", error.Code);
        Assert.Equal(UpdateMessages.NetworkUnreachable, error.Message);
    }

    [Fact]
    public async Task A_cancelled_caller_gets_a_timeout_error()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var transport = Transport(new StubHandler((_, _) => Ok("[]")));

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, cancelled.Token));

        Assert.Equal("UPDATES_TIMEOUT", error.Code);
        Assert.Equal(UpdateMessages.RequestTimeout, error.Message);
    }

    [Fact]
    public async Task Cancelling_while_waiting_for_github_is_a_timeout_error()
    {
        using var caller = new CancellationTokenSource();
        var handler = new StubHandler(async (_, _, ct) =>
        {
            caller.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("[]");
        });
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, caller.Token));

        Assert.Equal("UPDATES_TIMEOUT", error.Code);
    }

    [Fact]
    public async Task A_request_that_exceeds_its_time_limit_is_a_timeout()
    {
        var handler = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok("[]");
        });
        using var transport = Transport(handler, request: TimeSpan.FromMilliseconds(150));

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        Assert.Equal("UPDATES_TIMEOUT", error.Code);
    }

    [Fact]
    public async Task Headers_that_never_arrive_within_the_idle_limit_are_a_network_error()
    {
        var handler = new StubHandler(async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return Ok(new byte[] { 1 });
        });
        using var transport = Transport(handler, download: TimeSpan.FromMinutes(5), idle: TimeSpan.FromMilliseconds(150));
        using var dir = new TempDir();

        var error = await Fails(() => transport.DownloadAssetAsync(1, Path.Combine(dir.Path, "a.exe.part"), 1, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_NETWORK", error.Code);
        Assert.False(File.Exists(Path.Combine(dir.Path, "a.exe.part")));
    }

    [Fact]
    public async Task A_stream_error_after_the_headers_is_an_incomplete_transfer()
    {
        var handler = new StubHandler((_, _) => Chunked(new ChunkStream(new[] { new byte[] { 1, 2, 3 } }, failAtEnd: new IOException("connection reset"))));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 100, CancellationToken.None));

        Assert.Equal("UPDATES_DOWNLOAD", error.Code);
        Assert.Equal(UpdateMessages.TransferIncomplete, error.Message);
    }

    [Fact]
    public async Task A_stalled_body_is_an_incomplete_transfer_and_the_overall_limit_a_timeout()
    {
        using (var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream(new[] { new byte[] { 1 } }, stallAtEnd: true))),
                   idle: TimeSpan.FromMilliseconds(150)))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 100, CancellationToken.None));
            Assert.Equal("UPDATES_DOWNLOAD", error.Code);
        }
        using (var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream(new[] { new byte[] { 1 } }, stallAtEnd: true))),
                   request: TimeSpan.FromMilliseconds(150)))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(1, Token, 100, CancellationToken.None));
            Assert.Equal("UPDATES_TIMEOUT", error.Code);
        }
    }

    [Fact]
    public async Task A_disposed_transport_refuses_new_requests_and_disposes_its_handler()
    {
        var handler = new StubHandler((_, _) => Ok("[]"));
        var transport = Transport(handler);
        transport.Dispose();
        transport.Dispose();

        var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));

        Assert.Equal("UPDATES_CLOSED", error.Code);
        Assert.Empty(handler.Sent);
        Assert.True(handler.Disposed);
    }

    [Fact]
    public void The_handler_must_not_follow_redirects_by_itself()
    {
        using var sockets = new SocketsHttpHandler();
        Assert.Throws<ArgumentException>(() => new GitHubReleaseTransport(sockets, disposeHandler: false));
        using var client = new HttpClientHandler();
        Assert.Throws<ArgumentException>(() => new GitHubReleaseTransport(client, disposeHandler: false));
        using var manual = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var accepted = new GitHubReleaseTransport(manual, disposeHandler: false);
        using var production = GitHubReleaseTransport.CreateDefault();
    }

    // ---- JSON e contenuti in memoria ----------------------------------------------------------------------------

    [Fact]
    public async Task Invalid_json_or_utf8_is_a_response_error()
    {
        foreach (var body in new byte[][] { Encoding.UTF8.GetBytes("{not json"), Encoding.UTF8.GetBytes(""), [(byte)'"', 0xff, 0xfe, (byte)'"'] })
        {
            using var transport = Transport(new StubHandler((_, _) => Ok(body)));
            var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));
            Assert.Equal("UPDATES_RESPONSE", error.Code);
            Assert.Equal(UpdateMessages.MetadataInvalid, error.Message);
        }
    }

    [Fact]
    public async Task Json_larger_than_8_mb_is_refused_with_or_without_content_length()
    {
        const int limit = 8 * 1024 * 1024;
        var json = new byte[limit + 1];
        json.AsSpan().Fill((byte)' ');
        json[0] = (byte)'[';
        json[^1] = (byte)']';

        using (var transport = Transport(new StubHandler((_, _) => Ok(json))))
        {
            var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));
            Assert.Equal("UPDATES_SIZE", error.Code);
            Assert.Equal(UpdateMessages.ResponseTooLarge, error.Message);
        }
        using (var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream(Split(json, 65536))))))
        {
            var error = await Fails(() => transport.ReadJsonAsync("/releases", Token, CancellationToken.None));
            Assert.Equal("UPDATES_SIZE", error.Code);
        }
        // Esattamente al limite e' ancora accettato.
        var exact = json[..limit];
        exact[^1] = (byte)']';
        using (var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream(Split(exact, 65536))))))
        {
            using var document = await transport.ReadJsonAsync("/releases", Token, CancellationToken.None);
            Assert.Equal(0, document.RootElement.GetArrayLength());
        }
    }

    [Fact]
    public async Task ReadAssetBytes_asks_for_octet_stream_and_honours_the_limit()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('a', 64) + "  AIUsageMonitor-1.0.0-win-x64.exe\n");
        var handler = new StubHandler((_, _) => Ok(bytes));
        using (var transport = Transport(handler))
            Assert.Equal(bytes, await transport.ReadAssetBytesAsync(77, Token, bytes.Length, CancellationToken.None));
        Assert.Equal(new Uri(Api + "/releases/assets/77"), handler.Sent[0].Url);
        Assert.Equal("application/octet-stream", handler.Sent[0].Header("Accept"));

        using (var transport = Transport(new StubHandler((_, _) => Ok(bytes))))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(77, Token, bytes.Length - 1, CancellationToken.None));
            Assert.Equal("UPDATES_SIZE", error.Code);
        }
        using (var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream(Split(bytes, 7))))))
        {
            var error = await Fails(() => transport.ReadAssetBytesAsync(77, Token, bytes.Length - 1, CancellationToken.None));
            Assert.Equal("UPDATES_SIZE", error.Code);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Invalid_asset_ids_are_refused_before_any_request(long assetId)
    {
        var handler = new StubHandler((_, _) => Ok(new byte[] { 1 }));
        using var transport = Transport(handler);
        using var dir = new TempDir();

        Assert.Equal("UPDATES_URL", (await Fails(() => transport.ReadAssetBytesAsync(assetId, Token, 10, CancellationToken.None))).Code);
        Assert.Equal("UPDATES_URL", (await Fails(() => transport.DownloadAssetAsync(assetId, Path.Combine(dir.Path, "x"), 1, Token, null, CancellationToken.None))).Code);
        Assert.Empty(handler.Sent);
    }

    // ---- Download in streaming ----------------------------------------------------------------------------------

    private sealed class RecordingProgress : IProgress<long>
    {
        public List<long> Values { get; } = new();
        public void Report(long value) => Values.Add(value);
    }

    [Fact]
    public async Task Download_writes_a_new_file_and_returns_its_sha256()
    {
        var payload = Payload(300_000);
        var handler = new StubHandler((_, index) => index == 0 ? Redirect(Cdn) : Chunked(new ChunkStream(Split(payload, 300))));
        using var transport = Transport(handler);
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "AIUsageMonitor-1.2.3-0000.exe.part");
        var progress = new RecordingProgress();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var result = await transport.DownloadAssetAsync(5, path, payload.Length, Token, progress, CancellationToken.None);

        var elapsed = clock.Elapsed;
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(payload)), result.Sha256);
        Assert.Equal(payload.Length, result.Size);
        Assert.Equal(payload, File.ReadAllBytes(path));
        // Primo blocco subito, poi al massimo ogni 200 ms, e sempre il totale alla fine: non 1000 notifiche.
        Assert.Equal(payload.Length, progress.Values[^1]);
        var allowed = 2 + (int)(elapsed.TotalMilliseconds / 200) + 1;
        Assert.True(progress.Values.Count <= allowed, $"{progress.Values.Count} notifiche in {elapsed.TotalMilliseconds} ms");
        Assert.True(progress.Values.Count < 1000);
        Assert.Equal(progress.Values.OrderBy(v => v), progress.Values);
        Assert.Equal(new Uri(Api + "/releases/assets/5"), handler.Sent[0].Url);
        Assert.Equal("application/octet-stream", handler.Sent[0].Header("Accept"));
        Assert.Null(handler.Sent[1].Header("Authorization"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Download_reports_the_first_block_at_once_and_the_total_at_the_end()
    {
        var payload = Payload(4000);
        var chunks = Split(payload, 1000).ToList();
        var handler = new StubHandler((_, _) => Chunked(new ChunkStream(chunks)));
        using var transport = Transport(handler);
        using var dir = new TempDir();
        var progress = new RecordingProgress();

        var result = await transport.DownloadAssetAsync(5, Path.Combine(dir.Path, "a.part"), payload.Length, "", progress, CancellationToken.None);

        Assert.Equal(payload.Length, result.Size);
        Assert.Equal(1000, progress.Values[0]);
        Assert.Equal(payload.Length, progress.Values[^1]);
    }

    /// <summary>Avanzamento lento: simula il tempo speso tra due letture (disco lento, antivirus, UI occupata).</summary>
    private sealed class SlowProgress(TimeSpan delay) : IProgress<long>
    {
        public int Calls { get; private set; }

        public void Report(long value)
        {
            Calls++;
            Thread.Sleep(delay);
        }
    }

    [Fact]
    public async Task Time_spent_between_reads_does_not_count_as_a_stalled_connection()
    {
        var payload = Payload(3000);
        using var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream(Split(payload, 1000)))),
            idle: TimeSpan.FromMilliseconds(100));
        using var dir = new TempDir();
        var progress = new SlowProgress(TimeSpan.FromMilliseconds(250));

        var result = await transport.DownloadAssetAsync(5, Path.Combine(dir.Path, "a.part"), payload.Length, Token, progress, CancellationToken.None);

        Assert.Equal(payload.Length, result.Size);
        Assert.True(progress.Calls >= 3);
    }

    [Fact]
    public async Task A_content_length_different_from_the_release_is_refused_before_writing()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.part");
        using var transport = Transport(new StubHandler((_, _) => Ok(Payload(1001))));

        var error = await Fails(() => transport.DownloadAssetAsync(5, path, 1000, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_SIZE", error.Code);
        Assert.Equal(UpdateMessages.PackageSizeMismatch, error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_body_longer_than_expected_is_stopped()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.part");
        using var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream(Split(Payload(5000), 1000)))));

        var error = await Fails(() => transport.DownloadAssetAsync(5, path, 3500, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_SIZE", error.Code);
        Assert.Equal(UpdateMessages.PackageTooLarge, error.Message);
        // Il parziale resta al chiamante, che lo elimina; non contiene mai piu' dei byte gia' validati.
        Assert.True(new FileInfo(path).Length <= 3500);
    }

    [Fact]
    public async Task A_body_shorter_than_expected_is_incomplete()
    {
        using var dir = new TempDir();
        using var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream(Split(Payload(2000), 1000)))));

        var error = await Fails(() => transport.DownloadAssetAsync(5, Path.Combine(dir.Path, "a.part"), 3000, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_SIZE", error.Code);
        Assert.Equal(UpdateMessages.PackageIncomplete, error.Message);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(UpdateSource.MaxPackageBytes + 1)]
    public async Task An_impossible_expected_size_is_refused_before_any_request(long expectedSize)
    {
        using var dir = new TempDir();
        var handler = new StubHandler((_, _) => Ok(new byte[] { 1 }));
        using var transport = Transport(handler);

        var error = await Fails(() => transport.DownloadAssetAsync(5, Path.Combine(dir.Path, "a.part"), expectedSize, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_SIZE", error.Code);
        Assert.Empty(handler.Sent);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task An_existing_destination_is_never_overwritten()
    {
        using var dir = new TempDir();
        var path = dir.File("a.part", "keep me");
        using var transport = Transport(new StubHandler((_, _) => Ok(Payload(100))));

        var error = await Fails(() => transport.DownloadAssetAsync(5, path, 100, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_WRITE", error.Code);
        Assert.Equal(UpdateMessages.PackageWriteFailed, error.Message);
        Assert.Equal("keep me", File.ReadAllText(path));
    }

    [Fact]
    public async Task A_missing_destination_folder_is_a_write_error()
    {
        using var dir = new TempDir();
        using var transport = Transport(new StubHandler((_, _) => Ok(Payload(100))));

        var error = await Fails(() => transport.DownloadAssetAsync(5, Path.Combine(dir.Path, "missing", "a.part"), 100, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_WRITE", error.Code);
    }

    [Fact]
    public async Task A_download_that_exceeds_its_duration_is_a_timeout()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "a.part");
        using var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream([Payload(100)], stallAtEnd: true))),
            download: TimeSpan.FromMilliseconds(200));

        var error = await Fails(() => transport.DownloadAssetAsync(5, path, 1000, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_TIMEOUT", error.Code);
        Assert.True(File.Exists(path)); // parziale lasciato al chiamante
    }

    [Fact]
    public async Task A_download_cancelled_by_the_caller_is_a_timeout()
    {
        using var dir = new TempDir();
        using var caller = new CancellationTokenSource();
        var progress = new Progress<long>();
        using var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream([Payload(100)], stallAtEnd: true))));
        caller.CancelAfter(TimeSpan.FromMilliseconds(150));

        var error = await Fails(() => transport.DownloadAssetAsync(5, Path.Combine(dir.Path, "a.part"), 1000, Token, progress, caller.Token));

        Assert.Equal("UPDATES_TIMEOUT", error.Code);
        Assert.Equal(UpdateMessages.RequestTimeout, error.Message);
    }

    [Fact]
    public async Task A_network_error_during_the_download_is_an_incomplete_transfer()
    {
        using var dir = new TempDir();
        using var transport = Transport(new StubHandler((_, _) => Chunked(new ChunkStream([Payload(100)], failAtEnd: new HttpRequestException("reset")))));

        var error = await Fails(() => transport.DownloadAssetAsync(5, Path.Combine(dir.Path, "a.part"), 1000, Token, null, CancellationToken.None));

        Assert.Equal("UPDATES_DOWNLOAD", error.Code);
    }

    [Fact]
    public async Task Download_errors_are_classified_like_metadata_errors()
    {
        using var dir = new TempDir();
        using var transport = Transport(new StubHandler((_, _) => Status(404)));

        var error = await Fails(() => transport.DownloadAssetAsync(5, Path.Combine(dir.Path, "a.part"), 100, Token, null, CancellationToken.None));

        Assert.Equal(UpdateAccessReason.NotFound, Assert.IsType<UpdateAccessException>(error).Reason);
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task The_same_transport_serves_concurrent_requests()
    {
        var handler = new StubHandler(async (request, _, ct) =>
        {
            await Task.Delay(20, ct);
            return Ok($$"""{"path":"{{request.RequestUri!.AbsolutePath}}"}""");
        });
        using var transport = Transport(handler);

        var results = await Task.WhenAll(Enumerable.Range(1, 8).Select(async i =>
        {
            using var document = await transport.ReadJsonAsync($"/releases/{i}", Token, CancellationToken.None);
            return document.RootElement.GetProperty("path").GetString();
        }));

        Assert.Equal(Enumerable.Range(1, 8).Select(i => $"/repos/{UpdateSource.Repository}/releases/{i}"), results);
    }

    [Fact]
    public async Task The_shared_fake_handler_works_with_the_transport()
    {
        // FakeHttpMessageHandler (condiviso) non segue redirect: e' un handler valido per il trasporto.
        var fake = FakeHttpMessageHandler.Json(HttpStatusCode.OK, """[{"id":3}]""");
        using var transport = new GitHubReleaseTransport(fake);

        using var document = await transport.ReadJsonAsync("/releases", Token, CancellationToken.None);

        Assert.Equal(3, document.RootElement[0].GetProperty("id").GetInt32());
        var request = Assert.Single(fake.Requests);
        Assert.Equal(new Uri(Api + "/releases"), request.RequestUri);
        Assert.Equal("Bearer " + Token, request.Headers.Authorization!.ToString());
    }
}
