using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleet.Contracts.Agents;

/// <summary>Serializes <see cref="AgentId"/> as a plain, validated string.</summary>
public sealed class AgentIdJsonConverter : JsonConverter<AgentId>
{
    public override AgentId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        AgentId.TryParse(reader.GetString(), out var id) ? id : throw new JsonException("Invalid agent id.");

    public override void Write(Utf8JsonWriter writer, AgentId value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Value);
    }
}
