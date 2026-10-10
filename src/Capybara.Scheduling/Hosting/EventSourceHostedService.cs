using Capybara.Scheduling.Triggers;
using Microsoft.Extensions.Hosting;

namespace Capybara.Scheduling.Hosting;

/// <summary>
/// Starts every registered <see cref="IEventTriggerSource"/> (file watchers, mail/calendar/GitHub adapters) and connects
/// it to the <see cref="EventRouter"/> (FR-RTN-01). With no sources registered it does nothing.
/// </summary>
public sealed class EventSourceHostedService(IEnumerable<IEventTriggerSource> sources, EventRouter router) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            router.Attach(source);
            await source.StartAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            router.Detach(source);
        }

        return Task.CompletedTask;
    }
}
