using System.Text.Json;
using System.Text.Json.Serialization;
using Capybara.Contracts.Approvals;
using Capybara.Contracts.Storage;

namespace Capybara.Policy.Approvals;

/// <summary>
/// JSONL approval journal: every state change appends a snapshot and the last snapshot per id wins on replay. On load
/// the journal is compacted to the approvals that are still pending or approved (settled ones are in the audit log),
/// and a torn last line from a crash is dropped. Callers must serialize access, as <see cref="ApprovalService"/> does.
/// </summary>
public sealed class FileApprovalStore(string path) : IApprovalStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<ApprovalRequest> Load()
    {
        var latest = new Dictionary<string, ApprovalRequest>(StringComparer.Ordinal);
        foreach (var snapshot in JsonlFile.Replay<ApprovalRequest>(path, Json))
        {
            latest[snapshot.Id] = snapshot;
        }

        var live = latest.Values
            .Where(r => r.Status is ApprovalStatus.Pending or ApprovalStatus.Approved)
            .OrderBy(r => r.CreatedAt)
            .ToList();
        if (File.Exists(path))
        {
            JsonlFile.Rewrite(path, live, Json);
        }

        return live;
    }

    public void Save(ApprovalRequest request) => JsonlFile.Append(path, request, Json);
}
