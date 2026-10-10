using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capybara.Connections.Broker;
using Capybara.Connections.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Capybara.Connections.Mcp;

/// <summary>
/// The broker's per-agent loopback MCP endpoint, <c>/mcp/{agentId}</c> (FR-CON-03), on the MCP C# SDK's stateless
/// streamable HTTP transport. <c>tools/list</c> returns only the agent's granted <c>{connection}_{operation}</c> tools;
/// <c>tools/call</c> runs through <see cref="ConnectionBroker"/>, so freeze, grants, redaction and audit all apply.
/// The caller must present the agent's bearer from <see cref="PerAgentBearerTokenIssuer"/>; the agent id comes from
/// the authenticated principal, never from the request body.
/// </summary>
public static class BrokerMcpServer
{
    public const string AuthenticationScheme = "CapybaraAgentBearer";
    public const string AuthorizationPolicy = "CapybaraAgent";
    public const string AgentClaim = "capybara:agent";

    /// <summary>Metadata key on tool results carrying the result's taint (FR-SAF-03).</summary>
    public const string TaintMetaKey = "capybara/taint";

    public static IServiceCollection AddCapybaraBrokerMcp(this IServiceCollection services)
    {
        services.TryAddSingleton<PerAgentBearerTokenIssuer>();
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, AgentBearerHandler>(AuthenticationScheme, null);
        services.AddAuthorizationBuilder().AddPolicy(AuthorizationPolicy, p => p.AddAuthenticationSchemes(AuthenticationScheme).RequireClaim(AgentClaim));
        services.AddMcpServer(o => o.ServerInfo = new Implementation { Name = "capybara-broker", Version = "1.0" })
            .WithHttpTransport(o => o.Stateless = true)
            .WithListToolsHandler(ListToolsAsync)
            .WithCallToolHandler(CallToolAsync);
        return services;
    }

    public static IEndpointConventionBuilder MapCapybaraMcpEndpoints(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp("/mcp/{agentId}").RequireAuthorization(AuthorizationPolicy);


    private static ValueTask<ListToolsResult> ListToolsAsync(RequestContext<ListToolsRequestParams> request, CancellationToken cancellationToken)
    {
        var agent = AgentOf(request.User);
        var catalog = request.Services!.GetRequiredService<BrokerToolCatalog>();
        var tools = catalog.ForAgent(agent).Select(t => new Tool { Name = t.Name, Description = t.Description, InputSchema = t.InputSchema }).ToList();
        return ValueTask.FromResult(new ListToolsResult { Tools = tools });
    }

    private static async ValueTask<CallToolResult> CallToolAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken)
    {
        var agent = AgentOf(request.User);
        var name = request.Params?.Name ?? string.Empty;
        var tool = request.Services!.GetRequiredService<BrokerToolCatalog>().Find(agent, name);
        if (tool is null)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = $"denied: '{name}' is not granted" }] };
        }

        var args = JsonSerializer.Serialize(request.Params?.Arguments ?? new Dictionary<string, JsonElement>());
        var broker = request.Services!.GetRequiredService<ConnectionBroker>();
        var result = await broker.InvokeAsync(new BrokerCall(agent, tool.Connection, tool.Operation, args), cancellationToken);
        return new CallToolResult
        {
            IsError = !result.Allowed,
            Content = [new TextContentBlock { Text = result.Text }],
            Meta = new JsonObject { [TaintMetaKey] = result.Taint.ToString() },
        };
    }

    private static string AgentOf(ClaimsPrincipal? user) =>
        user?.FindFirst(AgentClaim)?.Value ?? throw new UnauthorizedAccessException("MCP request is not bound to an agent.");

    /// <summary>Authenticates <c>Authorization: Bearer</c> against the route's agent id.</summary>
    private sealed class AgentBearerHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, PerAgentBearerTokenIssuer tokens)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var agent = Context.GetRouteValue("agentId") as string;
            var header = Request.Headers.Authorization.ToString();
            var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header[7..] : null;
            if (string.IsNullOrEmpty(agent) || token is null)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            if (!tokens.Validate(agent, token))
            {
                return Task.FromResult(AuthenticateResult.Fail("invalid agent bearer"));
            }

            var identity = new ClaimsIdentity([new Claim(AgentClaim, agent)], AuthenticationScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AuthenticationScheme)));
        }
    }
}
