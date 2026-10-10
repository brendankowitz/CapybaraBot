using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Capybara.Contracts.Messaging;
using Capybara.Memory.Pages;
using Capybara.Memory.Paths;
using Capybara.Memory.Safety;
using Capybara.Memory.Store;

namespace Capybara.Memory.Consolidation;

/// <summary>
/// Nightly low-privilege memory steward (FR-MEM-08): proposes promoting daily logs into durable notes, deleting duplicate
/// pages and archiving stale ones, then stages a review digest. It never mutates a page: every change is a
/// <see cref="PendingUserChange"/> a human approves through <see cref="Admin.MemoryAdmin.ResolveChangeAsync"/>. Re-running
/// while proposals are still pending does not stage them again, and the steward is capped at
/// <see cref="MemoryOptions.MaxPendingPerProposer"/> waiting proposals (the digest has its own slot).
/// </summary>
/// <remarks>
/// Promotion never launders content: daily logs that are quarantined, oversized or look injected are skipped, and a
/// promoted page is tainted when the existing notes page or any source log is tainted (a log with no taint frontmatter
/// counts as tainted).
/// </remarks>
public sealed partial class ConsolidationJob(WikiStore store, MemoryOptions options, TimeProvider time, IConsolidationSummarizer? summarizer = null)
{
    private const string StewardName = "steward";
    private const string DigestProposer = "steward:digest";
    private const string TruncationMarker = "\n…[digest truncated]\n";

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.ReparsePoint,
    };

    public async Task<ConsolidationReport> RunAsync(CancellationToken cancellationToken = default)
    {
        var run = new RunState();
        var digest = new StringBuilder();
        digest.AppendLine($"Consolidation run {time.GetUtcNow():yyyy-MM-dd}");
        digest.AppendLine();
        digest.AppendLine("Facts proposed for promotion:");
        var promotions = await ProposePromotionsAsync(digest, run, cancellationToken);
        var (duplicates, deleted) = await ProposeDuplicateDeletionsAsync(run, cancellationToken);
        var archives = await ProposeArchivesAsync(deleted, run, cancellationToken);
        var report = new ConsolidationReport(promotions, duplicates, archives, run.QueueFull);

        digest.AppendLine($"- promotions proposed: {report.PromotionsProposed}");
        digest.AppendLine($"- duplicates proposed for deletion: {report.DuplicatesProposed}");
        digest.AppendLine($"- stale pages proposed for archive: {report.ArchivesProposed}");
        if (report.QueueFullSkipped > 0)
        {
            digest.AppendLine($"- skipped because the review queue is full: {report.QueueFullSkipped}");
        }

        if (summarizer is not null)
        {
            digest.AppendLine(await summarizer.SummarizeAsync(digest.ToString(), cancellationToken));
        }

        var digestPath = MemoryPath.Parse($"/user/digests/consolidation-{time.GetUtcNow():yyyy-MM-dd}.md");
        if (store.PendingUserChanges.All(c => c.Path != digestPath.Value))
        {
            var file = Path.Combine(store.Root, digestPath.RelativeFileSystemPath);
            var before = File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken) : null;
            store.TryStage(PendingChangeKind.Write, digestPath, DigestProposer, before, BuildDigestPage(digest.ToString()), "nightly consolidation review digest");
        }

        return report;
    }

    private async Task<int> ProposePromotionsAsync(StringBuilder digest, RunState run, CancellationToken cancellationToken)
    {
        var byNotes = new SortedDictionary<string, (List<string> Lines, bool Tainted)>(StringComparer.Ordinal);
        foreach (var (dailyPath, daily) in LivePages().Where(p => DailyPathPattern().IsMatch(p.Path.Value)))
        {
            if (new FileInfo(daily).Length > options.MaxPageBytes || store.IsQuarantined(dailyPath.Value))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(daily, cancellationToken);
            if (LooksInjected(content))
            {
                continue;
            }

            var (metadata, body) = FrontmatterSerializer.Parse(content);
            var bullets = ExtractBullets(body);
            if (bullets.Count == 0)
            {
                continue;
            }

            var notesPath = $"/agents/{dailyPath.Owner}/notes.md";
            var (lines, tainted) = byNotes.TryGetValue(notesPath, out var entry) ? entry : ([], false);
            lines.AddRange(bullets.Select(b => $"- {b} _(from {dailyPath.Value})_"));
            byNotes[notesPath] = (lines, tainted || metadata.Taint == Taint.Tainted);
        }

        var proposed = 0;
        foreach (var (notesRelative, (candidates, sourcesTainted)) in byNotes)
        {
            var notesPath = MemoryPath.Parse(notesRelative);
            var notesFile = Path.Combine(store.Root, notesPath.RelativeFileSystemPath);
            var before = File.Exists(notesFile) ? await File.ReadAllTextAsync(notesFile, cancellationToken) : null;
            if (before is not null && (store.IsQuarantined(notesRelative) || LooksInjected(before)))
            {
                continue;
            }

            var (existingMetadata, body) = FrontmatterSerializer.Parse(before ?? string.Empty);
            var known = new HashSet<string>(ExtractBullets(body).Select(StripSource), StringComparer.OrdinalIgnoreCase);
            var additions = candidates.Where(c => known.Add(StripSource(c[2..]))).ToList();
            if (additions.Count == 0)
            {
                continue;
            }

            var now = time.GetUtcNow();
            var taint = sourcesTainted || (before is not null && existingMetadata.Taint == Taint.Tainted) ? Taint.Tainted : Taint.Clean;
            var metadata = before is null
                ? new PageMetadata { Description = "Durable agent notes", Source = "steward:consolidation", Created = now, Updated = now, ValidFrom = now, Taint = taint }
                : existingMetadata with { Updated = now, Source = "steward:consolidation", Taint = taint };
            var after = FrontmatterSerializer.Write(metadata, body.TrimEnd() + "\n" + string.Join('\n', additions) + "\n");
            if (Encoding.UTF8.GetByteCount(after) > options.MaxPageBytes)
            {
                continue;
            }

            if (Stage(run, PendingChangeKind.Write, notesRelative, before, after, "promote daily-log facts into durable notes"))
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

    private async Task<(int Proposed, HashSet<string> Paths)> ProposeDuplicateDeletionsAsync(RunState run, CancellationToken cancellationToken)
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
            if (!firstSeen.TryAdd(key, path.Value)
                && Stage(run, PendingChangeKind.Delete, path.Value, content, string.Empty, $"duplicate of {firstSeen[key]}"))
            {
                proposedPaths.Add(path.Value);
            }
        }

        return (proposedPaths.Count, proposedPaths);
    }

    private async Task<int> ProposeArchivesAsync(HashSet<string> alreadyProposedForDeletion, RunState run, CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().AddDays(-options.ArchiveAfterDays);
        var proposed = 0;
        foreach (var (path, file) in LivePages())
        {
            // The profile is injected into every turn, so "unused" never applies to it.
            if (alreadyProposedForDeletion.Contains(path.Value) || path.Value == ProfilePath)
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

            if (Stage(run, PendingChangeKind.Archive, path.Value, content, string.Empty, $"unused since {lastUsed:yyyy-MM-dd}"))
            {
                proposed++;
            }
        }

        return proposed;
    }

    private const string ProfilePath = "/user/profile.md";

    /// <summary>Stages a steward proposal; counts it as skipped when the queue is full. Returns whether it was newly staged.</summary>
    private bool Stage(RunState run, PendingChangeKind kind, string path, string? before, string after, string reason)
    {
        switch (store.TryStage(kind, MemoryPath.Parse(path), StewardName, before, after, reason))
        {
            case PendingAddResult.Added:
                return true;
            case PendingAddResult.QueueFull:
                run.QueueFull++;
                return false;
            default:
                return false;
        }
    }

    /// <summary>The digest page, truncated with a marker so it always fits the page size cap. Tainted: it quotes agent-written logs.</summary>
    private string BuildDigestPage(string text)
    {
        var now = time.GetUtcNow();
        var metadata = new PageMetadata
        {
            Description = "Consolidation review digest",
            Source = "steward:consolidation",
            Created = now,
            Updated = now,
            ValidFrom = now,
            Taint = Taint.Tainted,
        };
        var page = FrontmatterSerializer.Write(metadata, text);
        if (Encoding.UTF8.GetByteCount(page) <= options.MaxPageBytes)
        {
            return page;
        }

        var budget = options.MaxPageBytes - Encoding.UTF8.GetByteCount(FrontmatterSerializer.Write(metadata, TruncationMarker));
        var keep = Math.Min(text.Length, Math.Max(budget, 0));
        while (keep > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, keep)) > budget)
        {
            keep -= Math.Max(1, keep / 10);
        }

        if (keep > 0 && char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return FrontmatterSerializer.Write(metadata, text[..Math.Max(keep, 0)] + TruncationMarker);
    }

    /// <summary>The scanner's regex has a timeout; a timeout means hostile or pathological input, so treat it as injected.</summary>
    private static bool LooksInjected(string content)
    {
        try
        {
            return InjectionScanner.LooksInjected(content);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    /// <summary>Pages in the live wiki, in a stable order. History, archive and the pending area are not live.</summary>
    private IEnumerable<(MemoryPath Path, string File)> LivePages()
    {
        foreach (var file in Directory.EnumerateFiles(store.Root, "*.md", Walk).Order(StringComparer.Ordinal))
        {
            var relative = "/" + Path.GetRelativePath(store.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (MemoryPath.TryParse(relative, out var path, out _))
            {
                yield return (path, file);
            }
        }
    }

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

    private sealed class RunState
    {
        public int QueueFull { get; set; }
    }
}
