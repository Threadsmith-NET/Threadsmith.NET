namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Models.OpenAiCodex;
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

    /// <summary>New priority support remains usable when persistence fails.</summary>
    [Fact]
    public static async Task CacheWriteFailureKeepsRefreshedPriorityCapabilityAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var legacy = new OpenAiCodexModelConfiguration { Id = ModelProfileId.New(), Name = "Fixture", ModelId = "fixture" };
        var cached = new OpenAiCodexProviderConfiguration { Id = "openai-codex", Name = "Codex", Models = [legacy] };
        var discovered = cached with { Models = [legacy with { SupportsPriorityTier = true }] };
        OpenAiCodexProviderConfiguration? attemptedWrite = null;

        var active = await ModelComposition.RefreshCodexCatalogAsync(
            cached,
            _ => Task.FromResult(discovered),
            (configuration, _) =>
            {
                attemptedWrite = configuration;
                return Task.FromException(new IOException("Fixture cache write failure."));
            },
            NullLogger.Instance,
            progress: null,
            attemptCancellationToken: cancellationToken,
            cancellationToken: cancellationToken);

        Assert.Same(discovered, attemptedWrite);
        Assert.Same(discovered, active);
    }

    /// <summary>Malformed optional discovery leaves the original cache usable and untouched.</summary>
    [Fact]
    public static async Task InvalidDiscoveryKeepsCachedCapabilitiesAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var model = new OpenAiCodexModelConfiguration { Id = ModelProfileId.New(), Name = "Fixture", ModelId = "fixture" };
        var cached = new OpenAiCodexProviderConfiguration { Id = "openai-codex", Name = "Codex", Models = [model] };

        var active = await ModelComposition.RefreshCodexCatalogAsync(
            cached,
            _ => Task.FromException<OpenAiCodexProviderConfiguration>(new InvalidDataException("Missing models.")),
            (_, _) => throw new InvalidOperationException("A failed discovery must not write the cache."),
            NullLogger.Instance,
            progress: null,
            attemptCancellationToken: cancellationToken,
            cancellationToken: cancellationToken);

        Assert.Same(cached, active);
    }
}
