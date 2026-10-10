using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Capybara.Contracts.Messaging;
using Capybara.Memory.Access;
using Capybara.Memory.Abstractions;
using Capybara.Memory.Context;
using Capybara.Memory.Pages;
using Capybara.Memory.Paths;
using Capybara.Memory.Safety;
using Capybara.Memory.Search;
using Capybara.Memory.Versioning;

namespace Capybara.Memory.Store;

/// <summary>
/// Host-owned markdown wiki (FR-MEM-01..07). Implements <c>memory_list</c>, <c>memory_read</c>, <c>memory_search</c>,
/// <c>remember</c> and <c>forget</c> with scope rules, path safety, provenance, injection quarantine and staged
/// <c>/user/</c> edits.
/// </summary>
public sealed class WikiStore : IMemoryStore, IDisposable
{
    private const int CharsPerToken = 4;

    private readonly MemoryOptions _options;
    private readonly IMemoryVersioning _versioning;
    private readonly TimeProvider _time;
    private readonly FtsMemoryIndex? _index;
    private readonly string _root;
    private readonly FilePendingChangeStore _pending;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _quarantined = new(StringComparer.Ordinal);

    public WikiStore(MemoryOptions options, IMemoryVersioning versioning, TimeProvider time)
        : this(options, versioning, time, null)
    {
    }

