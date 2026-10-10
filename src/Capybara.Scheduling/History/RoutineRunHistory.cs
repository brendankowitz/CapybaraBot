using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Capybara.Observability.Alerts;
using Capybara.Scheduling.Durable;
using Capybara.Scheduling.Validation;

namespace Capybara.Scheduling.History;

/// <summary>
/// Keeps the last 20 runs per routine and raises a visible alert for every run that did not succeed —
/// nothing fails silently (FR-RTN-05). Pass <c>sqlitePath</c> to make the history durable across restarts
/// (the file is shared with the Quartz store); without it the history lives in memory, which is for tests only.
/// </summary>
public sealed class RoutineRunHistory
{
    private readonly IAlertSink _alerts;
    private readonly string? _sqlitePath;
    private readonly Lock _writeGate = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<RoutineRun>> _memory = new(StringComparer.Ordinal);

    public RoutineRunHistory(IAlertSink alerts, string? sqlitePath = null)
    {
        ArgumentNullException.ThrowIfNull(alerts);
        _alerts = alerts;
        _sqlitePath = sqlitePath;
        if (sqlitePath is not null)
        {
            SchedulingDatabase.Initialize(sqlitePath);
        }
    }

    public async ValueTask RecordAsync(RoutineRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (_sqlitePath is null)
        {
            RecordInMemory(run);
        }
        else
        {
            RecordDurably(run);
        }

        if (run.Outcome != RoutineRunOutcome.Succeeded)
        {
            await _alerts.PublishAsync(
                new Alert(
                    $"routine.{run.Outcome.ToString().ToLowerInvariant()}",
                    run.Outcome == RoutineRunOutcome.Failed ? AlertSeverity.Critical : AlertSeverity.Warning,
                    $"Routine '{run.RoutineId}' {run.Outcome}: {run.Summary ?? "no detail"}",
                    run.StartedAt),
                cancellationToken);
        }
    }

    /// <summary>The routine's last <see cref="RoutineLimits.RunHistoryDepth"/> runs, oldest first.</summary>
    public IReadOnlyList<RoutineRun> Get(string routineId)
    {
        if (_sqlitePath is null)
        {
            return _memory.TryGetValue(routineId, out var queue) ? [.. queue] : [];
        }

        using var connection = SchedulingDatabase.Open(_sqlitePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT routine_id, started_at, outcome, summary, credits, evidence_json FROM capybara_routine_runs WHERE routine_id = $id ORDER BY id";
        command.Parameters.AddWithValue("$id", routineId);
        var runs = new List<RoutineRun>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            runs.Add(new RoutineRun(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                Enum.Parse<RoutineRunOutcome>(reader.GetString(2)),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                decimal.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<string[]>(reader.GetString(5))));
        }

        return runs;
    }

    private void RecordInMemory(RoutineRun run)
    {
        var queue = _memory.GetOrAdd(run.RoutineId, static _ => new ConcurrentQueue<RoutineRun>());
        queue.Enqueue(run);
        while (queue.Count > RoutineLimits.RunHistoryDepth && queue.TryDequeue(out _))
        {
        }
    }

    private void RecordDurably(RoutineRun run)
    {
        lock (_writeGate)
        {
            using var connection = SchedulingDatabase.Open(_sqlitePath!);
            using var transaction = connection.BeginTransaction();
            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO capybara_routine_runs (routine_id, started_at, outcome, summary, credits, evidence_json) VALUES ($id, $at, $outcome, $summary, $credits, $evidence)";
                insert.Parameters.AddWithValue("$id", run.RoutineId);
                insert.Parameters.AddWithValue("$at", run.StartedAt.ToString("O", CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$outcome", run.Outcome.ToString());
                insert.Parameters.AddWithValue("$summary", (object?)run.Summary ?? DBNull.Value);
                insert.Parameters.AddWithValue("$credits", run.CreditsSpent.ToString(CultureInfo.InvariantCulture));
                insert.Parameters.AddWithValue("$evidence", run.Evidence is null ? DBNull.Value : JsonSerializer.Serialize(run.Evidence));
                insert.ExecuteNonQuery();
            }

            using (var trim = connection.CreateCommand())
            {
                trim.Transaction = transaction;
                trim.CommandText = "DELETE FROM capybara_routine_runs WHERE routine_id = $id AND id NOT IN (SELECT id FROM capybara_routine_runs WHERE routine_id = $id ORDER BY id DESC LIMIT $depth)";
                trim.Parameters.AddWithValue("$id", run.RoutineId);
                trim.Parameters.AddWithValue("$depth", RoutineLimits.RunHistoryDepth);
                trim.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }
}
