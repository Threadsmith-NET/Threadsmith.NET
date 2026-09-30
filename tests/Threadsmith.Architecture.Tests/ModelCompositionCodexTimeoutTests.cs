namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.App;
using Xunit;

/// <summary>Checks the trusted native Codex request deadline used during model composition.</summary>
public static class ModelCompositionCodexTimeoutTests
{
    /// <summary>Omitted configuration uses the longer native Codex request deadline.</summary>
    [Fact]
    public static void LoadCodexTimeoutSeconds_UsesDefault()
    {
        var configuration = new ConfigurationBuilder().Build();

        var seconds = ModelComposition.LoadCodexTimeoutSeconds(configuration);

        Assert.Equal(600, seconds);
    }

    /// <summary>Trusted configuration can extend or disable the native Codex deadline.</summary>
    [Theory]
    [InlineData("900", 900)]
    [InlineData("0", 0)]
    [InlineData("4294967", 4294967)]
    public static void LoadCodexTimeoutSeconds_UsesConfiguredValue(string configured, int expected)
    {
        var configuration = Build(configured);

        var seconds = ModelComposition.LoadCodexTimeoutSeconds(configuration);

        Assert.Equal(expected, seconds);
    }

    /// <summary>Invalid deadlines fail startup without echoing configured content.</summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("4294968")]
    [InlineData("invalid-value")]
    public static void LoadCodexTimeoutSeconds_RejectsInvalidValue(string configured)
    {
        var configuration = Build(configured);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            ModelComposition.LoadCodexTimeoutSeconds(configuration));

        Assert.Contains("model:openAiCodex:timeoutSeconds", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(configured, exception.Message, StringComparison.Ordinal);
    }

    private static IConfigurationRoot Build(string value)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["model:openAiCodex:timeoutSeconds"] = value,
            })
            .Build();
    }
}
