using System.Globalization;
using Fleet.Contracts.Agents;
using Fleet.Contracts.Connections;
using Fleet.Contracts.Routines;
using Fleet.Policy.Rules;
using Fleet.Policy.Trifecta;
using Fleet.Scheduling.Validation;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Fleet.Orchestration.Configuration;

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
public static class FleetConfigurationLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static FleetConfiguration Load(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var errors = new List<string>();
        var agents = LoadAgents(Path.Combine(root, "agents"), errors);
        var rules = LoadList<RuleYaml>(Path.Combine(root, "rules.yaml"), errors).Select(r => ToRule(r, errors)).OfType<PolicyRule>().ToList();
        var routines = LoadList<RoutineYaml>(Path.Combine(root, "routines.yaml"), errors).Select(r => ToRoutine(r, errors)).OfType<RoutineSpec>().ToList();

        var known = agents.Select(a => a.Id).ToHashSet();
        foreach (var agent in agents)
        {
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
            throw new FleetConfigurationException(errors);
        }

        return new FleetConfiguration(agents, rules, routines);
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
                    Connection = g.Connection ?? throw new FleetConfigurationException($"{id}: a grant is missing its connection."),
                    Operations = g.Operations,
                    Constraints = g.Constraints.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal),
                })],
                EgressDomains = yaml.Egress,
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
