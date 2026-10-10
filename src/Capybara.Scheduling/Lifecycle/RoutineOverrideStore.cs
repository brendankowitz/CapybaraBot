using System.Globalization;
using Capybara.Scheduling.Durable;

namespace Capybara.Scheduling.Lifecycle;

/// <summary>
/// A routine the user enabled or disabled from a client (FR-RTN-04, FR-RTN-09). <see cref="SpecEnabled"/> is the config
/// file's own <c>enabled</c> flag when the decision was made: once the file's flag says something else, the user has
/// edited the config since, and the file wins. <see cref="AcknowledgedMonthlyCredits"/> is the projected cost the user
/// accepted when enabling; a definition that projects more no longer runs on that acknowledgement.
/// </summary>
internal sealed record RoutineOverride(bool Enabled, decimal? AcknowledgedMonthlyCredits, bool SpecEnabled);

/// <summary>Persists <see cref="RoutineOverride"/>s in the scheduling SQLite file, so a restart never reverts them.</summary>
/// <remarks>Not thread-safe; <see cref="RoutineRegistry"/> serializes every call under its own lock.</remarks>
internal sealed class RoutineOverrideStore
{
    private readonly string _path;

    public RoutineOverrideStore(string sqlitePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sqlitePath);
        _path = sqlitePath;
        SchedulingDatabase.Initialize(sqlitePath);
    }

    public Dictionary<string, RoutineOverride> Load()
    {
        var overrides = new Dictionary<string, RoutineOverride>(StringComparer.Ordinal);
        using var connection = SchedulingDatabase.Open(_path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT routine_id, enabled, acknowledged_credits, spec_enabled FROM capybara_routine_overrides";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            overrides[reader.GetString(0)] = new RoutineOverride(
                reader.GetInt64(1) != 0,
                reader.IsDBNull(2) ? null : decimal.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                reader.GetInt64(3) != 0);
        }

        return overrides;
    }

    public void Save(string routineId, RoutineOverride value)
    {
        using var connection = SchedulingDatabase.Open(_path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO capybara_routine_overrides (routine_id, enabled, acknowledged_credits, spec_enabled)
            VALUES ($id, $enabled, $ack, $spec)
            ON CONFLICT(routine_id) DO UPDATE SET enabled = excluded.enabled, acknowledged_credits = excluded.acknowledged_credits, spec_enabled = excluded.spec_enabled
            """;
        command.Parameters.AddWithValue("$id", routineId);
        command.Parameters.AddWithValue("$enabled", value.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$ack", value.AcknowledgedMonthlyCredits is { } ack ? ack.ToString(CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$spec", value.SpecEnabled ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public void Delete(string routineId)
    {
        using var connection = SchedulingDatabase.Open(_path);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM capybara_routine_overrides WHERE routine_id = $id";
        command.Parameters.AddWithValue("$id", routineId);
        command.ExecuteNonQuery();
    }
}
