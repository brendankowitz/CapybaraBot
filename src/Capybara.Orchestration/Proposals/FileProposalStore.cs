using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capybara.Contracts.Proposals;

namespace Capybara.Orchestration.Proposals;

/// <summary>One JSON file per proposal under <c>{state}/proposals</c>, written atomically.</summary>
public sealed class FileProposalStore : IProposalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _root;

    public FileProposalStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public async ValueTask SaveAsync(ChangeProposal proposal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        var file = PathFor(proposal.Id);
        var temp = file + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, proposal, JsonOptions, cancellationToken);
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

    public async IAsyncEnumerable<ChangeProposal> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*.json").Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(file);
            if (await JsonSerializer.DeserializeAsync<ChangeProposal>(stream, JsonOptions, cancellationToken) is { } proposal)
            {
                yield return proposal;
            }
        }
    }

    /// <summary>Proposal ids are 32 lower-case hex chars, so an id can never address another file.</summary>
    internal static bool IsValidId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    private string PathFor(string id) =>
        IsValidId(id) ? Path.Combine(_root, id + ".json") : throw new ArgumentException("Invalid proposal id.", nameof(id));
}
