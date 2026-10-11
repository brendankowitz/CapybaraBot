using Capybara.Orchestration.Registry;
using Capybara.Policy.Safety;
using Capybara.Runtime.Copilot.Hosting;
using Capybara.Testing;

namespace Capybara.Runtime.Copilot.Tests;

/// <summary>FR-AGT-07: a removed agent's runtime leaves the pool and the supervisor's view, and is disposed once.</summary>
public sealed class AgentRuntimePoolTests
{
    [Fact]
    public async Task Removing_a_runtime_disposes_it_and_hides_it_from_supervision()
    {
        using var killSwitch = new KillSwitch(TimeProvider.System);
        var ea = TestAgents.Create("ea");
        var code = TestAgents.Create("code");
        var disposed = new List<string>();
        await using var pool = new AgentRuntimePool(new AgentRegistry([ea, code], killSwitch), a => new DisposalRecorder(a.Id, disposed));
        var supervised = pool.Supervised;

        Assert.True(await pool.RemoveAsync(ea.Id));
        Assert.False(await pool.RemoveAsync(ea.Id));

        Assert.Null(pool.Find(ea.Id));
        Assert.Equal(["ea"], disposed);
        Assert.Equal([code.Id], pool.All.Select(r => r.Agent));

        // The supervisor holds this enumerable from construction on; it must reflect the removal.
        Assert.Equal([code.Id], supervised.Select(r => r.Agent));
    }

    private sealed class DisposalRecorder(Contracts.Agents.AgentId agent, List<string> disposed) : IAgentRuntime, Supervision.ISupervisedRuntime
    {
        private readonly FakeAgentRuntime _inner = new(agent);

        public Contracts.Agents.AgentId Agent => agent;

        public Contracts.Agents.AgentDefinition Definition => _inner.Definition;

        public Task<DefinitionChange> UpdateDefinitionAsync(Contracts.Agents.AgentDefinition definition, CancellationToken cancellationToken) => _inner.UpdateDefinitionAsync(definition, cancellationToken);

        public Task<TurnResult> RunTurnAsync(AgentTurnRequest request, CancellationToken cancellationToken) => _inner.RunTurnAsync(request, cancellationToken);

        public Task<TurnResult?> SteerAsync(string prompt, Contracts.Messaging.MessageOrigin origin, CancellationToken cancellationToken) => _inner.SteerAsync(prompt, origin, cancellationToken);

        public Task SetModelAsync(string model, CancellationToken cancellationToken) => _inner.SetModelAsync(model, cancellationToken);

        public Task EndSessionAsync(TurnTarget target, CancellationToken cancellationToken) => _inner.EndSessionAsync(target, cancellationToken);

        public Task<IReadOnlyList<IncognitoSessionInfo>> ListIncognitoSessionsAsync(CancellationToken cancellationToken) => _inner.ListIncognitoSessionsAsync(cancellationToken);

        public Task<Orchestration.Coordination.ConsultReply> ConsultAsync(string sessionId, string question, Contracts.Messaging.Envelope envelope, CancellationToken cancellationToken) =>
            _inner.ConsultAsync(sessionId, question, envelope, cancellationToken);

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> PingAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task RestartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<SelfTestResult> SelfTestAsync(CancellationToken cancellationToken) => Task.FromResult(new SelfTestResult(true, [], []));

        public ValueTask DisposeAsync()
        {
            disposed.Add(agent.Value);
            return ValueTask.CompletedTask;
        }
    }
}
