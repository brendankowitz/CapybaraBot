using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Fleet.Contracts.Agents;
using Fleet.Memory.Access;
using Fleet.Memory.Pages;
using Fleet.Memory.Paths;
using Fleet.Memory.Store;
using Fleet.Memory.Versioning;

namespace Fleet.Memory.Consolidation;

/// <summary>Nightly low-privilege memory steward: promote logs, dedupe, archive stale pages and stage a digest (FR-MEM-08).</summary>
public sealed partial class ConsolidationJob(WikiStore store, MemoryOptions options, IMemoryVersioning versioning, TimeProvider time, IConsolidationSummarizer? summarizer = null)
{
    private static readonly MemoryAccessContext StewardContext = new()
    {
        Agent = AgentId.Parse("steward"),
        SessionId = "steward:consolidation",
        CanReadUser = true,
    };

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var digest = new StringBuilder();
        digest.AppendLine($"Consolidation run {time.GetUtcNow():yyyy-MM-dd}");
        digest.AppendLine();
        digest.AppendLine("Facts promoted:");
        var promoted = await PromoteDailyLogsAsync(digest, cancellationToken);
        var deduped = await DedupeAsync(cancellationToken);
        var archived = await ArchiveStaleAsync(cancellationToken);
        digest.AppendLine($"- promoted: {promoted}");
        digest.AppendLine($"- deduped: {deduped}");
        digest.AppendLine($"- archived: {archived}");
        if (summarizer is not null)
        {
            digest.AppendLine(await summarizer.SummarizeAsync(digest.ToString(), cancellationToken));
        }

        var digestPath = $"/user/digests/consolidation-{time.GetUtcNow():yyyy-MM-dd}.md";
        await store.RememberAsync(StewardContext, digestPath, digest.ToString(), "Consolidation review digest", cancellationToken);
        await versioning.CommitAsync("memory consolidation", cancellationToken);
    }

    private async Task<int> PromoteDailyLogsAsync(StringBuilder digest, CancellationToken cancellationToken)
    {
        var count = 0;
        var root = store.Root;
        foreach (var daily in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories)
            .Where(f => f.Replace(Path.DirectorySeparatorChar, '/').Contains("/daily/", StringComparison.Ordinal)))
        {
            var relative = "/" + Path.GetRelativePath(root, daily).Replace(Path.DirectorySeparatorChar, '/');
            if (!DailyPathPattern().IsMatch(relative) || !MemoryPath.TryParse(relative, out var dailyPath, out _))
            {
                continue;
            }

            var notesPath = MemoryPath.Parse($"/agents/{dailyPath.Owner}/notes.md");
            var bullets = ExtractBullets(await File.ReadAllTextAsync(daily, cancellationToken));
            if (bullets.Count == 0)
            {
                continue;
            }

            var notesFile = Path.Combine(root, notesPath.RelativeFileSystemPath);
            var notes = File.Exists(notesFile) ? await File.ReadAllTextAsync(notesFile, cancellationToken) : string.Empty;
            var (_, body) = FrontmatterSerializer.Parse(notes);
            var existing = new HashSet<string>(ExtractBullets(body), StringComparer.OrdinalIgnoreCase);
            var additions = bullets.Where(b => existing.Add(b)).Select(b => $"- {b} _(from {relative})_").ToList();
            if (additions.Count == 0)
            {
                continue;
            }

            var now = time.GetUtcNow();
            var metadata = new PageMetadata { Description = "Durable agent notes", Source = "steward:consolidation", Created = now, Updated = now, ValidFrom = now };
            if (File.Exists(notesFile))
            {
                metadata = FrontmatterSerializer.Parse(notes).Metadata with { Updated = now, Source = "steward:consolidation" };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(notesFile)!);
            await File.WriteAllTextAsync(notesFile, FrontmatterSerializer.Write(metadata, body.TrimEnd() + "\n" + string.Join('\n', additions) + "\n"), cancellationToken);
            count += additions.Count;
            foreach (var addition in additions)
            {
                digest.AppendLine(addition);
            }
        }

        return count;
    }

    private async Task<int> DedupeAsync(CancellationToken cancellationToken)
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        var removed = 0;
        foreach (var file in MemoryFiles().Order(StringComparer.Ordinal))
        {
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var (_, body) = FrontmatterSerializer.Parse(content);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(body))));
            if (!seen.TryAdd(hash, file))
            {
                File.Delete(file);
                removed++;
            }
        }

        return removed;
    }

    private async Task<int> ArchiveStaleAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-options.ArchiveAfterDays);
        var archived = 0;
        foreach (var file in MemoryFiles().ToList())
        {
            var relative = "/" + Path.GetRelativePath(store.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("/archive/", StringComparison.Ordinal) || !MemoryPath.TryParse(relative, out _, out _))
            {
                continue;
            }

            var (metadata, _) = FrontmatterSerializer.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            var lastUsed = metadata.LastAccessed ?? metadata.Updated;
            if (lastUsed == DateTimeOffset.MinValue || lastUsed >= cutoff)
            {
                continue;
            }

            var target = Path.Combine(store.Root, "archive", relative.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
            archived++;
        }

        return archived;
    }

    private IEnumerable<string> MemoryFiles() => Directory.Exists(store.Root)
        ? Directory.EnumerateFiles(store.Root, "*.md", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(store.Root, f).Replace(Path.DirectorySeparatorChar, '/').StartsWith(".history/", StringComparison.Ordinal))
        : [];

    private static List<string> ExtractBullets(string text) =>
        [.. text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..].Trim())];

    private static string Normalize(string text) => WhitespacePattern().Replace(text.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"^/agents/[^/]+/daily/\d{4}-\d{2}-\d{2}\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex DailyPathPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();
}
