using Capybara.Hosting.Agents;

namespace Capybara.Hosting.Tests;

internal sealed class FakeCommandRunner : ICommandRunner
{
    public List<CommandRunRequest> Requests { get; } = [];

    public CommandRunResult NextResult { get; set; } = new(0, "active", string.Empty);

    public ValueTask<CommandRunResult> RunAsync(CommandRunRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return ValueTask.FromResult(NextResult);
    }
}
