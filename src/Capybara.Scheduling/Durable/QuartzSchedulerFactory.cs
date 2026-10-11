using System.Collections.Specialized;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Quartz;

namespace Capybara.Scheduling.Durable;

/// <summary>Creates a durable Quartz SQLite scheduler (FR-RTN-05).</summary>
public sealed class QuartzSchedulerFactory(Capybara.Scheduling.Dispatch.SchedulingOptions options)
{
    public const string TablePrefix = "QRTZ_";

    // Pooling is disabled so that closing a connection (and therefore scheduler shutdown) releases the file handle immediately
    // instead of leaving it open in the Microsoft.Data.Sqlite pool; scheduling volume is low enough that pooling has no benefit.
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = options.SQLitePath, Pooling = false }.ToString();

    public async Task<IScheduler> CreateSchedulerAsync(CancellationToken cancellationToken = default)
    {
        EnsureDatabase();
        var properties = new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = "CapybaraScheduling",
            ["quartz.scheduler.instanceId"] = "AUTO",
            ["quartz.serializer.type"] = "json",
            ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
            ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.SQLiteDelegate, Quartz",
            ["quartz.jobStore.dataSource"] = "default",
            ["quartz.jobStore.tablePrefix"] = TablePrefix,
            ["quartz.jobStore.useProperties"] = "true",
            ["quartz.jobStore.misfireThreshold"] = "1000",
            ["quartz.dataSource.default.provider"] = "SQLite-Microsoft",
            ["quartz.dataSource.default.connectionString"] = ConnectionString,
            ["quartz.threadPool.threadCount"] = Math.Max(2, options.ConcurrencyCap + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),

            // The misfire handler otherwise runs on a foreground thread, which keeps the process alive after Main
            // returns whenever a shutdown did not reach the job store. The host owns the lifetime: SchedulerHostedService
            // shuts the scheduler down on a graceful stop.
            ["quartz.jobStore.useBackgroundThreads"] = "true",
        };

        return await QuartzSchedulerBuilder.Create(_ => { }).UseProperties(properties).BuildScheduler(cancellationToken);
    }

    public void EnsureDatabase()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(options.SQLitePath));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        var schema = LoadSchema().Replace("\r\n", "\n", StringComparison.Ordinal);
        foreach (var statement in System.Text.RegularExpressions.Regex.Split(schema, @"^--;;$", System.Text.RegularExpressions.RegexOptions.Multiline).Select(x => x.Trim()))
        {
            if (string.IsNullOrWhiteSpace(statement))
            {
                continue;
            }

            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }

    private static string LoadSchema()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("tables_sqlite.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Quartz SQLite schema resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
