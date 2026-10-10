using System.Globalization;
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
        var dto = Deserializer.Deserialize<Dto?>(yaml) ?? new Dto();
        return (dto.ToMetadata(), body);
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
