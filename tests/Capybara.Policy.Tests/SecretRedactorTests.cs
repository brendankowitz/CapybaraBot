using Capybara.Policy.Redaction;

namespace Capybara.Policy.Tests;

/// <summary>FR-SAF-08: the high-precision check used for hard denials versus the loose redaction patterns.</summary>
public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_0123456789abcdefghijklmnop")]
    [InlineData("AKIAABCDEFGHIJKLMNOP")]
    [InlineData("xoxb-1234567890-abcdefghij")]
    [InlineData("-----BEGIN PRIVATE KEY-----\nMIIE\n-----END PRIVATE KEY-----")]
    public void Token_formats_are_secrets(string text) => Assert.True(new SecretRedactor().ContainsSecret($"see {text} here"));

    [Theory]
    [InlineData("var apiKey = options.ProviderKey;")]
    [InlineData("set ClientSecret=placeholder")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz")]
    public void Loose_shapes_are_redacted_but_are_not_secrets(string text)
    {
        var redactor = new SecretRedactor();

        Assert.True(redactor.TryRedact(text, out _));
        Assert.False(redactor.ContainsSecret(text));
    }

    [Fact]
    public void Registered_values_are_secrets()
    {
        var redactor = new SecretRedactor(["s3cr3t-value-xyz"]);

        Assert.True(redactor.ContainsSecret("prefix s3cr3t-value-xyz suffix"));
        Assert.False(redactor.ContainsSecret("prefix s3cr3t-value suffix"));
    }
}
