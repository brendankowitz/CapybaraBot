using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Capybara.Client;
using Capybara.Pwa.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Capybara.Pwa.Tests;

/// <summary>A bUnit context wired to a scripted gateway; the SignalR connection is replaced by a no-op and JS interop is loose.</summary>
public abstract class PwaTestContext : BunitContext
{
    protected static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    protected PwaTestContext()
    {
        // The default 1 s wait can flake when the whole solution's tests run in parallel on a busy machine.
        DefaultWaitTimeout = TimeSpan.FromSeconds(10);
        JSInterop.Mode = JSRuntimeMode.Loose;
        Gateway = new FakeGateway();
        Services.AddSingleton(new CapybaraApiClient(new HttpClient(Gateway) { BaseAddress = new Uri("https://capybara.test") }));
        Services.AddSingleton<CapybaraRealtimeService>(sp => new OfflineRealtime(sp.GetRequiredService<NavigationManager>(), new TokenStore(sp.GetRequiredService<IJSRuntime>())));
    }

    protected FakeGateway Gateway { get; }

    /// <summary>Whether the device offers desktop-only actions (<c>capybaraDevice.isDesktop</c>).</summary>
    protected void SetDesktop(bool desktop) => JSInterop.Setup<bool>("capybaraDevice.isDesktop").SetResult(desktop);

    protected void ServeJson<T>(string pathAndQuery, T value) => Gateway.Respond("GET", pathAndQuery, JsonSerializer.Serialize(value, Web));

    private sealed class OfflineRealtime(NavigationManager navigation, TokenStore tokens) : CapybaraRealtimeService(navigation, tokens)
    {
        public override Task<bool> TryStartAsync() => Task.FromResult(false);
    }

    protected sealed class FakeGateway : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body)> _routes = new(StringComparer.Ordinal);

        public List<(string Method, string Path, string? Body)> Requests { get; } = [];

        private readonly Dictionary<string, Queue<string>> _sequences = new(StringComparer.Ordinal);

        public void Respond(string method, string pathAndQuery, string body, HttpStatusCode status = HttpStatusCode.OK) => _routes[$"{method} {pathAndQuery}"] = (status, body);

        /// <summary>Successive 200 responses for one route; the last one repeats.</summary>
        public void RespondSequence(string method, string pathAndQuery, params string[] bodies) => _sequences[$"{method} {pathAndQuery}"] = new Queue<string>(bodies);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method.Method, path, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            if (_sequences.TryGetValue($"{request.Method} {path}", out var queue))
            {
                var next = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(next, Encoding.UTF8, "application/json"), RequestMessage = request };
            }

            var (status, body) = _routes.TryGetValue($"{request.Method} {path}", out var route) ? route : (HttpStatusCode.NotFound, "no such route");
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json"), RequestMessage = request };
        }
    }
}
