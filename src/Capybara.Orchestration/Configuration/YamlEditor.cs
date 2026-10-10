using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Structural edits to user-owned YAML (agent.yaml, routines.yaml) for human-initiated lifecycle operations and approved
/// proposals. Edits go through the YAML node model rather than text substitution.
/// </summary>
internal static class YamlEditor
{
    public static YamlNode? Parse(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode;
    }

    public static string Serialize(YamlNode root)
    {
        var builder = new StringBuilder();
        using (var writer = new StringWriter(builder))
        {
            new YamlStream(new YamlDocument(root)).Save(writer, assignAnchors: false);
        }

        var text = builder.ToString();
        return text.EndsWith("...\n", StringComparison.Ordinal) ? text[..^4] : text;
    }

    public static string? GetScalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;

    public static void SetScalar(YamlMappingNode map, string key, string value) => map.Children[new YamlScalarNode(key)] = new YamlScalarNode(value);

    public static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    /// <summary>Structural equality of two optional nodes (by canonical serialization).</summary>
    public static bool NodesEqual(YamlNode? a, YamlNode? b) =>
        (a is null && b is null) || (a is not null && b is not null && Serialize(a) == Serialize(b));

    /// <summary>Parses a routines.yaml sequence; an empty file is an empty sequence.</summary>
    public static YamlSequenceNode ParseSequence(string text) =>
        string.IsNullOrWhiteSpace(text) ? new YamlSequenceNode() : Parse(text) as YamlSequenceNode ?? throw new YamlException("expected a YAML sequence");

    public static YamlMappingNode ParseMapping(string text) =>
        Parse(text) as YamlMappingNode ?? throw new YamlException("expected a YAML mapping");
}
