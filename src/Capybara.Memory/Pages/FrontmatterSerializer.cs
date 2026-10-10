using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Capybara.Memory.Pages;

/// <summary>Reads and writes Obsidian-compatible YAML frontmatter.</summary>
internal static class FrontmatterSerializer
{
    private const string Fence = "---";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    /// <summary>The deepest YAML nesting accepted in frontmatter. Real frontmatter is a flat map of scalars.</summary>
    internal const int MaxDepth = 32;

    /// <summary>
    /// Reads the frontmatter and body of a page.
    /// </summary>
    /// <exception cref="InvalidFrontmatterException">The frontmatter is malformed or nested deeper than <see cref="MaxDepth"/>.
    /// Callers reading files from disk should use <see cref="TryParse"/> and treat a failure as a quarantined page.</exception>
    public static (PageMetadata Metadata, string Body) Parse(string content)
    {
        var text = content.ReplaceLineEndings("\n");
        if (!text.StartsWith(Fence + "\n", StringComparison.Ordinal))
        {
            return (new PageMetadata(), text);
        }

        var end = text.IndexOf("\n" + Fence, Fence.Length, StringComparison.Ordinal);
        if (end < 0)
        {
            return (new PageMetadata(), text);
        }

        var yaml = text[(Fence.Length + 1)..end];
        var bodyStart = text.IndexOf('\n', end + 1);
        var body = bodyStart < 0 ? string.Empty : text[(bodyStart + 1)..];
        EnsureShallow(yaml);
        try
        {
            var dto = Deserializer.Deserialize<Dto?>(yaml) ?? new Dto();
            return (dto.ToMetadata(), body);
        }
        catch (YamlException ex)
        {
            throw new InvalidFrontmatterException($"Frontmatter is not valid YAML: {ex.Message}", ex);
        }
    }

    /// <summary>Like <see cref="Parse"/>, but returns false instead of throwing for frontmatter that must not be trusted.</summary>
    public static bool TryParse(string content, out (PageMetadata Metadata, string Body) page)
    {
        try
        {
            page = Parse(content);
            return true;
        }
        catch (InvalidFrontmatterException)
        {
            page = default;
            return false;
        }
    }

    /// <summary>The body of a page, or the whole text when the frontmatter cannot be trusted (so comparisons stay exact).</summary>
    public static string BodyOrRaw(string content) => TryParse(content, out var page) ? page.Body : content;

    /// <summary>
    /// The deserializer recurses once per nesting level, so a hostile page of ten thousand <c>[</c> would overflow the stack
    /// and take the process down — an uncatchable failure. The parser itself is a non-recursive state machine, so the
    /// depth is measured on its event stream first and the page rejected before any recursion happens.
    /// </summary>
    private static void EnsureShallow(string yaml)
    {
        var depth = 0;
        try
        {
            var parser = new Parser(new StringReader(yaml));
            while (parser.MoveNext())
            {
                switch (parser.Current)
                {
                    case SequenceStart or MappingStart:
                        if (++depth > MaxDepth)
                        {
                            throw new InvalidFrontmatterException($"Frontmatter is nested deeper than {MaxDepth} levels.");
                        }

                        break;
                    case SequenceEnd or MappingEnd:
                        depth--;
                        break;
                }
            }
        }
        catch (YamlException ex)
        {
            throw new InvalidFrontmatterException($"Frontmatter is not valid YAML: {ex.Message}", ex);
        }
    }

    public static string Write(PageMetadata metadata, string body) =>
        $"{Fence}\n{Serializer.Serialize(Dto.From(metadata))}{Fence}\n{body}";

    private sealed class Dto
    {
        public string? Description { get; set; }

        public string? Source { get; set; }

        public string? Created { get; set; }

        public string? Updated { get; set; }

        public string? LastValidated { get; set; }

        public string? LastAccessed { get; set; }

        public string? Taint { get; set; }

        public string? ValidFrom { get; set; }

        public string? ValidTo { get; set; }

        public static Dto From(PageMetadata m) => new()
        {
            Description = m.Description,
            Source = m.Source,
            Created = Format(m.Created),
            Updated = Format(m.Updated),
            LastValidated = Format(m.LastValidated),
            LastAccessed = Format(m.LastAccessed),
            Taint = m.Taint.ToString().ToLowerInvariant(),
            ValidFrom = Format(m.ValidFrom),
            ValidTo = Format(m.ValidTo),
        };

        public PageMetadata ToMetadata() => new()
        {
            Description = Description ?? string.Empty,
            Source = Source ?? "unknown",
            Created = ParseDate(Created) ?? DateTimeOffset.MinValue,
            Updated = ParseDate(Updated) ?? ParseDate(Created) ?? DateTimeOffset.MinValue,
            LastValidated = ParseDate(LastValidated),
            LastAccessed = ParseDate(LastAccessed),

            // Unknown or missing taint is treated as tainted: fail safe.
            Taint = string.Equals(Taint, "clean", StringComparison.OrdinalIgnoreCase)
                ? Contracts.Messaging.Taint.Clean
                : Contracts.Messaging.Taint.Tainted,
            ValidFrom = ParseDate(ValidFrom),
            ValidTo = ParseDate(ValidTo),
        };

        private static string? Format(DateTimeOffset? value) =>
            value is { } v && v != DateTimeOffset.MinValue ? v.ToString("O", CultureInfo.InvariantCulture) : null;

        private static DateTimeOffset? ParseDate(string? value) =>
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }
}
