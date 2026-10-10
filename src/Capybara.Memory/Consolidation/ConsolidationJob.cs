using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Capybara.Contracts.Agents;
using Capybara.Memory.Access;
using Capybara.Memory.Pages;
using Capybara.Memory.Paths;
using Capybara.Memory.Store;

namespace Capybara.Memory.Consolidation;

/// <summary>
/// Nightly low-privilege memory steward (FR-MEM-08): proposes promoting daily logs into durable notes, deleting duplicate
/// pages and archiving stale ones, then stages a review digest. It never mutates a page: every change is a
/// <see cref="PendingUserChange"/> a human approves through <see cref="WikiStore.ResolveUserChangeAsync"/>. Re-running
/// while proposals are still pending does not stage them again.
/// </summary>
public sealed partial class ConsolidationJob(WikiStore store, MemoryOptions options, TimeProvider time, IConsolidationSummarizer? summarizer = null)
{
    private const string StewardName = "steward";

    private static readonly MemoryAccessContext StewardContext = new()
    {
        Agent = AgentId.Parse(StewardName),
        SessionId = "steward:consolidation",
        CanReadUser = true,
    };

    public async Task<ConsolidationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var digest = new StringBuilder();
        digest.AppendLine($"Consolidation run {time.GetUtcNow():yyyy-MM-dd}");
        digest.AppendLine();
        digest.AppendLine("Facts proposed for promotion:");
        var promotions = await ProposePromotionsAsync(digest, cancellationToken);
        var (duplicates, deleted) = await ProposeDuplicateDeletionsAsync(cancellationToken);
        var archives = await ProposeArchivesAsync(deleted, cancellationToken);
        var report = new ConsolidationReport(promotions, duplicates, archives);

        digest.AppendLine($"- promotions proposed: {report.PromotionsProposed}");
        digest.AppendLine($"- duplicates proposed for deletion: {report.DuplicatesProposed}");
        digest.AppendLine($"- stale pages proposed for archive: {report.ArchivesProposed}");
        if (summarizer is not null)
        {
            digest.AppendLine(await summarizer.SummarizeAsync(digest.ToString(), cancellationToken));
        }

        var digestPath = $"/user/digests/consolidation-{time.GetUtcNow():yyyy-MM-dd}.md";
        if (store.PendingUserChanges.All(c => c.Path != digestPath))
        {
            await store.RememberAsync(StewardContext, digestPath, digest.ToString(), "Consolidation review digest", cancellationToken);
        }

        return report;
    }

    private async Task<int> ProposePromotionsAsync(StringBuilder digest, CancellationToken cancellationToken)
    {
        var additionsByNotes = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var daily in Directory.EnumerateFiles(store.Root, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = RelativePath(daily);
            if (!DailyPathPattern().IsMatch(relative) || !MemoryPath.TryParse(relative, out var dailyPath, out _))
            {
                continue;
            }

            var notesPath = $"/agents/{dailyPath.Owner}/notes.md";
            var bullets = ExtractBullets(await File.ReadAllTextAsync(daily, cancellationToken));
            if (bullets.Count == 0)
            {
                continue;
            }

            if (!additionsByNotes.TryGetValue(notesPath, out var list))
            {
                additionsByNotes[notesPath] = list = [];
            }

            list.AddRange(bullets.Select(b => $"- {b} _(from {relative})_"));
        }

        var proposed = 0;
        foreach (var (notesRelative, candidates) in additionsByNotes)
        {
            var notesPath = MemoryPath.Parse(notesRelative);
            var notesFile = Path.Combine(store.Root, notesPath.RelativeFileSystemPath);
            var before = File.Exists(notesFile) ? await File.ReadAllTextAsync(notesFile, cancellationToken) : null;
            var (existingMetadata, body) = FrontmatterSerializer.Parse(before ?? string.Empty);
            var known = new HashSet<string>(ExtractBullets(body).Select(StripSource), StringComparer.OrdinalIgnoreCase);
            var additions = candidates.Where(c => known.Add(StripSource(c[2..]))).ToList();
            if (additions.Count == 0)
            {
                continue;
            }

            var now = time.GetUtcNow();
            var metadata = before is null
                ? new PageMetadata { Description = "Durable agent notes", Source = "steward:consolidation", Created = now, Updated = now, ValidFrom = now }
                : existingMetadata with { Updated = now, Source = "steward:consolidation" };
            var after = FrontmatterSerializer.Write(metadata, body.TrimEnd() + "\n" + string.Join('\n', additions) + "\n");
            if (store.TryStage(PendingChangeKind.Write, notesPath, StewardName, before, after, "promote daily-log facts into durable notes"))
            {
                proposed++;
                foreach (var addition in additions)
                {
                    digest.AppendLine(addition);
                }
            }
        }

        return proposed;
    }

    private async Task<(int Proposed, HashSet<string> Paths)> ProposeDuplicateDeletionsAsync(CancellationToken cancellationToken)
    {
        var firstSeen = new Dictionary<string, string>(StringComparer.Ordinal);
        var proposedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, file) in LivePages())
        {
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var normalized = Normalize(FrontmatterSerializer.Parse(content).Body);
            if (normalized.Length == 0)
            {
                continue;
            }

            // Duplicates are only compared inside one scope owner so one agent's page never deletes another's.
            var key = $"{path.Scope}:{path.Owner}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))}";
            if (!firstSeen.TryAdd(key, path.Value))
            {
                if (store.TryStage(PendingChangeKind.Delete, path, StewardName, content, string.Empty, $"duplicate of {firstSeen[key]}"))
                {
                    proposedPaths.Add(path.Value);
                }
            }
        }

        return (proposedPaths.Count, proposedPaths);
    }

    private async Task<int> ProposeArchivesAsync(HashSet<string> alreadyProposedForDeletion, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-options.ArchiveAfterDays);
        var proposed = 0;
        foreach (var (path, file) in LivePages())
        {
            if (alreadyProposedForDeletion.Contains(path.Value))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(file, cancellationToken);
            var (metadata, _) = FrontmatterSerializer.Parse(content);
            var lastUsed = metadata.LastAccessed ?? metadata.Updated;
            if (lastUsed == DateTimeOffset.MinValue || lastUsed >= cutoff)
            {
                continue;
            }

            if (store.TryStage(PendingChangeKind.Archive, path, StewardName, content, string.Empty, $"unused since {lastUsed:yyyy-MM-dd}"))
            {
                proposed++;
            }
        }

        return proposed;
    }

    /// <summary>Pages in the live wiki, in a stable order. History and archive are not live.</summary>
    private IEnumerable<(MemoryPath Path, string File)> LivePages()
    {
        foreach (var file in Directory.EnumerateFiles(store.Root, "*.md", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (MemoryPath.TryParse(RelativePath(file), out var path, out _))
            {
                yield return (path, file);
            }
        }
    }

    private string RelativePath(string file) => "/" + Path.GetRelativePath(store.Root, file).Replace(Path.DirectorySeparatorChar, '/');

    private static List<string> ExtractBullets(string text) =>
        [.. text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..].Trim())];

    private static string StripSource(string bullet) => SourceSuffixPattern().Replace(bullet, string.Empty).Trim();

    private static string Normalize(string text) => WhitespacePattern().Replace(text.ToLowerInvariant(), " ").Trim();

    [GeneratedRegex(@"^/agents/[^/]+/daily/\d{4}-\d{2}-\d{2}\.md$", RegexOptions.CultureInvariant)]
    private static partial Regex DailyPathPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"\s*_\(from [^)]*\)_\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SourceSuffixPattern();
}
