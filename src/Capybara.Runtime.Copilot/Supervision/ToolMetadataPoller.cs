namespace Capybara.Runtime.Copilot.Supervision;

/// <summary>
/// The runtime reports a null tool list until its tools have initialized, so the self-test (FR-AGT-09) polls until
/// the list exists. Running out of time is a failure, never a pass: the caller's <see cref="SelfTestGuard"/> pauses
/// the agent.
/// </summary>
internal static class ToolMetadataPoller
{
    /// <exception cref="TimeoutException">No tool list within <paramref name="timeout"/>.</exception>
    public static async Task<IReadOnlyCollection<string>> WaitForToolsAsync(
        Func<CancellationToken, Task<IReadOnlyCollection<string>?>> probe,
        TimeSpan timeout,
        TimeSpan interval,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var deadline = time.GetUtcNow() + timeout;
        while (true)
        {
            if (await probe(cancellationToken) is { } tools)
            {
                return tools;
            }

            if (time.GetUtcNow() >= deadline)
            {
                throw new TimeoutException($"The runtime did not report its tools within {timeout}.");
            }

            await Task.Delay(interval, time, cancellationToken);
        }
    }
}
