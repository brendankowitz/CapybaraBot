using Capybara.Hosting.Agents;
using Capybara.Hosting.Backup;
using Microsoft.Extensions.Configuration;

namespace Capybara.Hosting.DependencyInjection;

/// <summary>Options for registering Capybara.Hosting services and host placement (FR-HST-01, NFR-09).</summary>
public sealed class HostingOptions
{
    /// <summary>Configuration section read by <see cref="FromConfiguration"/>.</summary>
    public const string SectionName = "Capybara:Hosting";

    private readonly string? _stateDatabasePath;
    private readonly string? _blobRoot;

    /// <summary>Root of capybarad's state; defaults to <c>/var/lib/capybara</c>. Relative defaults would depend on the service's working directory, which is read-only under systemd.</summary>
    public string StateRoot { get; init; } = "/var/lib/capybara";

    /// <summary>SQLite state store; defaults to <c>{StateRoot}/capybara-state.db</c>.</summary>
    public string StateDatabasePath
    {
        get => _stateDatabasePath ?? DefaultStateDatabasePath(StateRoot);
        init => _stateDatabasePath = value;
    }

    /// <summary>Blob store root holding backup archives and artifacts; defaults to <c>{StateRoot}/blobs</c>. Point it at <c>/var/backups/capybara/blobs</c> when backups run from <c>capybara-backup.service</c>.</summary>
    public string BlobRoot
    {
        get => _blobRoot ?? DefaultBlobRoot(StateRoot);
        init => _blobRoot = value;
    }

    public Uri? AcaBaseUri { get; init; }

    public string AcaApiVersion { get; init; } = "2026-10-01-preview";

    public Uri? RemoteBaseUri { get; init; }

    public AgentHostKind DefaultHost { get; init; } = AgentHostKind.Systemd;

    public IReadOnlyDictionary<string, AgentHostKind> AgentHosts { get; init; } = new Dictionary<string, AgentHostKind>(StringComparer.Ordinal);

    public BackupOptions? Backup { get; init; }

    /// <summary>Daily backup time; defaults to 02:00 UTC when <see cref="Backup"/> or the <c>Capybara:Backup</c> section enables backups.</summary>
    public BackupScheduleOptions? BackupSchedule { get; init; }

    /// <summary>
    /// Reads <c>StateRoot</c>, <c>StateDatabasePath</c> and <c>BlobRoot</c> from <c>Capybara:Hosting</c>. <c>StateRoot</c> falls back to
    /// <c>Capybara:StateRoot</c> (the gateway's own setting, so <c>Capybara__StateRoot</c> in the unit applies) and then to the default.
    /// Other properties keep their defaults.
    /// </summary>
    public static HostingOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var stateRoot = section["StateRoot"] ?? configuration["Capybara:StateRoot"] ?? new HostingOptions().StateRoot;
        return new HostingOptions
        {
            StateRoot = stateRoot,
            StateDatabasePath = section["StateDatabasePath"] ?? DefaultStateDatabasePath(stateRoot),
            BlobRoot = section["BlobRoot"] ?? DefaultBlobRoot(stateRoot),
        };
    }

    private static string DefaultStateDatabasePath(string stateRoot) => Path.Combine(stateRoot, "capybara-state.db");

    private static string DefaultBlobRoot(string stateRoot) => Path.Combine(stateRoot, "blobs");
}
