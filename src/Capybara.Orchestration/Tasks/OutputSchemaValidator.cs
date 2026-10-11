using System.Globalization;
using System.Text.Json;

namespace Capybara.Orchestration.Tasks;

/// <summary>
/// Outcome of checking a task result against the task's output schema (FR-A2A-09). <see cref="Errors"/> block
/// completion; <see cref="Unchecked"/> lists what could not be verified and must be reported as unverified.
/// </summary>
public sealed record OutputSchemaCheck(IReadOnlyList<string> Errors, IReadOnlyList<string> Unchecked)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Verifies a task result against the output schema the requester gave (FR-A2A-09). Supports the JSON Schema subset
/// agents actually write: <c>type</c>, <c>properties</c>, <c>required</c>, <c>additionalProperties: false</c>,
/// <c>items</c> and <c>enum</c>. Anything else is not silently passed: it is reported in
/// <see cref="OutputSchemaCheck.Unchecked"/>, as is a schema that is prose rather than JSON.
/// </summary>
public static class OutputSchemaValidator
{
    private const int MaxErrors = 20;

    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "enum",
        "$schema", "$id", "title", "description", "examples", "default",
    };

    public static OutputSchemaCheck Check(string? schema, string result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (string.IsNullOrWhiteSpace(schema))
        {
            return new OutputSchemaCheck([], []);
        }

        JsonElement schemaRoot;
        try
        {
            using var document = JsonDocument.Parse(schema);
            schemaRoot = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return new OutputSchemaCheck([], ["the output schema is not JSON Schema, so the result was not machine-checked against it"]);
        }

        if (schemaRoot.ValueKind != JsonValueKind.Object)
        {
            return new OutputSchemaCheck([], ["the output schema is not a JSON Schema object, so the result was not machine-checked against it"]);
        }

        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(result);
            value = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return new OutputSchemaCheck([$"the result must be JSON matching the output schema: {ex.Message}"], []);
        }

        var errors = new List<string>();
        var @unchecked = new SortedSet<string>(StringComparer.Ordinal);
        Validate(schemaRoot, value, "$", errors, @unchecked);
        return new OutputSchemaCheck([.. errors.Take(MaxErrors)], [.. @unchecked]);
    }

    private static void Validate(JsonElement schema, JsonElement value, string path, List<string> errors, SortedSet<string> @unchecked)
    {
        if (schema.ValueKind == JsonValueKind.True)
        {
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
        {
            @unchecked.Add($"{path}: schema form '{schema.ValueKind}' not checked");
            return;
        }

        foreach (var keyword in schema.EnumerateObject().Select(p => p.Name).Where(k => !Supported.Contains(k)))
        {
            @unchecked.Add($"{path}: schema keyword '{keyword}' not checked");
        }

        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
        {
            errors.Add($"{path}: expected {Describe(type)}, got {Kind(value)}");
            return;
        }

        if (schema.TryGetProperty("enum", out var options) && options.ValueKind == JsonValueKind.Array
            && !options.EnumerateArray().Any(o => JsonElement.DeepEquals(o, value)))
        {
            errors.Add($"{path}: value is not one of the allowed values");
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(schema, value, path, errors, @unchecked);
        }
        else if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
            {
                Validate(items, item, string.Create(CultureInfo.InvariantCulture, $"{path}[{index++}]"), errors, @unchecked);
            }
        }
    }

    private static void ValidateObject(JsonElement schema, JsonElement value, string path, List<string> errors, SortedSet<string> @unchecked)
    {
        var properties = schema.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in required.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => n.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    errors.Add($"{path}: missing required property '{name}'");
                }
            }
        }

        var closed = schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False;
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Validate(propertySchema, property.Value, $"{path}.{property.Name}", errors, @unchecked);
            }
            else if (closed)
            {
                errors.Add($"{path}: unexpected property '{property.Name}'");
            }
        }
    }

    private static bool MatchesType(JsonElement type, JsonElement value) => type.ValueKind switch
    {
        JsonValueKind.String => Is(type.GetString(), value),
        JsonValueKind.Array => type.EnumerateArray().Any(t => t.ValueKind == JsonValueKind.String && Is(t.GetString(), value)),
        _ => true,
    };

    private static bool Is(string? type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "number" => value.ValueKind == JsonValueKind.Number,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var d) && decimal.Truncate(d) == d,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static string Describe(JsonElement type) => type.ValueKind == JsonValueKind.Array
        ? string.Join(" or ", type.EnumerateArray().Select(t => t.ToString()))
        : type.ToString();

    private static string Kind(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => value.ValueKind.ToString().ToLowerInvariant(),
    };
}
