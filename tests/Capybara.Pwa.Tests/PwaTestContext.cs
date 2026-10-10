using System.Net;
using System.Text;
using Bunit;
using Capybara.Client;
using Capybara.Pwa.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Capybara.Pwa.Tests;

/// <summary>A bUnit context wired to a scripted gateway; the SignalR connection is replaced by a no-op.</summary>
public abstract class PwaTestContext : BunitContext
{
    protected PwaTestContext()
    {
        Gateway = new FakeGateway();
        Services.AddSingleton(new CapybaraApiClient(new HttpClient(Gateway) { BaseAddress = new Uri("https://capybara.test") }));
        Services.AddSingleton<CapybaraRealtimeService>(sp => new OfflineRealtime(sp.GetRequiredService<NavigationManager>(), new TokenStore(sp.GetRequiredService<IJSRuntime>())));
    }

    protected FakeGateway Gateway { get; }

    private sealed class OfflineRealtime(NavigationManager navigation, TokenStore tokens) : CapybaraRealtimeService(navigation, tokens)
    {
        public override Task<bool> TryStartAsync() => Task.FromResult(false);
    }

    protected sealed class FakeGateway : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);

        public List<(string Method, string Path, string? Body)> Requests { get; } = [];

        public void Respond(string method, string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK) => _routes[$"{method} {pathAndQuery}"] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method.Method, path, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            var (status, body) = _routes.TryGetValue($"{request.Method} {path}", out var route) ? route : (HttpStatusCode.NotFound, "no such route");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }
}
