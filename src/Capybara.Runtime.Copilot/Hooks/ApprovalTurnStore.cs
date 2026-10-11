using System.Text.Json;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Runtime.Copilot.Configuration;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hooks;

/// <summary>
/// The turn an approval interrupted: its envelope (depth, remaining budget, deadline, root task and root origin), its
/// origin and the agent whose message started it. The turn that resumes after the decision continues under it, so an
/// approval can never turn an agent-started or event-started turn into a fresh, user-facing root (FR-A2A-05/07). A turn a
/// routine ran also keeps that routine's attribution and approval boundary (FR-OBS-03, FR-RTN-02), so a heartbeat's
/// resumed main-session turn still needs approval for every tool its routine declared.
/// </summary>
public sealed record ApprovalTurnContext(
    Envelope Envelope,
    MessageOrigin Origin,
    AgentId? From,
    string? RoutineId = null,
    IReadOnlyList<string>? ApprovalBoundary = null);

/// <summary>
/// Durable <see cref="ApprovalTurnContext"/>s keyed by approval id, one small file each under
/// <c>{StateRoot}/approvals/</c>, so a resume after a restart still continues the right chain. Thread-safe.
/// </summary>
public sealed partial class ApprovalTurnStore(CopilotRuntimeOptions options, ILogger<ApprovalTurnStore> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(options.StateRoot, "approvals");

    /// <summary>Records the context of the turn that raised <paramref name="approvalId"/>. Failures are logged; the resume then fails closed.</summary>
    public void Save(string approvalId, ApprovalTurnContext context)
    {
        if (PathFor(approvalId) is not { } path)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_root);
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(context, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnavailable(logger, ex, approvalId);
        }
    }

    /// <summary>Returns and forgets the context of <paramref name="approvalId"/>; <see langword="null"/> when none was recorded.</summary>
    public ApprovalTurnContext? Take(string approvalId)
    {
        if (PathFor(approvalId) is not { } path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var context = JsonSerializer.Deserialize<ApprovalTurnContext>(File.ReadAllText(path), Json);
            File.Delete(path);
            return context;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogUnavailable(logger, ex, approvalId);
            return null;
        }
    }

    /// <summary>Approval ids are generated hex; anything else addresses no file.</summary>
    private string? PathFor(string approvalId) =>
        approvalId is { Length: > 0 and <= 64 } && approvalId.All(char.IsAsciiLetterOrDigit) ? Path.Combine(_root, approvalId + ".json") : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "The turn context of approval {ApprovalId} could not be stored or read; its resume will be restricted")]
    private static partial void LogUnavailable(ILogger logger, Exception exception, string approvalId);
}
