using System.Security.Cryptography;
using Capybara.Connections.Mcp;
using Capybara.Testing;

namespace Capybara.Connections.Tests;

public sealed class McpServerCatalogTests
{
    [Fact]
    public async Task Startup_verification_reports_hash_mismatch_and_missing_files()
    {
        using var dir = new TempDirectory();
        var good = Path.Combine(dir.Path, "good.bin");
        var bad = Path.Combine(dir.Path, "bad.bin");
        var bytes = RandomNumberGenerator.GetBytes(16);
        await File.WriteAllBytesAsync(good, bytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(bad, bytes, TestContext.Current.CancellationToken);
        var pin = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var catalog = new McpServerCatalog(
        [
            new McpServerRegistration { Name = "good", ExecutablePath = good, Sha256 = pin },
            new McpServerRegistration { Name = "bad", ExecutablePath = bad, Sha256 = new string('0', 64) },
            new McpServerRegistration { Name = "gone", ExecutablePath = Path.Combine(dir.Path, "gone.bin"), Sha256 = pin },
        ]);

        var errors = await catalog.VerifyAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("'bad'", StringComparison.Ordinal) && e.Contains("mismatch", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'gone'", StringComparison.Ordinal));
    }
}
