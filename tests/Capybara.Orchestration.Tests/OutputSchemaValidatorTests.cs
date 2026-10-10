using Capybara.Orchestration.Tasks;

namespace Capybara.Orchestration.Tests;

/// <summary>FR-A2A-09: task results are checked against the output schema, and what cannot be checked is listed.</summary>
public sealed class OutputSchemaValidatorTests
{
    private const string Schema = """
        {"type":"object","required":["title","links"],"additionalProperties":false,
         "properties":{"title":{"type":"string"},"links":{"type":"array","items":{"type":"string"}},"score":{"type":"integer"}}}
        """;

    [Fact]
    public void A_matching_result_is_valid()
    {
        var check = OutputSchemaValidator.Check(Schema, """{"title":"t","links":["a","b"],"score":3}""");

        Assert.True(check.IsValid);
        Assert.Empty(check.Unchecked);
    }

    [Theory]
    [InlineData("""{"links":[]}""", "missing required property 'title'")]
    [InlineData("""{"title":1,"links":[]}""", "$.title: expected string")]
    [InlineData("""{"title":"t","links":[1]}""", "$.links[0]: expected string")]
    [InlineData("""{"title":"t","links":[],"extra":true}""", "unexpected property 'extra'")]
    [InlineData("""{"title":"t","links":[],"score":1.5}""", "$.score: expected integer")]
    [InlineData("plain prose", "must be JSON")]
    public void Mismatches_are_errors(string result, string expected)
    {
        var check = OutputSchemaValidator.Check(Schema, result);

        Assert.False(check.IsValid);
        Assert.Contains(check.Errors, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void Prose_schemas_are_reported_as_unchecked_rather_than_passed_silently()
    {
        var check = OutputSchemaValidator.Check("a bullet list of links", "- a\n- b");

        Assert.True(check.IsValid);
        Assert.Single(check.Unchecked);
    }

    [Fact]
    public void Unsupported_keywords_are_reported_as_unchecked()
    {
        var check = OutputSchemaValidator.Check("""{"type":"string","pattern":"^a"}""", "\"abc\"");

        Assert.True(check.IsValid);
        Assert.Contains(check.Unchecked, u => u.Contains("pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void No_schema_means_nothing_to_check()
    {
        var check = OutputSchemaValidator.Check(null, "anything");

        Assert.True(check.IsValid);
        Assert.Empty(check.Unchecked);
    }
}
