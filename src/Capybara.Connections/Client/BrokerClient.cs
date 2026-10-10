using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using Capybara.Connections.Model;
using Microsoft.Extensions.AI;

namespace Capybara.Connections.Client;

/// <summary>HTTP client used by capybarad to call the separate capybara-broker process (FR-CON-01).</summary>
public sealed class BrokerClient(HttpClient http)
{
    public async ValueTask<BrokerResult> InvokeAsync(BrokerCall call, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("/broker/invoke", call, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BrokerResult>(cancellationToken) ?? new BrokerResult(false, "empty broker response", Contracts.Messaging.Taint.Clean, 0, "empty response");
    }

    public async ValueTask<IReadOnlyList<ConnectionStatus>> ListConnectionsAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<IReadOnlyList<ConnectionStatus>>("/broker/connections", cancellationToken) ?? [];
}

/// <summary>Produces custom AI functions named connection_operation for granted broker tools (FR-CON-03).</summary>
public interface IBrokerToolSource
{
    IReadOnlyList<AIFunctionDeclaration> GetTools(string agentId, IReadOnlyDictionary<string, IReadOnlyList<string>> operationsByConnection);
}

public sealed class BrokerToolSource(BrokerClient client) : IBrokerToolSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public IReadOnlyList<AIFunctionDeclaration> GetTools(string agentId, IReadOnlyDictionary<string, IReadOnlyList<string>> operationsByConnection)
    {
        var tools = new List<AIFunctionDeclaration>();
        foreach (var (connection, operations) in operationsByConnection)
        {
            foreach (var operation in operations)
            {
                var name = $"{connection}_{operation}";
                var function = AIFunctionFactory.Create(
                    async ([Description("JSON object arguments for the broker connector")] string argsJson, CancellationToken ct) =>
                    {
                        var result = await client.InvokeAsync(new BrokerCall(agentId, connection, operation, argsJson), ct);
                        return JsonSerializer.Serialize(result, Json);
                    },
                    name,
                    $"Invoke broker connection {connection} operation {operation}. Returns redacted data.");
                tools.Add(function);
            }
        }

        return tools;
    }
}
