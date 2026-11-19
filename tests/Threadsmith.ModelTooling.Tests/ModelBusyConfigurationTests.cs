namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Busy timeout JSON defaults, validation, and runtime projection.</summary>
public static class ModelBusyConfigurationTests
{
    /// <summary>Invalid timer ranges fail before activation.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(ModelRetryPolicy.MaximumBusyTimeoutSeconds + 1)]
    public static void InvalidBusyWindowIsRejected(int seconds)
    {
        Assert.Throws<InvalidOperationException>(() => new OpenAiCompatibleProviderRegistration().Validate(
            Provider(Model() with { RetryBusyTimeoutSeconds = seconds })));
    }

    private static OpenAiCompatibleModelConfiguration Model() => new()
    {
        Id = ModelProfileId.New(),
        Name = "fixture",
        ModelId = "fixture",
        ContextWindow = 32000,
        MaximumOutputTokens = 4000,
    };

    private static OpenAiCompatibleProviderConfiguration Provider(OpenAiCompatibleModelConfiguration model) => new()
    {
        Id = "fixture",
        Name = "fixture",
        BaseUri = new Uri("https://models.example/v1/"),
        Models = [model],
    };
}
