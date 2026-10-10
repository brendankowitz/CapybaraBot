using Fleet.Policy.Audit;
using Fleet.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Fleet.Policy.Tests;

public sealed class FileAuditLogTests
{
    [Fact]
    public async Task Hash_chain_detects_tampering()
    {
        using var dir = new TempDirectory();
        var path = Path.Combine(dir.Path, "audit.jsonl");
        var ct = TestContext.Current.CancellationToken;

        using (var log = new FileAuditLog(path, new FakeTimeProvider()))
        {
            await log.AppendAsync(new AuditRecord("tool", "allow", "ea", Tool: "email_read"), ct);
            await log.AppendAsync(new AuditRecord("tool", "deny", "ea", Tool: "email_send"), ct);
            Assert.True(await log.VerifyAsync(ct));
        }

        var lines = await File.ReadAllLinesAsync(path, ct);
        lines[0] = lines[0].Replace("\"allow\"", "\"deny\"", StringComparison.Ordinal);
        await File.WriteAllLinesAsync(path, lines, ct);

        using var reopened = new FileAuditLog(path, new FakeTimeProvider());
        Assert.False(await reopened.VerifyAsync(ct));
    }
}
