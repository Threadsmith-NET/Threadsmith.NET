namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Xunit;

/// <summary>Checks how trusted settings select the Codex processing tier.</summary>
public static class ModelCompositionCodexServiceTierTests
{
    /// <summary>Priority must be requested explicitly.</summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("normal", false)]
    [InlineData("priority", true)]
    public static void OnlyPriorityOptsIn(string? value, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        if (value is not null)
        {
            configuration["model:openAiCodex:serviceTier"] = value;
        }

        Assert.Equal(expected, ModelComposition.LoadCodexPriorityTier(configuration));
    }

    /// <summary>Invalid values produce an actionable startup error.</summary>
    [Fact]
    public static void UnknownTierIdentifiesInvalidSetting()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        configuration["model:openAiCodex:serviceTier"] = "unknown";

        var error = Assert.Throws<InvalidOperationException>(() => ModelComposition.LoadCodexPriorityTier(configuration));

        Assert.Contains("model:openAiCodex:serviceTier", error.Message, StringComparison.Ordinal);
    }
}
