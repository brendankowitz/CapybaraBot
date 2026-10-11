using Capybara.Contracts.Agents;
using Capybara.Contracts.Routines;
using Capybara.Observability.Alerts;
using Capybara.Scheduling.Lifecycle;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.Hosting;

/// <summary>
/// What <see cref="RoutineConfigLoader.ApplyAsync"/> did with a routine configuration. <c>Applied</c> is false when
/// validation failed (see <c>Errors</c>); the previously loaded routines were then kept untouched. <c>ProjectedMonthlyCredits</c>
/// is the projected cost per runnable routine, to show the user before they enable anything (FR-RTN-09).
/// </summary>
public sealed record RoutineLoadResult(
    bool Applied,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Removed,
    IReadOnlyDictionary<string, decimal> ProjectedMonthlyCredits);

/// <summary>
/// Populates the <see cref="RoutineRegistry"/> from the routines declared in the user's config and resynchronizes the
/// scheduler (FR-RTN-04, FR-RTN-05). Registered as a singleton so the host can reach it, which is exactly why every call
/// must present a human <see cref="RoutinePrincipal"/>: call <see cref="ApplyAsync"/> only from the config-reload path
/// (startup, and <c>CapybaraConfigurationHost.Reloaded</c> after a human edit or an approved proposal) — never from an
/// agent-driven code path. Agents can only propose routine changes through FR-AGT-06.
/// </summary>
public sealed class RoutineConfigLoader(RoutineRegistry registry, ScheduleSynchronizer synchronizer, IAlertSink alerts, TimeProvider time) : IDisposable
{
    private readonly HashSet<string> _loaded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Validates <paramref name="routines"/> and, only if all are valid, makes them the registry's config-declared set:
    /// new routines are added, changed ones updated, and ones no longer declared removed. Routines the registry holds
    /// that did not come from config are left alone and count towards the per-agent limit. An invalid configuration is
    /// rejected as a whole and raises an alert.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="principal"/> is not human.</exception>
    public async Task<RoutineLoadResult> ApplyAsync(IEnumerable<RoutineSpec> routines, IReadOnlySet<AgentId> knownAgents, RoutinePrincipal principal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(routines);
        ArgumentNullException.ThrowIfNull(knownAgents);
        ArgumentNullException.ThrowIfNull(principal);
        principal.RequireHuman();
        var declared = routines.ToList();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var errors = RoutineValidator.Validate(declared, knownAgents, registry.TimeZone).ToList();
            var declaredIds = declared.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var kept = registry.Routines.Where(r => !_loaded.Contains(r.Id) && !declaredIds.Contains(r.Id));
            foreach (var group in declared.Concat(kept).GroupBy(r => r.Owner).Where(g => g.Count() > RoutineLimits.MaxRoutinesPerAgent && g.Count(r => declaredIds.Contains(r.Id)) <= RoutineLimits.MaxRoutinesPerAgent))
            {
                errors.Add($"{group.Key} would have {group.Count()} routines including ones not loaded from config; the limit is {RoutineLimits.MaxRoutinesPerAgent}.");
            }

            if (errors.Count > 0)
            {
                await alerts.PublishAsync(
                    new Alert("routine.config-rejected", AlertSeverity.Critical, $"Routine configuration rejected ({errors.Count} error(s)); keeping the previous routines. First error: {errors[0]}", time.GetUtcNow()),
                    cancellationToken);
                return new RoutineLoadResult(false, errors, [], [], [], new Dictionary<string, decimal>());
            }

            // Removals first, so swapping routines of an agent at the 50-routine limit never trips the registry's own limit.
            var removed = _loaded.Where(id => !declaredIds.Contains(id)).ToList();
            foreach (var id in removed)
            {
                registry.Remove(id, principal);
            }

            var added = new List<string>();
            var updated = new List<string>();
            foreach (var routine in declared)
            {
                var existing = registry.Find(routine.Id);
                if (existing is null)
                {
                    added.Add(routine.Id);
                }
                else if (existing != routine)
                {
                    updated.Add(routine.Id);
                }
                else
                {
                    continue;
                }

                registry.AddOrUpdate(routine, principal);
            }

            _loaded.Clear();
            _loaded.UnionWith(declaredIds);

            // The registry's Changed event also resynchronizes in the background; doing it here as well makes the result
            // deterministic for the caller and is harmless because synchronization is idempotent.
            await synchronizer.SynchronizeAsync(cancellationToken);

            var projections = declared.Where(r => registry.IsRunnable(r)).ToDictionary(r => r.Id, registry.ProjectedMonthlyCredits, StringComparer.Ordinal);
            return new RoutineLoadResult(true, [], added, updated, removed, projections);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
