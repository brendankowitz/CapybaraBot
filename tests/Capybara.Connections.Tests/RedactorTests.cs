using Capybara.Connections.Vault;

namespace Capybara.Connections.Tests;

public sealed class RedactorTests
{
    [Fact]
    public void Redacts_known_and_pattern_tokens()
    {
        var known = Convert.ToBase64String(Guid.NewGuid().ToByteArray()) + "x";
        var text = $"known={known} bearer=****** gh=ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ123456 jwt=eyJaaaaaaaaaaaa.bbbbbbbbbbbb.cccccccccccc aws=AKIAABCDEFGHIJKLMNOP";

        var redacted = new SecretRedactor([known]).Redact(text);

        Assert.DoesNotContain(known, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("AKIA", redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }
}
