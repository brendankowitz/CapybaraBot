using Capybara.Orchestration.Registry;
using Capybara.Orchestration.Versioning;
using Capybara.Policy.Rules;

namespace Capybara.Orchestration.Configuration;

/// <summary>
/// Owns the live configuration. A reload happens only after a human action (approved proposal, lifecycle operation,
/// or an explicit reload) and swaps the registry and rules atomically. An invalid config is rejected and the running
/// one is kept.
/// </summary>
/// <remarks>
/// Every write to the config tree goes through <see cref="ApplyAsync"/>, which serializes writers, so a validated
/// change cannot interleave with another one.
/// </remarks>
/// <param name="root">The live config tree.</param>
/// <param name="initial">The configuration already loaded from <paramref name="root"/>.</param>
/// <param name="registry">Swapped to the new agents on every reload.</param>
/// <param name="rules">Its configured rules are swapped on every reload; learned rules are kept.</param>
/// <param name="versioning">Commits every applied change.</param>
/// <param name="scratchRoot">
/// Where validation copies are made (default: the system temp directory). Set it to a private directory under the state
/// root: the copy holds the whole config, so it must not land in a shared <c>/tmp</c>. Credentials are never copied.
/// </param>
public sealed class CapybaraConfigurationHost(
    string root,
    CapybaraConfiguration initial,
    AgentRegistry registry,
    RulesEngine rules,
    IConfigVersioning versioning,
    string? scratchRoot = null) : IDisposable
{
    /// <summary>Encrypted systemd credentials live here; they are never copied, validated or rewritten.</summary>
    public const string CredentialsFolder = "credentials";

    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CapybaraConfiguration _current = initial;

    /// <summary>
    /// Raised after a successful reload, e.g. so the scheduler resynchronizes routines. When the reload is part of
    /// <see cref="ApplyAsync"/> the write lock is held, so handlers must not change the config themselves.
    /// </summary>
    public event EventHandler<CapybaraConfiguration>? Reloaded;

    public string Root { get; } = Path.GetFullPath(root);

    public CapybaraConfiguration Current => Volatile.Read(ref _current);

    /// <summary>Loads and validates the config root, then applies it. Throws <see cref="CapybaraConfigurationException"/> on errors.</summary>
    public CapybaraConfiguration Reload()
    {
        CapybaraConfiguration next;
        lock (_gate)
        {
            next = CapybaraConfigurationLoader.Load(Root);
            Volatile.Write(ref _current, next);
            rules.ReplaceConfigured(next.Rules);
            registry.Replace(next.Agents);
        }

        Reloaded?.Invoke(this, next);
        return next;
    }

    /// <summary>
    /// Applies <paramref name="edit"/> to a scratch copy of the config root and validates the result, so a bad change
    /// never touches the live tree. Returns the validation errors (empty when valid). An edit reports its own
    /// precondition failures by throwing <see cref="CapybaraConfigurationException"/>.
    /// </summary>
    public IReadOnlyList<string> Validate(Action<string> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);

        // Synchronous API: block rather than validate a tree that ApplyAsync is half-way through writing.
        _writeLock.Wait();
        try
        {
            return ValidateCore(edit);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// The single write path for human config changes (FR-AGT-06, FR-AGT-07, FR-SAF-11): validates
    /// <paramref name="edit"/> on a scratch copy, then runs the same edit on the live tree, reloads, and commits it
    /// through <see cref="IConfigVersioning"/>. The edit must be deterministic, because it runs twice. An exception
    /// other than <see cref="CapybaraConfigurationException"/> or <see cref="UnauthorizedAccessException"/> from the
    /// scratch run propagates with nothing written. A permission failure is returned as an error; on the live run it may
    /// leave that edit partially applied on disk, as may any other I/O failure there (which propagates).
    /// </summary>
    public async ValueTask<ConfigChangeResult> ApplyAsync(Action<string> edit, string commitMessage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentException.ThrowIfNullOrWhiteSpace(commitMessage);
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            var errors = ValidateCore(edit);
            if (errors.Count > 0)
            {
                return new ConfigChangeResult(errors);
            }

            // Past this point the change is applied even if the caller cancels: a half-applied tree is worse.
            try
            {
                edit(Root);
            }
            catch (UnauthorizedAccessException ex)
            {
                return new ConfigChangeResult([$"The config tree is not writable ({ex.Message}); the change may be partially applied."]);
            }

            Reload();
            try
            {
                await versioning.CommitAsync(commitMessage, CancellationToken.None);
                return new ConfigChangeResult([]);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                return new ConfigChangeResult([], ex.Message);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose() => _writeLock.Dispose();

    /// <summary>
    /// Runs <paramref name="write"/> on the live tree under the write lock without validating, reloading or
    /// committing. Only for files the loader ignores and git excludes, such as staged skills under
    /// <c>skills/pending/</c>, so no validation can observe a half-written file.
    /// </summary>
    internal async ValueTask WriteUnversionedAsync(Action<string> write, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            write(Root);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private IReadOnlyList<string> ValidateCore(Action<string> edit)
    {
        // A private (0700) directory, never a world-listable temp path; credentials are left out of the copy.
        var scratch = Path.Combine(scratchRoot ?? Path.GetTempPath(), "capybara-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            ConfigTree.CreateDirectory(scratch, PrivateDirectory);
            CopyDirectory(Root, scratch, path => !IsCredential(path));
            edit(scratch);
            CapybaraConfigurationLoader.Load(scratch);
            return [];
        }
        catch (CapybaraConfigurationException ex)
        {
            return ex.Errors.Count > 0 ? ex.Errors : [ex.Message];
        }
        catch (UnauthorizedAccessException ex)
        {
            return [$"The config tree could not be read or written ({ex.Message})."];
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private bool IsCredential(string path) =>
        path.EndsWith(".cred", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.GetFullPath(path), Path.Combine(Root, CredentialsFolder), StringComparison.Ordinal);

    /// <summary>Copies a tree, giving new directories and files the config tree's modes (see <see cref="ConfigTree.DirectoryMode"/>).</summary>
    internal static void CopyDirectory(string source, string destination, Func<string, bool>? include = null)
    {
        ConfigTree.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if (include?.Invoke(file) ?? true)
            {
                var target = Path.Combine(destination, Path.GetFileName(file));
                File.Copy(file, target, overwrite: true);
                ConfigTree.SetFileMode(target);
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