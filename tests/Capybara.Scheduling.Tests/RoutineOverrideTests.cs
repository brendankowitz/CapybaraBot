using Capybara.Scheduling.Lifecycle;
using Capybara.Testing;

namespace Capybara.Scheduling.Tests;

/// <summary>
/// FR-RTN-04/09: the user's enable and disable decisions survive a restart, but never outlive an edit of the config
/// file's own flag, and an enable never covers a definition that projects more than the cost it acknowledged.
/// </summary>
public sealed class RoutineOverrideTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    private string Db => Path.Combine(_dir.Path, "scheduling.sqlite");

    public void Dispose() => _dir.Dispose();

    private RoutineRegistry Restart(params Contracts.Routines.RoutineSpec[] routines)
    {
        var registry = new RoutineRegistry(sqlitePath: Db);
        foreach (var routine in routines)
        {
            registry.AddOrUpdate(routine, Kit.User);
        }

        return registry;
    }

    [Fact]
    public void A_routine_disabled_from_a_client_stays_disabled_after_a_restart()
    {
        var before = Restart(Kit.Heartbeat());
        before.Disable("hb", Kit.User);

        var after = Restart(Kit.Heartbeat());

        Assert.False(after.GetState("hb").Enabled);
    }

    [Fact]
    public void A_routine_enabled_from_a_client_stays_enabled_after_a_restart()
    {
        var routine = Kit.Heartbeat() with { Enabled = false };
        var before = Restart(routine);
        before.Enable("hb", before.ProjectedMonthlyCredits(routine), Kit.User);

        var after = Restart(routine);

        Assert.True(after.GetState("hb").Runnable);
        Assert.Equal(after.ProjectedMonthlyCredits(routine), after.GetState("hb").AcknowledgedMonthlyCredits);
    }

    [Fact]
    public void Editing_the_config_files_enabled_flag_since_the_decision_wins()
    {
        var before = Restart(Kit.Heartbeat());
        before.Disable("hb", Kit.User);

        // The user flips the flag in routines.yaml off and on again while capybarad is down: the file is the newer word.
        Restart(Kit.Heartbeat() with { Enabled = false });
        var after = Restart(Kit.Heartbeat());

        Assert.True(after.GetState("hb").Enabled);
    }

    [Fact]
    public void An_enable_does_not_cover_a_definition_that_now_projects_more()
    {
        var routine = Kit.Heartbeat() with { Enabled = false };
        var before = Restart(routine);
        before.Enable("hb", before.ProjectedMonthlyCredits(routine), Kit.User);

        var dearer = routine with { Interval = TimeSpan.FromMinutes(5) };
        var after = Restart(dearer);

        Assert.False(after.GetState("hb").Enabled);
        Assert.Contains("re-enable", after.GetState("hb").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_running_routine_whose_edit_projects_more_stops_until_re_enabled()
    {
        var routine = Kit.Heartbeat() with { Enabled = false };
        var registry = Restart(routine);
        registry.Enable("hb", registry.ProjectedMonthlyCredits(routine), Kit.User);

        registry.AddOrUpdate(routine with { Prompt = "reworded" }, Kit.User);
        Assert.True(registry.GetState("hb").Enabled);

        registry.AddOrUpdate(routine with { Interval = TimeSpan.FromMinutes(5) }, Kit.User);
        Assert.False(registry.GetState("hb").Enabled);
        Assert.False(Restart(routine with { Interval = TimeSpan.FromMinutes(5) }).GetState("hb").Enabled);
    }

    [Fact]
    public void A_removed_routine_forgets_the_decision_so_a_new_one_with_its_id_starts_from_config()
    {
        var before = Restart(Kit.Heartbeat());
        before.Disable("hb", Kit.User);
        before.Remove("hb", Kit.User);

        var after = Restart(Kit.Heartbeat());

        Assert.True(after.GetState("hb").Enabled);
    }
}
