using Capybara.Client;
using Microsoft.AspNetCore.Components;

namespace Capybara.Pwa.Components;

/// <summary>
/// Base for pages that call the gateway: API failures become a visible <see cref="Error"/> instead of an unhandled
/// exception, and a missing item is distinguishable from a failed route.
/// </summary>
public abstract class CapybaraPage : ComponentBase
{
    [Inject]
    protected CapybaraApiClient Api { get; set; } = default!;

    protected string? Error { get; private set; }

    protected void ClearError() => Error = null;

    /// <summary>Runs an API action; returns false and sets <see cref="Error"/> when the gateway refuses it. A success leaves an earlier error in place until <see cref="ClearError"/>.</summary>
    protected async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (HttpRequestException ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary>Runs an API read; returns <see langword="default"/> and sets <see cref="Error"/> on failure.</summary>
    protected async Task<T?> TryAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (HttpRequestException ex)
        {
            Error = ex.Message;
            return default;
        }
    }
}
