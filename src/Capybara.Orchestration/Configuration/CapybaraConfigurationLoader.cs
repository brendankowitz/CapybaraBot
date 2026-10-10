using System.Globalization;
using Capybara.Contracts.Agents;
using Capybara.Contracts.Connections;
using Capybara.Contracts.Routines;
using Capybara.Policy.Rules;
using Capybara.Policy.Trifecta;
using Capybara.Scheduling.Validation;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Loads the user-owned, git-versioned config directory (FR-AGT-01):
/// <code>
/// config/
///   agents/{id}/agent.yaml, SOUL.md, AGENTS.md
///   rules.yaml
///   routines.yaml
/// </code>
/// Every error is collected and startup fails closed.
/// </summary>
public static class CapybaraConfigurationLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static CapybaraConfiguration Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var errors = new List<string>();
        var agents = LoadAgents(Path.Combine(root, "agents"), errors);
        var rules = LoadList<RuleYaml>(Path.Combine(root, "rules.yaml"), errors).Select(r => ToRule(r, errors)).OfType<PolicyRule>().ToList();
        var routines = LoadList<RoutineYaml>(Path.Combine(root, "routines.yaml"), errors).Select(r => ToRoutine(r, errors)).OfType<RoutineSpec>().ToList();

        var known = agents.Select(a => a.Id).ToHashSet();
        foreach (var agent in agents)
        {
            if (!Hosts.Contains(agent.Host, StringComparer.Ordinal))
            {
                errors.Add($"{agent.Id}: host must be one of {string.Join(", ", Hosts)}.");
            }

            foreach (var tool in agent.Tools.Where(t => t.StartsWith("mcp:", StringComparison.Ordinal)))
            {
                if (!agent.McpServers.SelectMany(m => m.QualifiedTools()).Contains(tool, StringComparer.Ordinal))
                {
                    errors.Add($"{agent.Id}: tool '{tool}' does not name an enabled tool of a declared MCP server.");
                }
            }

            errors.AddRange(TrifectaValidator.Validate(agent));
            foreach (var peer in agent.Peers.Consult.Concat(agent.Peers.Message).Concat(agent.Peers.Task).Distinct(StringComparer.Ordinal))
            {
                if (!AgentId.TryParse(peer, out var peerId) || !known.Contains(peerId))
                {
                    errors.Add($"{agent.Id}: peer '{peer}' is not a declared agent.");
                }
                else if (peerId == agent.Id)
                {
                    errors.Add($"{agent.Id}: an agent cannot list itself as a peer.");
                }
            }
        }

        errors.AddRange(RoutineValidator.Validate(routines, known));

        if (errors.Count > 0)
        {
            throw new CapybaraConfigurationException(errors);
        }

        return new CapybaraConfiguration(agents, rules, routines);
    }

    private static List<AgentDefinition> LoadAgents(string agentsDir, List<string> errors)
    {
        var agents = new List<AgentDefinition>();
        if (!Directory.Exists(agentsDir))
        {
            errors.Add($"missing agents directory '{agentsDir}'.");
            return agents;
        }

        foreach (var dir in Directory.EnumerateDirectories(agentsDir).Order(StringComparer.Ordinal))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith('.') || folder.StartsWith('_'))
            {
                continue;
            }

            var file = Path.Combine(dir, "agent.yaml");
            if (!File.Exists(file))
            {
                errors.Add($"{folder}: missing agent.yaml.");
                continue;
            }

            AgentYaml? yaml;
            try
            {
                yaml = Yaml.Deserialize<AgentYaml?>(File.ReadAllText(file));
            }
            catch (YamlException ex)
            {
                errors.Add($"{folder}/agent.yaml: {ex.Message}");
                continue;
            }

            if (yaml is null)
            {
                errors.Add($"{folder}/agent.yaml is empty.");
                continue;
            }

            if (!AgentId.TryParse(yaml.Id, out var id))
            {
                errors.Add($"{folder}: invalid or missing id '{yaml.Id}'.");
                continue;
            }

            if (id.Value != folder)
            {
                errors.Add($"{folder}: id '{id}' must match its folder name.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(yaml.Name) || string.IsNullOrWhiteSpace(yaml.Job))
            {
                errors.Add($"{id}: name and job are required.");
                continue;
            }

            var budget = new AgentBudget();
            agents.Add(new AgentDefinition
            {
                Id = id,
                Name = yaml.Name,
                Job = yaml.Job,
                Description = yaml.Description?.Trim() ?? string.Empty,
                Model = string.IsNullOrWhiteSpace(yaml.Model) ? "auto" : yaml.Model,
                Soul = ReadOptional(Path.Combine(dir, "SOUL.md")),
                OperatingRules = ReadOptional(Path.Combine(dir, "AGENTS.md")),
                Tools = yaml.Tools,
                ExternalActionTools = yaml.ExternalActionTools,
                ApprovalRequiredTools = yaml.ApprovalRequiredTools,
                Trifecta = new TrifectaProfile
                {
                    UntrustedInput = yaml.Trifecta.UntrustedInput,
                    PrivateData = yaml.Trifecta.PrivateData,
                    ExternalAction = yaml.Trifecta.ExternalAction,
                },
                Peers = new PeerAllowlist { Consult = yaml.Peers.Consult, Message = yaml.Peers.Message, Task = yaml.Peers.Task },
                AcceptsConsults = yaml.Accepts.Consults,
                AcceptsMessages = yaml.Accepts.Messages,
                AcceptsTasks = yaml.Accepts.Tasks,
                ReadsUserMemory = yaml.Memory.ReadUser,
                Grants = [.. yaml.Grants.Select(g => new Grant
                {
                    Connection = g.Connection ?? throw new CapybaraConfigurationException($"{id}: a grant is missing its connection."),
                    Operations = g.Operations,
                    Constraints = g.Constraints.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal),
                })],
                EgressDomains = yaml.Egress,
                Hidden = yaml.Hidden,
                Host = string.IsNullOrWhiteSpace(yaml.Host) ? "systemd" : yaml.Host,
                Provider = ToProvider(id, yaml.Provider, errors),
                McpServers = [.. yaml.McpServers.Select(m => ToMcpServer(id, m, errors)).OfType<McpServerSpec>()],
                EnableRemoteSessions = yaml.RemoteSessions,
                Notifications = yaml.Notifications ?? true,
                Skills = SkillCardReader.Read(Path.Combine(dir, "skills")),
                Budget = new AgentBudget
                {
                    DailyCredits = yaml.Budget?.DailyCredits ?? budget.DailyCredits,
                    MonthlyCredits = yaml.Budget?.MonthlyCredits ?? budget.MonthlyCredits,
                    SessionMaxCredits = yaml.Budget?.SessionMaxCredits ?? budget.SessionMaxCredits,
                },
            });
        }

        return agents;
    }

    private static List<T> LoadList<T>(string file, List<string> errors)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return Yaml.Deserialize<List<T>?>(File.ReadAllText(file)) ?? [];
        }
        catch (YamlException ex)
        {
            errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            return [];
        }
    }

    private static PolicyRule? ToRule(RuleYaml yaml, List<string> errors)
    {
        if (!Enum.TryParse<RuleEffect>(yaml.Effect, ignoreCase: true, out var effect) || string.IsNullOrWhiteSpace(yaml.Tool))
        {
            errors.Add($"rules.yaml: rule for tool '{yaml.Tool}' needs a valid effect (allow, allowUnlessTainted, ask, deny) and a tool.");
            return null;
        }

        return new PolicyRule
        {
            Effect = effect,
            Agent = yaml.Agent ?? "*",
            Tool = yaml.Tool,
            Argument = yaml.Argument,
            ArgumentPattern = yaml.Pattern,
            DestinationAllowlist = yaml.DestinationAllowlist,
            Reason = yaml.Reason,
        };
    }

    private static RoutineSpec? ToRoutine(RoutineYaml yaml, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(yaml.Id) || !AgentId.TryParse(yaml.Owner, out var owner)
            || !Enum.TryParse<TriggerKind>(yaml.Kind, ignoreCase: true, out var kind))
        {
            errors.Add($"routines.yaml: routine '{yaml.Id}' needs an id, a valid owner and a kind (heartbeat, cron, event).");
            return null;
        }

        TimeSpan? interval = null;
        if (yaml.Every is not null)
        {
            if (!TryParseDuration(yaml.Every, out var parsed))
            {
                errors.Add($"{yaml.Id}: cannot parse interval '{yaml.Every}' (use e.g. 30m, 2h, 1d).");
                return null;
            }

            interval = parsed;
        }

        TimeOnly? from = null, to = null;
        if (yaml.ActiveHours is not null)
        {
            var parts = yaml.ActiveHours.Split('-', StringSplitOptions.TrimEntries);
            if (parts.Length != 2
                || !TimeOnly.TryParse(parts[0], CultureInfo.InvariantCulture, out var f)
                || !TimeOnly.TryParse(parts[1], CultureInfo.InvariantCulture, out var t))
            {
                errors.Add($"{yaml.Id}: activeHours must look like 07:00-21:00.");
                return null;
            }

            (from, to) = (f, t);
        }

        return new RoutineSpec
        {
            Id = yaml.Id,
            Owner = owner,
            Kind = kind,
            Interval = interval,
            Cron = string.IsNullOrWhiteSpace(yaml.Cron) ? null : yaml.Cron.Trim(),
            EventFilter = yaml.Event,
            ActiveFrom = from,
            ActiveTo = to,
            Prompt = yaml.Prompt ?? string.Empty,
            Skill = yaml.Skill,
            InputSource = yaml.Input,
            ExpectedOutput = yaml.Output,
            DeliveryTarget = yaml.Deliver ?? "push",
            ApprovalBoundary = yaml.ApprovalBoundary,
            OnMissingInput = Enum.TryParse<MissingInputBehavior>(yaml.OnMissingInput, ignoreCase: true, out var missing) ? missing : MissingInputBehavior.SkipAndAlert,
            BudgetCreditsPerRun = yaml.BudgetCredits ?? 1m,
            Model = yaml.Model ?? "auto",
            Enabled = yaml.Enabled ?? true,
            DryRun = yaml.DryRun,
        };
    }

    private static readonly string[] Hosts = ["systemd", "podman", "aca", "remote"];

    private static ModelProviderSpec? ToProvider(AgentId id, ProviderYaml? yaml, List<string> errors)
    {
        if (yaml is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(yaml.Type) || !Uri.TryCreate(yaml.BaseUrl, UriKind.Absolute, out var baseUrl))
        {
            errors.Add($"{id}: provider needs a type and an absolute baseUrl.");
            return null;
        }

        if (baseUrl.Scheme != Uri.UriSchemeHttps && !baseUrl.IsLoopback)
        {
            errors.Add($"{id}: provider baseUrl must use https unless it is loopback.");
            return null;
        }

        if (yaml.ApiKeySecret is not null && !SecretName.IsValid(yaml.ApiKeySecret))
        {
            errors.Add($"{id}: provider apiKeySecret '{yaml.ApiKeySecret}' is not a valid secret name.");
            return null;
        }

        return new ModelProviderSpec
        {
            Type = yaml.Type,
            BaseUrl = baseUrl.ToString(),
            WireApi = yaml.WireApi,
            ApiKeySecret = yaml.ApiKeySecret,
            ModelId = yaml.Model,
            AzureApiVersion = yaml.AzureApiVersion,
        };
    }

    /// <summary>FR-SKL-04: MCP servers are vendored and pinned; FR-CON-03: tools are enabled one by one.</summary>
    private static McpServerSpec? ToMcpServer(AgentId id, McpServerYaml yaml, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(yaml.Name) || !AgentId.TryParse(yaml.Name, out _))
        {
            errors.Add($"{id}: MCP server name '{yaml.Name}' must be lower-case letters, digits and dashes.");
            return null;
        }

        var prefix = $"{id}: MCP server '{yaml.Name}'";
        if (yaml.Tools.Count == 0)
        {
            errors.Add($"{prefix} must enable at least one tool explicitly.");
            return null;
        }

        switch (yaml.Type)
        {
            case "stdio":
                if (string.IsNullOrWhiteSpace(yaml.Command) || !Path.IsPathFullyQualified(yaml.Command))
                {
                    errors.Add($"{prefix} needs an absolute path to a vendored command (no package-manager installs).");
                    return null;
                }

                if (yaml.Sha256 is null || yaml.Sha256.Length != 64 || !yaml.Sha256.All(Uri.IsHexDigit))
                {
                    errors.Add($"{prefix} must be pinned with the sha256 of its command.");
                    return null;
                }

                if (yaml.ScriptSha256 is not null && (yaml.ScriptSha256.Length != 64 || !yaml.ScriptSha256.All(Uri.IsHexDigit)))
                {
                    errors.Add($"{prefix} scriptSha256 must be a sha256 hex digest.");
                    return null;
                }

                if (yaml.ScriptSha256 is null && !yaml.SelfContained)
                {
                    errors.Add($"{prefix} must pin the script it runs (scriptSha256) or declare selfContained: true.");
                    return null;
                }

                if (yaml.ScriptSha256 is not null && yaml.SelfContained)
                {
                    errors.Add($"{prefix} cannot both pin a script (scriptSha256) and declare selfContained: true.");
                    return null;
                }

                break;
            case "http":
                if (!Uri.TryCreate(yaml.Url, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback))
                {
                    errors.Add($"{prefix} needs an https or loopback url.");
                    return null;
                }

                break;
            default:
                errors.Add($"{prefix} type must be stdio or http.");
                return null;
        }

        if (yaml.TokenSecret is not null && !SecretName.IsValid(yaml.TokenSecret))
        {
            errors.Add($"{prefix} tokenSecret is not a valid secret name.");
            return null;
        }

        return new McpServerSpec
        {
            Name = yaml.Name,
            Type = yaml.Type,
            Command = yaml.Command,
            Args = yaml.Args,
            Url = yaml.Url,
            Tools = yaml.Tools,
            Sha256 = yaml.Sha256?.ToLowerInvariant(),
            ScriptSha256 = yaml.ScriptSha256?.ToLowerInvariant(),
            SelfContained = yaml.SelfContained,
            Untrusted = yaml.Untrusted ?? true,
            TokenSecret = yaml.TokenSecret,
            TimeoutSeconds = yaml.Timeout,
        };
    }

    internal static bool TryParseDuration(string value, out TimeSpan duration)
    {
        duration = default;
        if (value.Length < 2 || !double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount) || amount <= 0)
        {
            return false;
        }

        TimeSpan? parsed = value[^1] switch
        {
            'm' => TimeSpan.FromMinutes(amount),
            'h' => TimeSpan.FromHours(amount),
            'd' => TimeSpan.FromDays(amount),
            _ => null,
        };

        duration = parsed ?? default;
        return parsed is not null;
    }

    private static string ReadOptional(string path) => File.Exists(path) ? File.ReadAllText(path) : string.Empty;
}
