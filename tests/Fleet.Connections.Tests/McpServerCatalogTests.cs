using System.Security.Cryptography;
using Fleet.Connections.Mcp;

namespace Fleet.Connections.Tests;

public sealed class McpServerCatalogTests
{
    [Fact]
    public async Task Refuses_hash_mismatch()
    {
        var root = Path.Combine(Environment.CurrentDirectory, "test-artifacts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "server.bin");
        await File.WriteAllBytesAsync(file, RandomNumberGenerator.GetBytes(16), TestContext.Current.CancellationToken);
        var catalog = new McpServerCatalog([]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await catalog.VerifyAsync(new McpServerRegistration("server", file, new string('0', 64), []), TestContext.Current.CancellationToken));

        Directory.Delete(root, recursive: true);
    }
}
