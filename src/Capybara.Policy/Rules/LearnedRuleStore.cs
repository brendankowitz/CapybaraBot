using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capybara.Policy.Rules;

/// <summary>Persists rules created by "Always allow" approvals (FR-SAF-04) as JSON under the state root.</summary>
public sealed class LearnedRuleStore(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();

    public IReadOnlyList<PolicyRule> Load()
    {
        if (!File.Exists(path))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<PolicyRule>>(File.ReadAllText(path), Json) ?? [];
    }

    /// <summary>
    /// Builds the engine for startup: configured rules plus the learned rules persisted by earlier runs, with every
    /// future "Always allow" saved back. Without this, learned rules are lost on restart.
    /// </summary>
    public RulesEngine CreateEngine(IEnumerable<PolicyRule> configured)
    {
        var engine = new RulesEngine(configured, Load());
        Attach(engine);
        return engine;
    }

    /// <summary>Saves the learned rules and subscribes to future additions.</summary>
    public void Attach(RulesEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);

        // Snapshot under the store lock so concurrent additions cannot write an older list over a newer one.
        engine.RuleLearned += (_, _) =>
        {
            lock (_gate)
            {
                Save(engine.LearnedRules);
            }
        };
    }

    public void Save(IReadOnlyList<PolicyRule> rules)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(rules, Json)));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
    }
}
