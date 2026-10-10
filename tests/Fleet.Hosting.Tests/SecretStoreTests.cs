using Fleet.Hosting.Storage;

namespace Fleet.Hosting.Tests;

public sealed class SecretStoreTests
{
    [Fact]
    public async Task Systemd_creds_validates_names_and_passes_safe_arguments()
    {
        using var temp = new LocalTempDirectory();
        var runner = new FakeCommandRunner();
        var store = new SystemdCredsSecretStore(temp.Path, runner);

        await Assert.ThrowsAsync<ArgumentException>(async () => await store.SetAsync("Bad/Name", "secret", TestContext.Current.CancellationToken));
        await store.SetAsync("github-token", "secret", TestContext.Current.CancellationToken);

        var request = Assert.Single(runner.Requests);
        Assert.Equal("systemd-creds", request.FileName);
        Assert.Contains("--name=github-token", request.Arguments);
        Assert.Equal("secret", request.StandardInput);
        Assert.EndsWith("github-token.cred", request.Arguments[^1], StringComparison.Ordinal);
    }
}
