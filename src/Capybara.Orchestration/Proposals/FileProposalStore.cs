using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capybara.Contracts.Proposals;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capybara.Orchestration.Proposals;

/// <summary>One JSON file per proposal under <c>{state}/proposals</c>, flushed to disk and renamed into place.</summary>
public sealed partial class FileProposalStore : IProposalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;
    private readonly ILogger _logger;

    public FileProposalStore(string root, ILogger<FileProposalStore>? logger = null)
    {
        _root = Path.GetFullPath(root);
        _logger = logger ?? NullLogger<FileProposalStore>.Instance;
        Directory.CreateDirectory(_root);
    }

    public async ValueTask SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var file = PathFor(proposal.Id);
        var temp = file + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, proposal, JsonOptions, cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, file, overwrite: true);
    }

    public async ValueTask<ChangeProposal?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!IsValidId(id))
        {
            return null;
        }

        var file = PathFor(id);
        if (!File.Exists(file))
        {
            return null;
        }

        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<ChangeProposal>(stream, JsonOptions, cancellationToken);
    }

    /// <summary>Every readable proposal. A corrupt or unreadable file is logged and skipped so one bad file cannot hide the inbox.</summary>
    public async IAsyncEnumerable<ChangeProposal> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.json").Order(StringComparer.Ordinal))
        {
            ChangeProposal? proposal;
            try
            {
                await using var stream = File.OpenRead(file);
                proposal = await JsonSerializer.DeserializeAsync<ChangeProposal>(stream, JsonOptions, cancellationToken);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                LogUnreadable(_logger, Path.GetFileName(file), ex);
                continue;
            }

            if (proposal is not null)
            {
                yield return proposal;
            }
        }
    }

    /// <summary>Proposal ids are 32 lower-case hex chars, so an id can never address another file.</summary>
    internal static bool IsValidId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    private string PathFor(string id) =>
        IsValidId(id) ? Path.Combine(_root, id + ".json") : throw new ArgumentException("Invalid proposal id.", nameof(id));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipping unreadable proposal file {File}")]
    private static partial void LogUnreadable(ILogger logger, string file, Exception exception);
}
