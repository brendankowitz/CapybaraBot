namespace Fleet.Orchestration.Configuration;

#pragma warning disable CA1812, CA2227, CA1002 // Instantiated and populated by YamlDotNet.

internal sealed class AgentYaml
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string? Job { get; set; }

    public string? Description { get; set; }

    public string? Model { get; set; }

    public List<string> Tools { get; set; } = [];

    public List<string> ExternalActionTools { get; set; } = [];

    public List<string> ApprovalRequiredTools { get; set; } = [];

    public TrifectaYaml Trifecta { get; set; } = new();

    public PeersYaml Peers { get; set; } = new();

    public AcceptsYaml Accepts { get; set; } = new();

    public List<GrantYaml> Grants { get; set; } = [];

    public List<string> Egress { get; set; } = [];

    public BudgetYaml? Budget { get; set; }

    public MemoryYaml Memory { get; set; } = new();

    public bool Hidden { get; set; }

    public string? Host { get; set; }

    public ProviderYaml? Provider { get; set; }

    public List<McpServerYaml> McpServers { get; set; } = [];

    public bool RemoteSessions { get; set; }

    public bool? Notifications { get; set; }
}

internal sealed class ProviderYaml
{
    public string? Type { get; set; }

    public string? BaseUrl { get; set; }

    public string? WireApi { get; set; }

    public string? ApiKeySecret { get; set; }

    public string? Model { get; set; }

    public string? AzureApiVersion { get; set; }
}

internal sealed class McpServerYaml
{
    public string? Name { get; set; }

    public string? Type { get; set; }

    public string? Command { get; set; }

    public List<string> Args { get; set; } = [];

    public string? Url { get; set; }

    public List<string> Tools { get; set; } = [];

    public string? Sha256 { get; set; }

    public bool? Untrusted { get; set; }

    public string? TokenSecret { get; set; }

    public int? Timeout { get; set; }
}

internal sealed class TrifectaYaml
{
    public bool UntrustedInput { get; set; }

    public bool PrivateData { get; set; }

    public bool ExternalAction { get; set; }
}

internal sealed class PeersYaml
{
    public List<string> Consult { get; set; } = [];

    public List<string> Message { get; set; } = [];

    public List<string> Task { get; set; } = [];
}

internal sealed class AcceptsYaml
{
    public bool Consults { get; set; }

    public bool Messages { get; set; }

    public bool Tasks { get; set; }
}

internal sealed class GrantYaml
{
    public string? Connection { get; set; }

    public List<string> Operations { get; set; } = [];

    public Dictionary<string, List<string>> Constraints { get; set; } = [];
}

internal sealed class BudgetYaml
{
    public decimal? DailyCredits { get; set; }

    public decimal? MonthlyCredits { get; set; }

    public decimal? SessionMaxCredits { get; set; }
}

internal sealed class MemoryYaml
{
    public bool ReadUser { get; set; }
}

internal sealed class RuleYaml
{
    public string? Effect { get; set; }

    public string? Agent { get; set; }

    public string? Tool { get; set; }

    public string? Argument { get; set; }

    public string? Pattern { get; set; }

    public List<string> DestinationAllowlist { get; set; } = [];

    public string? Reason { get; set; }
}

internal sealed class RoutineYaml
{
    public string? Id { get; set; }

    public string? Owner { get; set; }

    public string? Kind { get; set; }

    public string? Every { get; set; }

    public string? Cron { get; set; }

    public string? ActiveHours { get; set; }

    public string? Event { get; set; }

    public string? Prompt { get; set; }

    public string? Skill { get; set; }

    public string? Input { get; set; }

    public string? Output { get; set; }

    public string? Deliver { get; set; }

    public List<string> ApprovalBoundary { get; set; } = [];

    public string? OnMissingInput { get; set; }

    public decimal? BudgetCredits { get; set; }

    public string? Model { get; set; }

    public bool? Enabled { get; set; }

    public bool DryRun { get; set; }
}

#pragma warning restore CA1812, CA2227, CA1002
