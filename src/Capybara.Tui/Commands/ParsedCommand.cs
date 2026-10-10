namespace Capybara.Tui.Commands;

/// <summary>
/// Command line after global options. <see cref="AcknowledgedCredits"/> (<c>--ack N</c>) is the non-interactive cost
/// acknowledgement for <c>routines enable</c> (FR-RTN-09); <see cref="OutputPath"/> (<c>--out FILE</c>) is the
/// destination for <c>memory export</c>; <see cref="Yes"/> (<c>--yes</c>) skips the confirmation before an approval is
/// allowed (it never overrides a refusal).
/// </summary>
public sealed record ParsedCommand(
    string Name,
    IReadOnlyList<string> Arguments,
    Uri BaseUrl,
    bool Live,
    bool ReleaseKillSwitch,
    decimal? AcknowledgedCredits = null,
    string? OutputPath = null,
    bool Yes = false)
{
    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var baseUrl = new Uri(Environment.GetEnvironmentVariable("CAPYBARA_URL") ?? "http://127.0.0.1:8787");
        var live = false;
        var release = false;
        var yes = false;
        decimal? acknowledged = null;
        string? output = null;
        var remaining = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--base-url" or "-u")
            {
                baseUrl = new Uri(ValueOf(args, ref i, arg));
            }
            else if (arg == "--ack")
            {
                var text = ValueOf(args, ref i, arg);
                acknowledged = decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var credits) && credits >= 0
                    ? credits
                    : throw new ArgumentException($"--ack requires a non-negative number of credits, got '{text}'.");
            }
            else if (arg == "--out")
            {
                output = ValueOf(args, ref i, arg);
            }
            else if (arg == "--live")
            {
                live = true;
            }
            else if (arg == "--release")
            {
                release = true;
            }
            else if (arg is "--yes" or "-y")
            {
                yes = true;
            }
            else
            {
                remaining.Add(arg);
            }
        }

        var name = remaining.Count == 0 ? "dashboard" : remaining[0];
        return new ParsedCommand(name, remaining.Skip(1).ToList(), baseUrl, live, release, acknowledged, output, yes);
    }

    private static string ValueOf(IReadOnlyList<string> args, ref int index, string option) =>
        index + 1 < args.Count ? args[++index] : throw new ArgumentException($"{option} requires a value.");
}
