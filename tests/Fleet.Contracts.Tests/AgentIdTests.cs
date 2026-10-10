using System.Text.Json;
using Fleet.Contracts.Agents;

namespace Fleet.Contracts.Tests;

public sealed class AgentIdTests
{
    [Theory]
    [InlineData("ea")]
    [InlineData("research")]
    [InlineData("code-2")]
    public void Parse_accepts_valid_ids(string value) => Assert.Equal(value, AgentId.Parse(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("E")]
    [InlineData("1abc")]
    [InlineData("../etc")]
    [InlineData("has space")]
    [InlineData("this-id-is-way-too-long-to-be-accepted-by-fleet")]
    public void TryParse_rejects_invalid_ids(string value) => Assert.False(AgentId.TryParse(value, out _));

    [Fact]
    public void Round_trips_through_json_as_a_string()
    {
        var json = JsonSerializer.Serialize(AgentId.Parse("ea"));
        Assert.Equal("\"ea\"", json);
        Assert.Equal(AgentId.Parse("ea"), JsonSerializer.Deserialize<AgentId>(json));
    }
}
