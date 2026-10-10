using Fleet.Contracts.Agents;

namespace Fleet.Channels;

/// <summary>Escalates A2A ask_user questions from the root agent to the phone channel (FR-A2A-07).</summary>
public interface IUserEscalation
{
    ValueTask<string?> AskAsync(string questionId, AgentId agent, string title, string body, TimeSpan timeout, CancellationToken cancellationToken = default);

    bool SubmitAnswer(string questionId, string answer);
}
