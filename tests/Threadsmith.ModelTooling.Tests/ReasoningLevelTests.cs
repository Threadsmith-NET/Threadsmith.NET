namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Models;
using Xunit;

/// <summary>Verifies custom reasoning names and compatibility with persisted enum snapshots.</summary>
public static class ReasoningLevelTests
{
    /// <summary>JSON preserves model-specific names in both values and mapping keys.</summary>
    [Theory]
    [InlineData("xhigh")]
    [InlineData("provider-Custom")]
    public static void Json_ValuesAndMappingKeys_RoundTripCustomNames(string name)
    {
        var level = new ReasoningLevel(name);
        var mapping = new Dictionary<ReasoningLevel, string> { [level] = "ProviderEffort" };

        var json = JsonSerializer.Serialize(mapping);
        var restored = JsonSerializer.Deserialize<Dictionary<ReasoningLevel, string>>(json);

        Assert.NotNull(restored);
        var entry = Assert.Single(restored);
        Assert.Equal(name, entry.Key.Value);
        Assert.Equal("ProviderEffort", entry.Value);
        Assert.Equal(level, JsonSerializer.Deserialize<ReasoningLevel>(JsonSerializer.Serialize(level)));
    }

    /// <summary>Request snapshots written with the former numeric enum remain readable.</summary>
    [Theory]
    [InlineData("0", "none")]
    [InlineData("1", "minimal")]
    [InlineData("2", "low")]
    [InlineData("3", "medium")]
    [InlineData("4", "high")]
    public static void Json_LegacyNumericSnapshot_RemainsReadable(string json, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<ReasoningLevel>(json).Value);
    }
}
