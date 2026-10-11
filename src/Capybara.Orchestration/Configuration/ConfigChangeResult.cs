namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Outcome of <see cref="CapybaraConfigurationHost.ApplyAsync"/>. When <see cref="Errors"/> is non-empty nothing was
/// written. <see cref="VersioningError"/> is set when the change was applied and is live, but the git commit failed;
/// the next successful commit picks the change up.
/// </summary>
public sealed record ConfigChangeResult(IReadOnlyList<string> Errors, string? VersioningError = null)
{
    public bool Applied => Errors.Count == 0;
}
