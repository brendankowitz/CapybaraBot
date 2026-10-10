using System.Net;
using System.Text;

namespace Capybara.Connections.Tests;

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(_handler(request));
    }

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
