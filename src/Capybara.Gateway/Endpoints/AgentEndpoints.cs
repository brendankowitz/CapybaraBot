using Capybara.Client;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Messaging;
using Capybara.Gateway.Chat;
using Capybara.Gateway.Realtime;
using Capybara.Gateway.Security;
using Capybara.Gateway.Wiring;
using Capybara.Observability.Cost;
using Capybara.Orchestration.Inbox;
using Capybara.Orchestration.Registry;
using Capybara.Policy.Audit;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Supervision;
using Capybara.Scheduling.Lifecycle;

namespace Capybara.Gateway.Endpoints;

/// <summary>Agents, the per-agent chat and runtime health (FR-CLI-02, FR-AGT-05, FR-AGT-08, FR-MEM-12).</summary>
public static class AgentEndpoints
{
    /// <summary>Longest prompt accepted from a client.</summary>
    public const int MaxPromptLength = 32_000;

    public const int MaxHistory = ChatHistory.RecentLines;

    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder api)
    {
        var group = api.MapGroup("/agents").WithTags("Agents");

        group.MapGet("/", (AgentRegistry registry, KillSwitch killSwitch, CostLedger ledger, AgentActivityTracker activity) =>
            TypedResults.Ok(registry.All
                .OrderBy(a => a.Id.Value, StringComparer.Ordinal)
                .Select(a =>
                {
                    var state = activity.Get(a.Id.Value);
                    return new AgentStatusDto(a.Id.Value, a.Name, a.Job, a.Model, killSwitch.IsBlocked(a.Id), ledger.GetStatus(a.Id).IsExhausted, a.Hidden, state.State.ToWire(), state.Unread);
                })
                .ToList()));

        // A human pause outlives a restart (journaled) and is audited (FR-SAF-06, FR-SAF-09).
        group.MapPost("/{id}/pause", async (string id, HttpContext context, AgentRegistry registry, KillSwitch killSwitch, PausedAgentsJournal journal, IAuditLog audit, CancellationToken cancellationToken) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return Results.NotFound();
            }

            // Paused in memory first: if the journal write fails, the agent is still paused now and the caller hears so.
            killSwitch.Pause(agent);
            journal.Add(agent);
            await audit.AppendAsync(new AuditRecord("agent_control", "paused", agent.Value, Detail: $"by {context.GetDevice().PrincipalName}"), cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/{id}/resume", async (string id, HttpContext context, AgentRegistry registry, KillSwitch killSwitch, PausedAgentsJournal journal, IAuditLog audit, CancellationToken cancellationToken) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return Results.NotFound();
            }

            // Journal first: a resume that could not be recorded must not take effect, or a restart would re-pause.
            journal.Remove(agent);
            killSwitch.Resume(agent);
            await audit.AppendAsync(new AuditRecord("agent_control", "resumed", agent.Value, Detail: $"by {context.GetDevice().PrincipalName}"), cancellationToken);
            return Results.NoContent();
        });

        group.MapPost("/{id}/read", async (string id, AgentRegistry registry, AgentActivityTracker activity, CancellationToken cancellationToken) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return Results.NotFound();
            }

            await activity.MarkReadAsync(agent.Value, cancellationToken);
            return Results.NoContent();
        });

        // A message the user typed on an authenticated device: the PWA/TUI entry point for MessageOrigin.User. It goes
        // through the agent's durable inbox like every other message (FR-AGT-05) and the reply arrives in the chat history.
        group.MapPost("/{id}/turn", async (
            string id,
            TurnRequestDto request,
            HttpContext context,
            AgentRegistry registry,
            IInboxStore inbox,
            ChatHistory chat,
            IncognitoSessionReaper incognito,
            RoutineAutoPauser autoPauser,
            TimeProvider time,
            CancellationToken cancellationToken) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > MaxPromptLength)
            {
                return Results.BadRequest($"prompt is required and at most {MaxPromptLength} characters");
            }

            string? session = null;
            string? oneShot = null;
            if (request.Incognito)
            {
                if (request.ConversationId is { } conversation)
                {
                    if (!IsConversationId(conversation))
                    {
                        return Results.BadRequest("conversationId is 1-80 letters, digits, '-' or '_'");
                    }

                    session = SessionIds.Incognito(agent, conversation);
                }
                else
                {
                    // No client can continue a conversation whose id it never saw: its session ends with this turn.
                    oneShot = Guid.NewGuid().ToString("N");
                    session = SessionIds.Incognito(agent, oneShot);
                    incognito.EndAfterTurn(agent, oneShot);
                }
            }

            var message = new FleetMessage
            {
                To = agent,
                Origin = MessageOrigin.User,
                Body = request.Prompt,
                SessionId = session,
                Taint = Taint.Clean,
                CreatedAt = time.GetUtcNow(),
            };
            try
            {
                await inbox.EnqueueAsync(message, cancellationToken);
            }
            catch when (oneShot is not null)
            {
                incognito.Cancel(agent, oneShot);
                throw;
            }

            chat.RecordUser(agent, message.Id, message.Body, request.Incognito, message.CreatedAt);

            // Talking to an agent keeps its routines from auto-pausing (FR-RTN-06); only a user request may say so.
            autoPauser.RecordUserInteraction(agent, RoutinePrincipal.Human(context.GetDevice().PrincipalName));
            return Results.Ok(new TurnResultDto(TurnResultDto.AcceptedStatus, MessageId: message.Id));
        });

        group.MapPut("/{id}/model", async (string id, SetModelDto request, AgentRegistry registry, AgentRuntimePool runtimes, CancellationToken cancellationToken) =>
        {
            if (Resolve(registry, id) is not { } agent || runtimes.Find(agent) is not { } runtime)
            {
                return Results.NotFound();
            }

            if (!IsModelName(request.Model))
            {
                return Results.BadRequest("model is 1-100 letters, digits, '.', '-', '_', ':' or '/'");
            }

            await runtime.SetModelAsync(request.Model, cancellationToken);
            return Results.NoContent();
        });

        // Ends an incognito conversation: its CLI session (the transcript) is deleted once a turn in flight finishes (FR-MEM-12).
        group.MapDelete("/{id}/incognito/{conversationId}", (string id, string conversationId, AgentRegistry registry, IncognitoSessionReaper incognito) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return Results.NotFound();
            }

            if (!IsConversationId(conversationId))
            {
                return Results.BadRequest("conversationId is 1-80 letters, digits, '-' or '_'");
            }

            incognito.End(agent, conversationId);
            return Results.NoContent();
        });

        group.MapGet("/{id}/history", (string id, int? limit, AgentRegistry registry, ChatHistory chat) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return Results.NotFound();
            }

            return Results.Ok(chat.Read(agent, Math.Clamp(limit ?? 100, 1, MaxHistory)));
        });

        var runtimes = api.MapGroup("/runtimes").WithTags("Agents");

        runtimes.MapGet("/", (RuntimeSupervisor supervisor) =>
            TypedResults.Ok(supervisor.Health
                .Select(h => new RuntimeStatusDto(h.Agent, h.State.ToString(), h.NextAttemptAt is { } next ? $"{h.RecentCrashes} recent crashes; next attempt at {next:u}" : null))
                .ToList()));

        runtimes.MapPost("/{id}/unquarantine", (string id, AgentRegistry registry, RuntimeSupervisor supervisor) =>
        {
            if (Resolve(registry, id) is not { } agent)
            {
                return Results.NotFound();
            }

            supervisor.Unquarantine(agent.Value);
            return Results.NoContent();
        });

        return api;
    }

    internal static AgentId? Resolve(AgentRegistry registry, string id) =>
        AgentId.TryParse(id, out var agent) && registry.Find(agent) is not null ? agent : null;

    private static bool IsConversationId(string value) =>
        value.Length is > 0 and <= 80 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool IsModelName(string? value) =>
        value is { Length: > 0 and <= 100 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or ':' or '/');
}
