using Fleet.Tui.Commands;

namespace Fleet.Tui.Tests;

public sealed class TuiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Directory.GetCurrentDirectory(), "tui-test-artifacts", Guid.NewGuid().ToString("N"));

    public TuiTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Parser_handles_base_url_live_and_release_flags()
    {
        var command = ParsedCommand.Parse(["--base-url", "https://fleet.example", "kill", "--release", "--live"]);

        Assert.Equal("kill", command.Name);
        Assert.Equal(new Uri("https://fleet.example"), command.BaseUrl);
        Assert.True(command.Live);
        Assert.True(command.ReleaseKillSwitch);
    }

    [Fact]
    public void Token_file_refuses_world_readable_permissions()
    {
        var path = Path.Combine(_directory, "token");
        File.WriteAllText(path, "not-a-real-token");
#pragma warning disable CA1416 // Tests run on the Linux target host used by the TUI.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
#pragma warning restore CA1416

        Assert.Throws<UnauthorizedAccessException>(() => TokenFile.EnsurePrivate(path));
    }

    [Fact]
    public void Token_file_accepts_0600_permissions()
    {
        var path = Path.Combine(_directory, "token");
        File.WriteAllText(path, "not-a-real-token");
#pragma warning disable CA1416 // Tests run on the Linux target host used by the TUI.
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
#pragma warning restore CA1416

        TokenFile.EnsurePrivate(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
