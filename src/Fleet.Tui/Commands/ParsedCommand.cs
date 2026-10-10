namespace Fleet.Tui.Commands;

public sealed record ParsedCommand(string Name, IReadOnlyList<string> Arguments, Uri BaseUrl, bool Live, bool ReleaseKillSwitch)
{
    public static ParsedCommand Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var baseUrl = new Uri(Environment.GetEnvironmentVariable("FLEET_URL") ?? "http://127.0.0.1:8787");
        var live = false;
        var release = false;
        var remaining = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg is "--base-url" or "-u")
            {
                if (i + 1 >= args.Count)
                {
                    throw new ArgumentException("--base-url requires a value.");
                }

                baseUrl = new Uri(args[++i]);
            }
            else if (arg == "--live")
            {
                live = true;
            }
            else if (arg == "--release")
            {
                release = true;
            }
            else
            {
                remaining.Add(arg);
            }
        }

        var name = remaining.Count == 0 ? "dashboard" : remaining[0];
        return new ParsedCommand(name, remaining.Skip(1).ToList(), baseUrl, live, release);
    }
}
