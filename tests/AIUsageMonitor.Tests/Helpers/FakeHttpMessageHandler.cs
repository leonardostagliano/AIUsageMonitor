using System.Net;

namespace AIUsageMonitor.Tests.Helpers;

public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

    public static FakeHttpMessageHandler Json(HttpStatusCode status, string body) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });

    public static FakeHttpMessageHandler Throws(Exception ex) => new(_ => throw ex);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // PricingService downloads the price list and the ECB rate at the same time on pool threads: two unguarded
        // Adds can lose one, and a test counting the requests then waits for one that never shows up.
        lock (Requests) Requests.Add(request);
        return Task.FromResult(_responder(request));
    }
}
