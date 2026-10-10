using System.Text;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
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

    /// <summary>
    /// Serializes <paramref name="root"/>, keeping the leading comment block of <paramref name="original"/>. The node
    /// model drops comments, and the header is where the user documents a file.
    /// </summary>
    public static string SerializeWithHeader(string original, YamlNode root)
    {
        var header = new StringBuilder();
        foreach (var line in original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (!line.TrimStart().StartsWith('#'))
            {
                break;
            }

            header.Append(line).Append('\n');
        }

        return header + Serialize(root);
    }

    public static string? GetScalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar ? scalar.Value : null;

    public static void SetScalar(YamlMappingNode map, string key, string value) => map.Children[new YamlScalarNode(key)] = new YamlScalarNode(value);

    public static YamlNode? Get(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;

    /// <summary>Structural equality of two optional nodes (by canonical serialization).</summary>
    public static bool NodesEqual(YamlNode? a, YamlNode? b) =>
        (a is null && b is null) || (a is not null && b is not null && Serialize(a) == Serialize(b));

    /// <summary>
    /// Agent-authored YAML must be plain so that what the reviewer reads is what the loader sees: one document, no
    /// directives, no tags, anchors or aliases, and every mapping key a plain scalar. Tagged or quoted keys
    /// (<c>!!str grants:</c>) compare unequal to plain ones in the node model but bind to the same property in the
    /// deserializer. Returns the first problem, or null.
    /// </summary>
    public static string? FindUnsafeSyntax(string text)
    {
        var parser = new Parser(new StringReader(text));
        var documents = 0;

        // One entry per open collection: null for a sequence, otherwise whether a mapping expects a key next.
        var open = new Stack<bool?>();
        try
        {
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case DocumentStart start:
                        if (++documents > 1)
                        {
                            return "only one YAML document is allowed";
                        }

                        if (start.Version is not null)
                        {
                            return "YAML directives are not allowed";
                        }

                        break;
                    case AnchorAlias:
                        return "YAML aliases are not allowed";
                    case NodeEvent node:
                        if (!node.Anchor.IsEmpty)
                        {
                            return "YAML anchors are not allowed";
                        }

                        if (!node.Tag.IsEmpty)
                        {
                            return $"YAML tags are not allowed ('{node.Tag}')";
                        }

                        if (open.TryPeek(out var expectsKey) && expectsKey is { } isKey)
                        {
                            if (isKey && node is not Scalar { Style: ScalarStyle.Plain })
                            {
                                return "mapping keys must be plain, unquoted scalars";
                            }

                            open.Pop();
                            open.Push(!isKey);
                        }

                        if (node is MappingStart)
                        {
                            open.Push(true);
                        }
                        else if (node is SequenceStart)
                        {
                            open.Push(null);
                        }

                        break;
                    case MappingEnd or SequenceEnd:
                        open.Pop();
                        break;
                }
            }
        }
        catch (YamlException ex)
        {
            return ex.Message;
        }

        return null;
    }

    /// <summary>Parses a routines.yaml sequence; an empty file is an empty sequence.</summary>
    public static YamlSequenceNode ParseSequence(string text) =>
        string.IsNullOrWhiteSpace(text) ? new YamlSequenceNode() : Parse(text) as YamlSequenceNode ?? throw new YamlException("expected a YAML sequence");

    public static YamlMappingNode ParseMapping(string text) =>
        Parse(text) as YamlMappingNode ?? throw new YamlException("expected a YAML mapping");
}
