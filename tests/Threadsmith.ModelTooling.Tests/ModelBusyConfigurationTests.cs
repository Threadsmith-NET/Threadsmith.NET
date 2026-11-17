namespace Threadsmith.ModelTooling.Tests;

using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Busy timeout JSON defaults, validation, and runtime projection.</summary>
public static class ModelBusyConfigurationTests
{
    /// <summary>Catalog models round-trip the configurable window and retain its default when omitted.</summary>
    [Theory]
    [InlineData(null, 15)]
    [InlineData(0, 0)]
    [InlineData(30, 30)]
    [InlineData(ModelRetryPolicy.MaximumBusyTimeoutSeconds, ModelRetryPolicy.MaximumBusyTimeoutSeconds)]
    public static void CatalogProjectsBusyWindow(int? configured, int expected)
    {
        var registration = new OpenAiCompatibleProviderRegistration();
        var model = Model();
        if (configured is { } seconds)
        {
            model = model with { RetryBusyTimeoutSeconds = seconds };
        }

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var json = JsonSerializer.SerializeToNode(model, options)!.AsObject();
        if (configured is null)
        {
            Assert.True(json.Remove("retryBusyTimeoutSeconds"));
        }

        var roundTrip = json.Deserialize<OpenAiCompatibleModelConfiguration>(options)!;
        var provider = Provider(roundTrip);
        registration.Validate(provider);
        var profile = Assert.Single(registration.CreateProfiles(provider));
        Assert.Equal(TimeSpan.FromSeconds(expected), profile.RetryPolicy.BusyTimeout);
    }

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
