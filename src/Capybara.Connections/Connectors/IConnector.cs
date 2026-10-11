using System.Text.Json;
using Capybara.Connections.Model;

namespace Capybara.Connections.Connectors;

/// <summary>
/// Broker-side connector for one connection (FR-CON-01). Connectors build provider requests only from the named,
/// schema-declared arguments, so grant constraints checked on those arguments cannot be bypassed by extra fields
/// (FR-CON-02).
/// </summary>
public interface IConnector
{
    ConnectionDefinition Connection { get; }

    IReadOnlyList<ConnectorOperation> Operations { get; }

    /// <summary>Runs one operation. Invalid arguments surface as <see cref="ArgumentException"/>.</summary>
    ValueTask<BrokerResult> InvokeAsync(string operation, JsonElement args, string accessToken, CancellationToken cancellationToken = default);
}

/// <summary>
/// One connector operation as advertised to agents (FR-CON-03). <see cref="Untrusted"/> marks operations whose
/// output is third-party content (FR-SAF-03).
/// </summary>
public sealed record ConnectorOperation(string Name, string Description, JsonElement InputSchema, bool Untrusted)
{
    public static ConnectorOperation Read(string name, string description, params ConnectorArgument[] arguments) =>
        new(name, description, ConnectorArgument.Schema(arguments), Untrusted: true);

    public static ConnectorOperation Write(string name, string description, params ConnectorArgument[] arguments) =>
        new(name, description, ConnectorArgument.Schema(arguments), Untrusted: false);
}

/// <summary>A named connector argument; the JSON schema is generated from these declarations.</summary>
public sealed record ConnectorArgument(string Name, ConnectorArgumentType Type, string Description, bool Required = true)
{
    public static ConnectorArgument Text(string name, string description, bool required = true) => new(name, ConnectorArgumentType.Text, description, required);

    public static ConnectorArgument Number(string name, string description, bool required = true) => new(name, ConnectorArgumentType.Number, description, required);

    public static ConnectorArgument List(string name, string description, bool required = true) => new(name, ConnectorArgumentType.TextList, description, required);

    internal static JsonElement Schema(IReadOnlyList<ConnectorArgument> arguments)
    {
        var properties = arguments.ToDictionary(
            a => a.Name,
            a => a.Type switch
            {
                ConnectorArgumentType.Number => (object)new { type = "integer", description = a.Description },
                ConnectorArgumentType.TextList => new { type = "array", items = new { type = "string" }, description = a.Description },
                _ => new { type = "string", description = a.Description },
            },
            StringComparer.Ordinal);
        var schema = new
        {
            type = "object",
            properties,
            required = arguments.Where(a => a.Required).Select(a => a.Name).ToArray(),
            additionalProperties = false,
        };
        return JsonSerializer.SerializeToElement(schema);
    }
}

public enum ConnectorArgumentType
{
    Text,
    Number,
    TextList,
}
