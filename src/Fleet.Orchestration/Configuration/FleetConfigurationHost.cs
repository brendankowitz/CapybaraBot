using Fleet.Orchestration.Registry;
using Fleet.Policy.Rules;

namespace Fleet.Orchestration.Configuration;

/// <summary>
/// Owns the live configuration. A reload happens only after a human action (approved proposal, lifecycle operation,
/// or an explicit reload) and swaps the registry and rules atomically. An invalid config is rejected and the running
/// one is kept.
/// </summary>
public sealed class FleetConfigurationHost(string root, FleetConfiguration initial, AgentRegistry registry, RulesEngine rules)
{
    private readonly Lock _gate = new();
    private FleetConfiguration _current = initial;

    /// <summary>Raised after a successful reload, e.g. so the scheduler resynchronizes routines.</summary>
    public event EventHandler<FleetConfiguration>? Reloaded;

    public string Root { get; } = Path.GetFullPath(root);

    public FleetConfiguration Current => Volatile.Read(ref _current);

    /// <summary>Loads and validates the config root, then applies it. Throws <see cref="FleetConfigurationException"/> on errors.</summary>
    public FleetConfiguration Reload()
    {
        FleetConfiguration next;
        lock (_gate)
        {
            next = FleetConfigurationLoader.Load(Root);
            Volatile.Write(ref _current, next);
            rules.ReplaceConfigured(next.Rules);
            registry.Replace(next.Agents);
        }

        Reloaded?.Invoke(this, next);
        return next;
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to a scratch copy of the config root and validates the result, so a bad change
    /// never touches the live tree. Returns the validation errors (empty when valid).
    /// </summary>
    public IReadOnlyList<string> Validate(Action<string> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var scratch = Path.Combine(Path.GetTempPath(), "fleet-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(Root, scratch);
            edit(scratch);
            FleetConfigurationLoader.Load(scratch);
            return [];
        }
        catch (FleetConfigurationException ex)
        {
            return ex.Errors;
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    internal static void CopyDirectory(string source, string destination, Func<string, bool>? include = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (include?.Invoke(file) ?? true)
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name == ".git" || !(include?.Invoke(dir) ?? true))
            {
                continue;
            }

            CopyDirectory(dir, Path.Combine(destination, name), include);
        }
    }
}
