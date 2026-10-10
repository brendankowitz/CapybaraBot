using Capybara.Connections.Broker;
using Capybara.Connections.Connectors;
using Capybara.Connections.Grants;
using Capybara.Connections.Mcp;
using Capybara.Connections.OAuth;
using Capybara.Contracts.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Capybara.Connections.DependencyInjection;

/// <summary>
/// Fails broker startup closed (FR-CON-02, FR-CON-04, FR-SKL-04): loads grants from config, checks every grant
/// against the configured connectors, opens the encrypted vault and verifies MCP server pins. All errors are
/// reported together.
/// </summary>
internal sealed class BrokerStartupValidator(IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        try
        {
            var grants = services.GetRequiredService<ConfigGrantProvider>();
            errors.AddRange(services.GetRequiredService<BrokerToolCatalog>().Validate(grants.All));
        }
        catch (GrantConfigurationException ex)
        {
            errors.AddRange(ex.Errors);
        }

        try
        {
            _ = services.GetRequiredService<ConnectorRegistry>();
            _ = services.GetRequiredService<ISecretStore>();
            _ = services.GetRequiredService<CredentialManager>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            errors.Add(ex.Message);
        }

        errors.AddRange(await services.GetRequiredService<McpServerCatalog>().VerifyAllAsync(cancellationToken));
        if (errors.Count > 0)
        {
            throw new GrantConfigurationException(errors);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
