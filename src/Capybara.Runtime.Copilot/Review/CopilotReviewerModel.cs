using Capybara.Contracts.Agents;
using Capybara.Runtime.Copilot.Configuration;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Runtime.Copilot.Sessions;
using GitHub.Copilot;
using Microsoft.Extensions.Logging;

namespace Capybara.Runtime.Copilot.Review;

/// <summary>
/// Runs each review in a fresh, tool-less session on the cheap reviewer model (FR-SAF-05), in a dedicated CLI process
/// that shares nothing with any agent: its own state directory, no MCP servers, skills, memory or remote session. The
/// session is deleted after the review, so no review can see another.
/// </summary>
/// <remarks>
/// The CLI starts on the first review. The session factory is resolved lazily because the policy gate, which uses the
/// reviewer, is itself a dependency of the factory's handlers. Thread-safe.
/// </remarks>
public sealed partial class CopilotReviewerModel(
    Lazy<SessionConfigFactory> configs,
    CopilotRuntimeOptions options,
    ILogger<CopilotReviewerModel> logger) : IReviewerModel, IAsyncDisposable
{
    private readonly SemaphoreSlim _start = new(1, 1);
    private CopilotClient? _client;

    public async Task<string?> CompleteAsync(AgentId reviewed, string prompt, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        var client = await ClientAsync(cancellationToken);
        var sessionId = $"reviewer_{Guid.NewGuid():N}";
        var session = await client.CreateSessionAsync(configs.Value.CreateReviewerConfig(sessionId, options.ReviewerModel, reviewed), cancellationToken);
        try
        {
            var reply = await session.SendAndWaitAsync(new MessageOptions { Prompt = prompt, Source = MessageSource.System }, options.ReviewerTimeout, cancellationToken);
            return reply?.Data?.Content;
        }
        finally
        {
            await session.DisposeAsync();
            try
            {
                await client.DeleteSessionAsync(sessionId, CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                LogDeleteFailed(logger, sessionId, ex.Message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is { } client)
        {
            await client.DisposeAsync();
            _client = null;
        }

        _start.Dispose();
    }

    private async Task<CopilotClient> ClientAsync(CancellationToken cancellationToken)
    {
        if (_client is { } started)
        {
            return started;
        }

        await _start.WaitAsync(cancellationToken);
        try
        {
            if (_client is null)
            {
                Directory.CreateDirectory(options.ReviewerDirectory);
                var client = new CopilotClient(CopilotClientOptionsFactory.CreateForReviewer(options, logger));
                await client.StartAsync(cancellationToken);
                _client = client;
            }

            return _client;
        }
        finally
        {
            _start.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Reviewer session {SessionId} was not deleted: {Reason}")]
    private static partial void LogDeleteFailed(ILogger logger, string sessionId, string reason);
}
