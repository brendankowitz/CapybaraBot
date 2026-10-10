using Capybara.Memory.Abstractions;
using Capybara.Memory.Access;
using Capybara.Policy.Provenance;
using Capybara.Runtime.Copilot.Sessions;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Hosting;

/// <summary>
/// Memory around a turn (FR-MEM-02, FR-MEM-10, FR-MEM-11, FR-MEM-12): before a main or task turn the ranked memory
/// index is prepended as a delimited data block; after it, the turn text is offered to conversational memory providers.
/// Consult turns get neither, and incognito turns never touch memory at all.
/// </summary>
/// <remarks>
/// Both steps fail open for the turn: an index that cannot be built, or text a provider refuses, is logged and the turn
/// proceeds, because memory is an aid, never a gate.
/// </remarks>
public sealed partial class TurnMemory(
    IMemoryStore memory,
    TaintTracker taint,
    TimeProvider time,
    ILogger<TurnMemory> logger,
    CompositeMemoryStore? providers = null)
{
    /// <summary>The prompt the model sees: the index block, a blank line, then <paramref name="prompt"/>.</summary>
    public async Task<string> PrepareAsync(CapybaraSession session, string prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!UsesMemory(session))
        {
            return prompt;
        }

        try
        {
            var index = await memory.BuildIndexAsync(SessionMemory.ContextFor(session, taint), cancellationToken);
            return $"{index}\n\n{prompt}";
        }
#pragma warning disable CA1031 // The turn runs without an index rather than not at all.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogIndexFailed(logger, ex, session.SessionId);
            return prompt;
        }
    }

    /// <summary>
    /// Offers the turn's text (not the injected index) to memory providers, with the session's taint. Never for a dry
    /// run (FR-RTN-08): a test must leave no trace in memory.
    /// </summary>
    public async Task IngestAsync(CapybaraSession session, string prompt, string? reply, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (providers is null || session.DryRun || !UsesMemory(session))
        {
            return;
        }

        var text = reply is null ? prompt : $"{prompt}\n\n{reply}";
        try
        {
            await providers.IngestAsync(new MemoryIngestItem(text, session.Agent.Id.Value, session.SessionId, taint.Of(session.SessionId), time.GetUtcNow()), cancellationToken);
        }
        catch (MemoryAccessDeniedException ex)
        {
            LogIngestRefused(logger, session.SessionId, ex.Message);
        }
#pragma warning disable CA1031 // A provider outage must not fail a turn that already happened.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogIngestFailed(logger, ex, session.SessionId);
        }
    }

    private static bool UsesMemory(CapybaraSession session) => session.Kind is SessionKind.Main or SessionKind.Task;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not build the memory index for {SessionId}; the turn runs without it")]
    private static partial void LogIndexFailed(ILogger logger, Exception exception, string sessionId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Memory providers refused the turn text of {SessionId}: {Reason}")]
    private static partial void LogIngestRefused(ILogger logger, string sessionId, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Offering the turn text of {SessionId} to memory providers failed")]
    private static partial void LogIngestFailed(ILogger logger, Exception exception, string sessionId);
}
