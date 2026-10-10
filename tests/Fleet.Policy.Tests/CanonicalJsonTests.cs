using System.Text.Json;
using Fleet.Policy.Canonicalization;

namespace Fleet.Policy.Tests;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void Key_order_does_not_change_the_hash()
    {
        var a = JsonDocument.Parse("""{"b":1,"a":{"y":2,"x":[1,2]}}""").RootElement;
        var b = JsonDocument.Parse("""{ "a": {"x":[1,2],"y":2}, "b": 1 }""").RootElement;

        Assert.Equal(CanonicalJson.Hash(a), CanonicalJson.Hash(b));
    }

    [Fact]
    public void Different_values_change_the_hash()
    {
        var a = JsonDocument.Parse("""{"to":"bob"}""").RootElement;
        var b = JsonDocument.Parse("""{"to":"eve"}""").RootElement;

        Assert.NotEqual(CanonicalJson.Hash(a), CanonicalJson.Hash(b));
    }
}
