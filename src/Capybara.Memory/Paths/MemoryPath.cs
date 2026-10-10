using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Capybara.Memory.Paths;

/// <summary>
/// A validated wiki path such as <c>/agents/ea/procedures/triage.md</c>. Rejects traversal, absolute host paths,
/// hidden segments and anything outside the three scopes (FR-MEM-06).
/// </summary>
public sealed partial record MemoryPath
{
    private const int MaxSegments = 8;

    private MemoryPath(string value, MemoryScopeKind scope, string? owner)
    {
        Value = value;
        Scope = scope;
        Owner = owner;
    }

    /// <summary>Normalized path, always starting with <c>/</c> and ending with <c>.md</c>.</summary>
    public string Value { get; }

    public MemoryScopeKind Scope { get; }

    /// <summary>Agent id for <see cref="MemoryScopeKind.Agent"/>, project id for <see cref="MemoryScopeKind.Project"/>.</summary>
    public string? Owner { get; }

    /// <summary>Path relative to the memory root, using the OS separator.</summary>
    public string RelativeFileSystemPath => Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

    public static bool TryParse(string? raw, [NotNullWhen(true)] out MemoryPath? path, [NotNullWhen(false)] out string? error)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "path is empty";
            return false;
        }

        var normalized = raw.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        if (!normalized.EndsWith(".md", StringComparison.Ordinal))
        {
            normalized += ".md";
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length is < 2 or > MaxSegments)
        {
            error = $"path must have between 2 and {MaxSegments} segments";
            return false;
        }

        foreach (var segment in segments)
        {
            if (!SegmentPattern().IsMatch(segment))
            {
                error = $"invalid path segment '{segment}'";
                return false;
            }
        }

        (MemoryScopeKind Scope, string? Owner)? scope = segments[0] switch
        {
            "user" => (MemoryScopeKind.User, null),
            "agents" when segments.Length >= 3 => (MemoryScopeKind.Agent, segments[1]),
            "projects" when segments.Length >= 3 => (MemoryScopeKind.Project, segments[1]),
            _ => null,
        };

        if (scope is null)
        {
            error = "path must be under /user/, /agents/{id}/ or /projects/{id}/";
            return false;
        }

        path = new MemoryPath("/" + string.Join('/', segments), scope.Value.Scope, scope.Value.Owner);
        error = null;
        return true;
    }

    public static MemoryPath Parse(string raw) =>
        TryParse(raw, out var path, out var error) ? path : throw new ArgumentException(error, nameof(raw));

    public override string ToString() => Value;

    // Lower-case letters, digits, '-', '_' and '.', not starting with '.', so no '..' or hidden files.
    [GeneratedRegex(@"^[a-z0-9_][a-z0-9_\-.]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SegmentPattern();
}
