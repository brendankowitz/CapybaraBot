using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Capybara.Hosting.Backup;

/// <summary>
/// Binds backup settings from the <c>Capybara:Backup</c> configuration section (FR-HST-05). Shape:
/// <c>Prefix</c>, <c>WorkingDirectory</c>, <c>RetainCount</c>, <c>ScheduleUtc</c> ("HH:mm"), <c>ExcludedDirectories[]</c> and
/// <c>Sources[]</c> of <c>{ Name, Path, Kind: Files|SqliteDatabase }</c> — one source per agent COPILOT_HOME, the memory repo,
/// each SQLite store and the encrypted credential directory.
/// </summary>
public static class BackupConfiguration
{
    public const string SectionName = "Capybara:Backup";

    /// <summary>Returns the bound settings, or <see langword="null"/> when the section defines no sources.</summary>
    /// <exception cref="InvalidOperationException">A source is missing its name or path, or a value cannot be parsed.</exception>
    public static (BackupOptions Options, BackupScheduleOptions Schedule)? Bind(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var sources = section.GetSection("Sources").GetChildren().Select(ReadSource).ToList();
        if (sources.Count == 0)
        {
            return null;
        }

        var defaults = new BackupOptions(sources);
        var options = defaults with
        {
            Prefix = section["Prefix"] ?? defaults.Prefix,
            WorkingDirectory = section["WorkingDirectory"],
            RetainCount = ReadRetainCount(section["RetainCount"], defaults.RetainCount),
            ExcludedDirectories = section.GetSection("ExcludedDirectories").GetChildren().Select(c => c.Value).OfType<string>().ToList(),
        };
        return (options, ReadSchedule(section["ScheduleUtc"]));
    }

    private static BackupSource ReadSource(IConfigurationSection child)
    {
        var name = child["Name"];
        var path = child["Path"];
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"{SectionName}:Sources:{child.Key} needs both Name and Path.");
        }

        var kind = BackupSourceKind.Files;
        if (child["Kind"] is { Length: > 0 } text && !Enum.TryParse(text, ignoreCase: true, out kind))
        {
            throw new InvalidOperationException($"{SectionName}:Sources:{child.Key}:Kind '{text}' is not one of {string.Join(", ", Enum.GetNames<BackupSourceKind>())}.");
        }

        return new BackupSource(name, path, kind);
    }

    private static int ReadRetainCount(string? text, int fallback) =>
        text is null ? fallback
        : int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count >= 1 ? count
        : throw new InvalidOperationException($"{SectionName}:RetainCount '{text}' must be an integer >= 1.");

    private static BackupScheduleOptions ReadSchedule(string? text) =>
        text is null ? BackupScheduleOptions.Default
        : TimeOnly.TryParseExact(text, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? new BackupScheduleOptions(time)
        : throw new InvalidOperationException($"{SectionName}:ScheduleUtc '{text}' must be a UTC time formatted HH:mm.");
}
