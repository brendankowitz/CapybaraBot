using Capybara.Hosting.DependencyInjection;
using Capybara.Hosting.Storage;
using Microsoft.Extensions.Configuration;

namespace Capybara.Hosting.Backup;

/// <summary>
/// The <c>capybarad backup</c> / <c>capybarad restore &lt;archive&gt; &lt;destination&gt;</c> command line (FR-HST-05), run by
/// <c>capybara-backup.service</c>. capybarad dispatches here before building its web host:
/// <code>
/// if (BackupCommand.IsBackupCommand(args))
///     return await BackupCommand.RunAsync(args, configuration, HostingOptions.FromConfiguration(configuration), Console.Out, Console.Error, cancellationToken: lifetimeToken);
/// </code>
/// Settings come from the <c>Capybara:Backup</c> section (<see cref="BackupConfiguration"/>); archives live in the blob store rooted at
/// <see cref="HostingOptions.BlobRoot"/> (<c>Capybara:Hosting:BlobRoot</c>), which is never itself archived.
/// </summary>
public static class BackupCommand
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;

    private const string Usage = "Usage: capybarad backup | capybarad restore <archive> <destination>  (<archive> is a blob path such as backups/20260101T020000000Z.tar.gz; <destination> must be empty or new)";

    /// <summary>Whether <paramref name="args"/> start with a backup verb (<c>backup</c> or <c>restore</c>).</summary>
    public static bool IsBackupCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0] is "backup" or "restore";

    /// <summary>Runs the command in <paramref name="args"/> (including the leading verb) and returns a process exit code.</summary>
    /// <returns><see cref="ExitOk"/>, <see cref="ExitFailed"/> (backup or restore failed, including tamper or corruption detection) or <see cref="ExitUsage"/>.</returns>
    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        IConfiguration configuration,
        HostingOptions hosting,
        TextWriter output,
        TextWriter error,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hosting);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return args switch
            {
                ["backup"] => await BackupAsync(configuration, hosting, output, error, timeProvider ?? TimeProvider.System, cancellationToken),
                ["restore", var archive, var destination] => await RestoreAsync(archive, destination, configuration, hosting, output, error, cancellationToken),
                _ => await UsageErrorAsync(error, Usage),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await error.WriteLineAsync($"capybarad {args[0]} failed: {ex.Message}");
            return ExitFailed;
        }
    }

    private static async Task<int> BackupAsync(IConfiguration configuration, HostingOptions hosting, TextWriter output, TextWriter error, TimeProvider time, CancellationToken cancellationToken)
    {
        var options = hosting.Backup ?? BackupConfiguration.Bind(configuration)?.Options;
        if (options is null)
        {
            return await UsageErrorAsync(error, $"No backup sources are configured. Define {BackupConfiguration.SectionName}:Sources (see deploy/capybarad.backup.sample.json).");
        }

        var service = CreateService(options, hosting, time);
        var result = await service.BackupAsync(cancellationToken);
        await output.WriteLineAsync($"Backup written to {result.Path} ({result.Manifest.Entries.Count} files).");
        return ExitOk;
    }

    private static async Task<int> RestoreAsync(string archive, string destination, IConfiguration configuration, HostingOptions hosting, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            return await UsageErrorAsync(error, $"Restore destination '{destination}' is not empty; restore into a clean directory, then move the files into place.");
        }

        var options = hosting.Backup ?? BackupConfiguration.Bind(configuration)?.Options ?? new BackupOptions([]);
        await CreateService(options, hosting, TimeProvider.System).RestoreAsync(archive, destination, cancellationToken);
        await output.WriteLineAsync($"Restored {archive} into {Path.GetFullPath(destination)} (manifest verified).");
        return ExitOk;
    }

    private static BackupService CreateService(BackupOptions options, HostingOptions hosting, TimeProvider time) =>
        new(new FileSystemBlobStore(hosting.BlobRoot), time, options with { ExcludedDirectories = [.. options.ExcludedDirectories ?? [], hosting.BlobRoot] });

    private static async Task<int> UsageErrorAsync(TextWriter error, string message)
    {
        await error.WriteLineAsync(message);
        return ExitUsage;
    }
}