    public WikiStore(MemoryOptions options, IMemoryVersioning versioning, TimeProvider time, FtsMemoryIndex? index)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _versioning = versioning;
        _time = time;
        _index = index;
        _root = Path.GetFullPath(options.Root);
        Directory.CreateDirectory(_root);
        _pending = new FilePendingChangeStore(_root);
    }

    public IReadOnlyCollection<string> Quarantined => [.. _quarantined.Keys];

    /// <summary>Staged changes awaiting human approval. Persisted under the memory root, so they survive restarts (FR-MEM-05).</summary>
    public IReadOnlyList<PendingUserChange> PendingUserChanges => _pending.List();

    internal string Root => _root;

    public void Dispose() => _gate.Dispose();

    /// <summary>Rebuilds the SQLite FTS5 index from readable disk pages (FR-MEM-11).</summary>
    public async Task RebuildSearchIndexAsync(CancellationToken cancellationToken = default)
    {
        if (_index is null)
        {
            return;
        }

        var pages = new List<MemoryPage>();
        foreach (var path in EnumeratePaths())
        {
            if (await LoadAsync(path, ResolveFile(path), cancellationToken) is { } page)
            {
                pages.Add(page);
            }
        }

        await _index.RebuildAsync(pages, cancellationToken);
    }

    /// <summary>
    /// <c>memory_read</c>. Quarantined pages are never returned to a model. A successful read refreshes the page's
    /// <c>lastAccessed</c> provenance, debounced by <see cref="MemoryOptions.LastAccessedDebounce"/> (FR-MEM-04).
    /// </summary>
    public async Task<MemoryPage?> ReadAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default)
    {
        var page = await PeekAsync(context, rawPath, cancellationToken);
        return page is null ? null : await TouchAsync(page, cancellationToken);
    }

    /// <summary>Same access rules as <see cref="ReadAsync"/> but records no access; used for bulk reads such as the per-turn index.</summary>
    internal async Task<MemoryPage?> PeekAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken)
    {
        if (context.Incognito)
        {
            return null;
        }

        var path = ParseReadable(context, rawPath);
        var file = ResolveFile(path);
        if (!File.Exists(file))
        {
            return null;
        }

        var page = await LoadAsync(path, file, cancellationToken);
        return page is null || _quarantined.ContainsKey(path.Value) ? null : page;
    }

    /// <summary><c>memory_list</c>: every readable page path under an optional prefix.</summary>
    public IReadOnlyList<string> List(MemoryAccessContext context, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Incognito)
        {
            return [];
        }

        return [.. EnumeratePaths()
            .Where(p => MemoryAccessPolicy.CanRead(context, p) && !_quarantined.ContainsKey(p.Value))
            .Where(p => prefix is null || p.Value.StartsWith(prefix, StringComparison.Ordinal))
            .Select(p => p.Value)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary><c>memory_search</c>: case-insensitive term match over readable pages, best first.</summary>
    public async Task<IReadOnlyList<IndexEntry>> SearchAsync(MemoryAccessContext context, string query, int limit = 10, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Incognito)
        {
            return [];
        }

        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        if (_index is not null && await _index.ApproximateTokenCountAsync(cancellationToken) >= _options.FtsSearchTokenThreshold)
        {
            return await _index.SearchAsync(context, query, limit, cancellationToken);
        }

        var scored = new List<(IndexEntry Entry, int Score)>();
        foreach (var page in await LoadReadableAsync(context, cancellationToken))
        {
            var haystack = page.Metadata.Description + "\n" + page.Body;
            var score = terms.Sum(t => CountOccurrences(haystack, t));
            if (score > 0)
            {
                scored.Add((ToEntry(page), score));
            }
        }

        return [.. scored.OrderByDescending(s => s.Score).ThenByDescending(s => s.Entry.Updated).Take(limit).Select(s => s.Entry)];
    }

    /// <summary>
    /// The ranked table of contents injected every turn, within a token budget. Bodies load only via <c>memory_read</c>.
    /// Returned as a delimited data block so it is never mistaken for instructions (FR-MEM-10).
    /// </summary>
    public async Task<string> BuildIndexAsync(MemoryAccessContext context, CancellationToken cancellationToken = default)
    {
        var builder = new MemoryContextBuilder(this, _options) { Reader = PeekAsync };
        return await builder.BuildAsync(context, cancellationToken);
    }

    /// <summary>
    /// <c>remember</c>: merge a fact into a page with an ADD/UPDATE/DELETE/NOOP decision instead of appending duplicates.
    /// A fact that negates an existing bullet ("no longer ...", "doesn't ...") DELETEs that bullet; the previous page
    /// version stays in <c>.history</c>. <c>/user/</c> facts are staged for review.
    /// </summary>
    public async Task<MemoryOperation> RememberAsync(
        MemoryAccessContext context,
        string rawPath,
        string fact,
        string description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(fact);
        if (context.Incognito)
        {
            return MemoryOperation.NoOp;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await RememberCoreAsync(context, rawPath, fact, description, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MemoryOperation> RememberCoreAsync(
        MemoryAccessContext context,
        string rawPath,
        string fact,
        string description,
        CancellationToken cancellationToken)
    {
        var path = Parse(rawPath);
        var proposesUserChange = MemoryAccessPolicy.CanProposeUserChange(context, path);
        if (!proposesUserChange)
        {
            // Authorize before reading the page, so dedupe results never leak another scope's content.
            EnsureWritable(context, path);
        }
        else if (!context.CanReadUser)
        {
            throw new MemoryAccessDeniedException($"{context.Agent} may not propose changes to {path}.");
        }

        EnsureNoSensitiveData(fact);

        var file = ResolveFile(path);
        var existing = File.Exists(file) ? await LoadAsync(path, file, cancellationToken) : null;
        var bullet = "- " + fact.Trim();

        string body;
        MemoryOperation operation;
        if (existing is not null && FactNegation.Retract(existing.Body, fact.Trim()) is { } retracted)
        {
            body = retracted.Trim().Length == 0 ? string.Empty : retracted.TrimEnd() + "\n";
            operation = MemoryOperation.Delete;
        }
        else if (existing is not null && existing.Body.Contains(fact.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return MemoryOperation.NoOp;
        }
        else
        {
            body = existing is null ? bullet + "\n" : existing.Body.TrimEnd() + "\n" + bullet + "\n";
            operation = existing is null ? MemoryOperation.Add : MemoryOperation.Update;
        }

        var now = _time.GetUtcNow();
        if (existing is not null)
        {
            await WriteHistoryAsync(path, existing, now, cancellationToken);
        }

        var metadata = (existing?.Metadata ?? new PageMetadata { Created = now }) with
        {
            Description = string.IsNullOrWhiteSpace(description) ? existing?.Metadata.Description ?? string.Empty : description,
            Source = $"agent:{context.Agent}/{context.SessionId}",
            Updated = now,
            LastValidated = now,
            ValidFrom = now,

            // A page whose last fact was retracted has no live content left: close its validity window.
            ValidTo = string.IsNullOrWhiteSpace(body) ? now : null,
            Taint = existing?.Metadata.Taint == Taint.Tainted ? Taint.Tainted : context.SessionTaint,
        };
        var content = FrontmatterSerializer.Write(metadata, body);
        EnsureSize(content);

        if (proposesUserChange)
        {
            var before = File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken) : null;
            var change = new PendingUserChange(Guid.NewGuid().ToString("N"), path.Value, context.Agent.Value, before, content, now);
            _pending.Add(change);
            return MemoryOperation.Proposed;
        }

        await WriteFileAsync(file, content, cancellationToken);
        await SyncIndexAsync(new MemoryPage(path, metadata, body), cancellationToken);
        await _versioning.CommitAsync($"{operation.ToString().ToLowerInvariant()} {path} by {context.Agent}", cancellationToken);
        return operation;
    }

    /// <summary>
    /// Records that an agent re-verified a page against its source system (FR-MEM-07): sets <c>lastValidated</c>
    /// (and <c>lastAccessed</c>) to now. Requires direct write access, so <c>/user/</c> pages are not eligible.
    /// </summary>
    public async Task<MemoryOperation> MarkValidatedAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Incognito)
        {
            return MemoryOperation.NoOp;
        }

        var path = Parse(rawPath);
        EnsureWritable(context, path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var file = ResolveFile(path);
            var page = File.Exists(file) ? await LoadAsync(path, file, cancellationToken) : null;
            if (page is null || page.Metadata.ValidTo is not null)
            {
                return MemoryOperation.NoOp;
            }

            var now = _time.GetUtcNow();
            await WriteFileAsync(file, FrontmatterSerializer.Write(page.Metadata with { LastValidated = now, LastAccessed = now }, page.Body), cancellationToken);
            await _versioning.CommitAsync($"revalidate {path} by {context.Agent}", cancellationToken);
            return MemoryOperation.Update;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Supersede a page instead of overwriting it (FR-MEM-09): marks it <c>validTo = now</c> so it leaves the index but
    /// stays in history.
    /// </summary>
    public async Task<MemoryOperation> SupersedeAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default)
    {
        if (context.Incognito)
        {
            return MemoryOperation.NoOp;
        }

        var path = Parse(rawPath);
        EnsureWritable(context, path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var file = ResolveFile(path);
            var page = File.Exists(file) ? await LoadAsync(path, file, cancellationToken) : null;
            if (page is null || page.Metadata.ValidTo is not null)
            {
                return MemoryOperation.NoOp;
            }

            var metadata = page.Metadata with { ValidTo = _time.GetUtcNow() };
            await WriteFileAsync(file, FrontmatterSerializer.Write(metadata, page.Body), cancellationToken);
            if (_index is not null)
            {
                await _index.DeleteAsync(path.Value, cancellationToken);
            }

            await _versioning.CommitAsync($"supersede {path} by {context.Agent}", cancellationToken);
            return MemoryOperation.Update;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary><c>forget</c>: delete a page in a directly writable scope.</summary>
    public async Task<MemoryOperation> ForgetAsync(MemoryAccessContext context, string rawPath, CancellationToken cancellationToken = default)
    {
        if (context.Incognito)
        {
            return MemoryOperation.NoOp;
        }

        var path = Parse(rawPath);
        EnsureWritable(context, path);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var file = ResolveFile(path);
            if (!File.Exists(file))
            {
                return MemoryOperation.NoOp;
            }

            File.Delete(file);
            if (_index is not null)
            {
                await _index.DeleteAsync(path.Value, cancellationToken);
            }

            await _versioning.CommitAsync($"forget {path} by {context.Agent}", cancellationToken);
            return MemoryOperation.Delete;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Human-only: apply or discard a staged change (a <c>/user/</c> proposal or a consolidation proposal) from the PWA.
    /// Returns <see langword="false"/> when the id is unknown, or when applying and the page changed since it was
    /// proposed, in which case the stale proposal is discarded rather than clobbering newer content.
    /// </summary>
    public async Task<bool> ResolveUserChangeAsync(string changeId, bool apply, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_pending.TryRemove(changeId, out var change) || change is null)
            {
                return false;
            }

            if (!apply)
            {
                return true;
            }

            var path = MemoryPath.Parse(change.Path);
            var file = ResolveFile(path);
            var current = File.Exists(file) ? await File.ReadAllTextAsync(file, cancellationToken) : null;
            if (!SameBody(change.Before, current))
            {
                return false;
            }

            if (current is not null)
            {
                var (currentMetadata, currentBody) = FrontmatterSerializer.Parse(current);
                await WriteHistoryAsync(path, new MemoryPage(path, currentMetadata, currentBody), _time.GetUtcNow(), cancellationToken);
            }

            switch (change.Kind)
            {
                case PendingChangeKind.Write:
                    await WriteFileAsync(file, change.After, cancellationToken);
                    var (metadata, body) = FrontmatterSerializer.Parse(change.After);
                    await SyncIndexAsync(new MemoryPage(path, metadata, body), cancellationToken);
                    break;
                case PendingChangeKind.Delete:
                    File.Delete(file);
                    await DeleteFromIndexAsync(path, cancellationToken);
                    break;
                case PendingChangeKind.Archive:
                    var target = Path.Combine(_root, "archive", path.RelativeFileSystemPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Move(file, target, overwrite: true);
                    await DeleteFromIndexAsync(path, cancellationToken);
                    break;
            }

            await _versioning.CommitAsync($"approve {change.Kind.ToString().ToLowerInvariant()} {path} proposed by {change.ProposedBy}", cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stages a proposal on any page (used by consolidation, FR-MEM-08). Nothing on disk changes until a human resolves it.
    /// Returns <see langword="false"/> when an equivalent proposal is already pending, so nightly runs do not pile up.
    /// </summary>
    internal bool TryStage(PendingChangeKind kind, MemoryPath path, string proposedBy, string? before, string after, string reason)
    {
        var duplicate = _pending.List().Any(p =>
            p.Kind == kind
            && string.Equals(p.Path, path.Value, StringComparison.Ordinal)
            && (kind != PendingChangeKind.Write || SameBody(p.After, after)));
        if (duplicate)
        {
            return false;
        }

        _pending.Add(new PendingUserChange(Guid.NewGuid().ToString("N"), path.Value, proposedBy, before, after, _time.GetUtcNow())
        {
            Kind = kind,
            Reason = reason,
        });
        return true;
    }

    /// <summary>Human edit of any page (FR-MEM-12). Keeps provenance, stores the prior version in history, refreshes the index.</summary>
    internal async Task WriteHumanPageAsync(MemoryPath path, string description, string body, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var file = ResolveFile(path);
            var now = _time.GetUtcNow();
            PageMetadata metadata;
            if (File.Exists(file))
            {
                var (existing, existingBody) = FrontmatterSerializer.Parse(await File.ReadAllTextAsync(file, cancellationToken));
                await WriteHistoryAsync(path, new MemoryPage(path, existing, existingBody), now, cancellationToken);
                metadata = existing with { Description = description, Updated = now, LastValidated = now, Source = "user:pwa" };
            }
            else
            {
                metadata = new PageMetadata { Description = description, Source = "user:pwa", Created = now, Updated = now, LastValidated = now, ValidFrom = now };
            }

            var content = FrontmatterSerializer.Write(metadata, body);
            EnsureSize(content);
            await WriteFileAsync(file, content, cancellationToken);
            if (InjectionScanner.LooksInjected(content))
            {
                // Stays on disk for review but never reaches a model or the index.
                _quarantined.TryAdd(path.Value, 0);
                await DeleteFromIndexAsync(path, cancellationToken);
            }
            else
            {
                _quarantined.TryRemove(path.Value, out _);
                await SyncIndexAsync(new MemoryPage(path, metadata, body), cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Human delete of any page (FR-MEM-12). The deleted content is kept in history.</summary>
    internal async Task<bool> DeleteHumanPageAsync(MemoryPath path, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var file = ResolveFile(path);
            if (!File.Exists(file))
            {
                return false;
            }

            var (metadata, body) = FrontmatterSerializer.Parse(await File.ReadAllTextAsync(file, cancellationToken));
            await WriteHistoryAsync(path, new MemoryPage(path, metadata, body), _time.GetUtcNow(), cancellationToken);
            File.Delete(file);
            _quarantined.TryRemove(path.Value, out _);
            await DeleteFromIndexAsync(path, cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<MemoryPage?> TouchAsync(MemoryPage page, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (IsFresh(page.Metadata, now))
        {
            return page;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Re-read under the gate so a concurrent write is never overwritten with stale content.
            var file = ResolveFile(page.Path);
            if (!File.Exists(file) || await LoadAsync(page.Path, file, cancellationToken) is not { } current)
            {
                return null;
            }

            if (IsFresh(current.Metadata, now))
            {
                return current;
            }

            var touched = current with { Metadata = current.Metadata with { LastAccessed = now } };
            await WriteFileAsync(file, FrontmatterSerializer.Write(touched.Metadata, touched.Body), cancellationToken);
            return touched;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsFresh(PageMetadata metadata, DateTimeOffset now) =>
        metadata.LastAccessed is { } last && now - last < _options.LastAccessedDebounce;

    /// <summary>True when the two file contents have the same page body; metadata-only drift (such as <c>lastAccessed</c>) is ignored.</summary>
    private static bool SameBody(string? left, string? right) =>
        left is null || right is null
            ? left is null && right is null
            : string.Equals(FrontmatterSerializer.Parse(left).Body, FrontmatterSerializer.Parse(right).Body, StringComparison.Ordinal);

    private async Task SyncIndexAsync(MemoryPage page, CancellationToken cancellationToken)
    {
        if (_index is null)
        {
            return;
        }

        if (page.Metadata.ValidTo is null)
        {
            await _index.UpsertAsync(page, cancellationToken);
        }
        else
        {
            await _index.DeleteAsync(page.Path.Value, cancellationToken);
        }
    }

    private async Task DeleteFromIndexAsync(MemoryPath path, CancellationToken cancellationToken)
    {
        if (_index is not null)
        {
            await _index.DeleteAsync(path.Value, cancellationToken);
        }
    }

    private static int CountOccurrences(string haystack, string term)
    {
        var count = 0;
        for (var i = haystack.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0; i = haystack.IndexOf(term, i + term.Length, StringComparison.OrdinalIgnoreCase))
        {
            count++;
        }

        return count;
    }

    private static IndexEntry ToEntry(MemoryPage page) => new(page.Path.Value, page.Metadata.Description, page.Metadata.Updated);

    private static MemoryPath Parse(string rawPath) =>
        MemoryPath.TryParse(rawPath, out var path, out var error) ? path : throw new MemoryAccessDeniedException(error);

    private static MemoryPath ParseReadable(MemoryAccessContext context, string rawPath)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Parse(rawPath);
        return MemoryAccessPolicy.CanRead(context, path)
            ? path
            : throw new MemoryAccessDeniedException($"{context.Agent} may not read {path}.");
    }

    private static void EnsureWritable(MemoryAccessContext context, MemoryPath path)
    {
        if (!MemoryAccessPolicy.CanWriteDirectly(context, path))
        {
            throw new MemoryAccessDeniedException($"{context.Agent} may not write {path}.");
        }
    }

    private static void EnsureNoSensitiveData(string content)
    {
        if (SensitiveDataFilter.FindViolation(content) is { } violation)
        {
            throw new MemoryAccessDeniedException($"Refusing to store a {violation} in memory.");
        }
    }

    private void EnsureSize(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > _options.MaxPageBytes)
        {
            throw new MemoryAccessDeniedException($"Page exceeds the {_options.MaxPageBytes}-byte cap.");
        }
    }

    /// <summary>Maps a validated path onto disk and refuses anything that escapes the root or crosses a symlink.</summary>
    private string ResolveFile(MemoryPath path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, path.RelativeFileSystemPath));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new MemoryAccessDeniedException("Path escapes the memory root.");
        }

        for (var current = full; current.Length > _root.Length; current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                throw new MemoryAccessDeniedException("Symbolic links are not allowed in memory.");
            }
        }

        return full;
    }

    private async Task<MemoryPage?> LoadAsync(MemoryPath path, string file, CancellationToken cancellationToken)
    {
        var info = new FileInfo(file);
        if (info.Length > _options.MaxPageBytes)
        {
            _quarantined.TryAdd(path.Value, 0);
            return null;
        }

        var content = await File.ReadAllTextAsync(file, cancellationToken);
        if (InjectionScanner.LooksInjected(content))
        {
            _quarantined.TryAdd(path.Value, 0);
            return null;
        }

        _quarantined.TryRemove(path.Value, out _);
        var (metadata, body) = FrontmatterSerializer.Parse(content);
        return new MemoryPage(path, metadata, body);
    }

    private async Task<List<MemoryPage>> LoadReadableAsync(MemoryAccessContext context, CancellationToken cancellationToken)
    {
        var pages = new List<MemoryPage>();
        foreach (var path in EnumeratePaths().Where(p => MemoryAccessPolicy.CanRead(context, p)))
        {
            if (await LoadAsync(path, ResolveFile(path), cancellationToken) is { } page)
            {
                pages.Add(page);
            }
        }

        return pages;
    }

    private async Task WriteHistoryAsync(MemoryPath path, MemoryPage page, DateTimeOffset validTo, CancellationToken cancellationToken)
    {
        var relative = path.Value.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var historyFile = Path.Combine(_root, ".history", Path.ChangeExtension(relative, null), validTo.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + ".md");
        var metadata = page.Metadata with { ValidTo = validTo };
        await WriteFileAsync(historyFile, FrontmatterSerializer.Write(metadata, page.Body), cancellationToken);
    }

    private IEnumerable<MemoryPath> EnumeratePaths()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.ReparsePoint };
        foreach (var file in Directory.EnumerateFiles(_root, "*.md", options))
        {
            var relative = "/" + Path.GetRelativePath(_root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (MemoryPath.TryParse(relative, out var path, out _))
            {
                yield return path;
            }
        }
    }

    private static async Task WriteFileAsync(string file, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        await File.WriteAllTextAsync(temp, content, cancellationToken);
        File.Move(temp, file, overwrite: true);
    }
}
