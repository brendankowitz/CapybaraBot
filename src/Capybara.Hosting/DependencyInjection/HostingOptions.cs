using Capybara.Hosting.Agents;
using Capybara.Hosting.Backup;

namespace Capybara.Hosting.DependencyInjection;

/// <summary>Options for registering Capybara.Hosting services and host placement (FR-HST-01, NFR-09).</summary>
public sealed class HostingOptions
{
    public string StateDatabasePath { get; init; } = "capybara-state.db";

    public string BlobRoot { get; init; } = "capybara-blobs";

    public Uri? AcaBaseUri { get; init; }

    public string AcaApiVersion { get; init; } = "2026-10-01-preview";

    public Uri? RemoteBaseUri { get; init; }

    public AgentHostKind DefaultHost { get; init; } = AgentHostKind.Systemd;

    public IReadOnlyDictionary<string, AgentHostKind> AgentHosts { get; init; } = new Dictionary<string, AgentHostKind>(StringComparer.Ordinal);

    public BackupOptions? Backup { get; init; }

    /// <summary>Daily backup time; defaults to 02:00 UTC when <see cref="Backup"/> or the <c>Capybara:Backup</c> section enables backups.</summary>
    public BackupScheduleOptions? BackupSchedule { get; init; }
}
