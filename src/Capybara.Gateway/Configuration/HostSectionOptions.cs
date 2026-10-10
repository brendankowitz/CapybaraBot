namespace Capybara.Gateway.Configuration;

/// <summary><c>Capybara:Telemetry</c>: OTLP export for capybarad's spans and metrics and the agents' CLIs (FR-OBS-01, FR-OBS-04).</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Capybara:Telemetry";

    /// <summary>
    /// Local collector base URL, e.g. the Aspire dashboard or Grafana LGTM (<c>http://127.0.0.1:4318</c>). Unset turns
    /// export off; spans are still created in-process. Message content is never captured.
    /// </summary>
    public Uri? OtlpEndpoint { get; set; }

    /// <summary><c>http/protobuf</c> (default) or <c>grpc</c>.</summary>
    public string OtlpProtocol { get; set; } = "http/protobuf";
}

/// <summary><c>Capybara:Backup:InProcess</c> and the backup sources shared with <c>capybarad backup</c> (FR-HST-05).</summary>
public sealed class BackupHostOptions
{
    public const string SectionName = "Capybara:Backup";

    /// <summary>
    /// Run the nightly backup inside capybarad. Set to <see langword="false"/> when <c>capybara-backup.timer</c> runs
    /// <c>capybarad backup</c> instead, so the two never both run.
    /// </summary>
    public bool InProcess { get; set; } = true;
}

/// <summary><c>Capybara:Broker</c>: how capybarad reaches capybara-broker (FR-CON-01).</summary>
public sealed class BrokerHostOptions
{
    public const string SectionName = "Capybara:Broker";

    public Uri BaseAddress { get; set; } = new("http://127.0.0.1:8788");

    /// <summary>Unix socket instead of TCP, matching the broker's <c>Broker:UnixSocketPath</c>.</summary>
    public string? UnixSocketPath { get; set; }

    /// <summary>systemd credential holding the shared broker API key.</summary>
    public string ApiKeyCredential { get; set; } = "broker-api-key";

    /// <summary>Development and test override for the shared key; production reads <see cref="ApiKeyCredential"/>.</summary>
    public string? ApiKey { get; set; }

    public TimeSpan TokenExpiryLeadTime { get; set; } = TimeSpan.FromDays(3);
}

/// <summary><c>Capybara:Quota</c>: how often the plan quota is read (FR-OBS-02).</summary>
public sealed class QuotaHostOptions
{
    public const string SectionName = "Capybara:Quota";

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary><c>Capybara:Scheduling</c> overrides; the database lives under the state root (FR-RTN-05).</summary>
public sealed class SchedulingHostOptions
{
    public const string SectionName = "Capybara:Scheduling";

    /// <summary>IANA or Windows time zone id for cron and active hours; unset uses the host's local zone.</summary>
    public string? TimeZone { get; set; }

    public int ConcurrencyCap { get; set; } = 2;

    public int AutoPauseDays { get; set; } = 14;

    /// <summary><c>Skip</c> (default) or <c>FireOnceNow</c>.</summary>
    public string CatchUpPolicy { get; set; } = "Skip";
}
