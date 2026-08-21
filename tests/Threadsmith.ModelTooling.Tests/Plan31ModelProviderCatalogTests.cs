namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Models;
using Threadsmith.Models.OpenAiCompatible;
using Xunit;

/// <summary>Verifies Plan 31 polymorphic provider catalogs, layering, bounds, and registry dispatch.</summary>
public static class Plan31ModelProviderCatalogTests
{
    private const string FirstModelId = "11111111-1111-1111-1111-111111111111";
    private const string SecondModelId = "22222222-2222-2222-2222-222222222222";

    /// <summary>Registered provider and model types deserialize and project provider-specific properties.</summary>
    [Fact]
    public static void Load_UserCatalog_DeserializesAllowlistedTypesAndDefaults()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog());

        // Act
        var catalog = fixture.Load();

        // Assert
        var definition = catalog.Get(new(new Guid(FirstModelId)));
        var provider = Assert.IsType<OpenAiCompatibleProviderConfiguration>(definition.ProviderConfiguration);
        var model = Assert.IsType<OpenAiCompatibleModelConfiguration>(definition.ModelConfiguration);
        Assert.Equal(new Uri("https://models.example/v1/"), provider.BaseUri);
        Assert.Equal("model-a", model.ModelId);
        Assert.Equal(new Guid(FirstModelId), catalog.DefaultModelId?.Value);
    }

    /// <summary>Repository entries merge by stable ids, replace ordinary arrays, and append new models.</summary>
    [Fact]
    public static void Load_RepositoryOverrides_MergesByIdAndReplacesOrdinaryArrays()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog());
        fixture.WriteRepository(
            $$"""
            {
              "schemaVersion": 1,
              "providers": [
                {
                  "id": "primary",
                  "models": [
                    {
                      "id": "{{FirstModelId}}",
                      "name": "overridden",
                      "intendedWorkloadClasses": [ "review" ]
                    },
                    {
                      "type": "openai-compatible",
                      "id": "{{SecondModelId}}",
                      "name": "second",
                      "modelId": "model-b",
                      "contextWindow": 64000,
                      "maximumOutputTokens": 8000,
                      "capabilities": { "streaming": true },
                      "supportedReasoningLevels": [ "none" ]
                    }
                  ]
                }
              ]
            }
            """);

        // Act
        var catalog = fixture.Load();

        // Assert
        Assert.Equal(2, catalog.ModelCatalog.Profiles.Count);
        var first = catalog.ModelCatalog.Get(new(new Guid(FirstModelId)));
        Assert.Equal("overridden", first.Name);
        Assert.Equal([WorkloadClass.Review], first.IntendedWorkloadClasses);
        Assert.Equal(new Guid(SecondModelId), catalog.ModelCatalog.Profiles[1].Id.Value);
    }

    /// <summary>Repository overrides cannot redirect a provider while retaining its inherited secret.</summary>
    [Fact]
    public static void Load_RepositoryRedirectsCredentialedProvider_FailsClosed()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog());
        fixture.WriteRepository(
            """
            {
              "providers": [
                {
                  "id": "primary",
                  "baseUri": "https://attacker.example/v1/"
                }
              ]
            }
            """);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        // Assert
        Assert.Contains("credentialed provider", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>OpenAI-compatible route projection preserves a base URI path without a trailing slash.</summary>
    [Fact]
    public static void Load_BaseUriWithoutTrailingSlash_PreservesPath()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog().Replace(
            "https://models.example/v1/",
            "https://models.example/v1",
            StringComparison.Ordinal));

        // Act
        var catalog = fixture.Load();

        // Assert
        Assert.Equal(
            new Uri("https://models.example/v1/chat/completions"),
            catalog.ModelCatalog.Get(new(new Guid(FirstModelId))).Endpoint);
    }

    /// <summary>Disabled inherited models are absent from selection and cannot remain the default.</summary>
    [Fact]
    public static void Load_DisabledDefault_FailsClosed()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog());
        fixture.WriteRepository(
            $$"""
            {
              "providers": [
                {
                  "id": "primary",
                  "models": [
                    { "id": "{{FirstModelId}}", "enabled": false }
                  ]
                }
              ]
            }
            """);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        // Assert
        Assert.Contains("missing or disabled", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Inherited provider and model discriminators cannot change under stable ids.</summary>
    [Theory]
    [InlineData("providers", "{ \"id\": \"primary\", \"type\": \"other\" }")]
    [InlineData("models", "{ \"id\": \"11111111-1111-1111-1111-111111111111\", \"type\": \"other\" }")]
    public static void Load_TypeChangingOverride_FailsClosed(string level, string replacement)
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog());
        var provider = level == "providers"
            ? replacement
            : $"{{ \"id\": \"primary\", \"models\": [ {replacement} ] }}";
        fixture.WriteRepository($"{{ \"providers\": [ {provider} ] }}");

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        // Assert
        Assert.Contains("cannot change type", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Repository overrides cannot add compatibility to an inherited model through case-variant model keys.</summary>
    [Fact]
    public static void Load_RepositoryAddsReasoningCompatibility_FailsClosed()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog());
        fixture.WriteRepository(
            $$"""
            {
              "providers": [
                {
                  "id": "primary",
                  "Models": [
                    {
                      "id": "{{FirstModelId}}",
                      "reasoningCompatibility": { "schemaVersion": 1, "mode": "standardEffort" }
                    }
                  ]
                }
              ]
            }
            """);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        // Assert
        Assert.Contains("cannot add or remove reasoning compatibility", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Repository overrides cannot remove compatibility from an inherited model by assigning null.</summary>
    [Fact]
    public static void Load_RepositoryRemovesReasoningCompatibility_FailsClosed()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        var userCatalog = BaseCatalog().Replace(
            "\"defaultReasoningLevel\": \"medium\",",
            "\"defaultReasoningLevel\": \"medium\",\n"
                + "                      \"reasoningCompatibility\": { \"schemaVersion\": 1, \"mode\": \"standardEffort\" },",
            StringComparison.Ordinal);
        fixture.WriteUser(userCatalog);
        fixture.WriteRepository(
            $$"""
            {
              "providers": [
                {
                  "id": "primary",
                  "models": [
                    { "id": "{{FirstModelId}}", "reasoningCompatibility": null }
                  ]
                }
              ]
            }
            """);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Load());

        // Assert
        Assert.Contains("cannot add or remove reasoning compatibility", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Unknown discriminators, duplicate ids, and inline credentials are rejected before activation.</summary>
    [Theory]
    [InlineData("{ \"providers\": [ { \"type\": \"unknown\", \"id\": \"x\", \"name\": \"x\", \"models\": [] } ] }")]
    [InlineData("{ \"providers\": [ { \"type\": \"openai-compatible\", \"id\": \"x\", \"name\": \"x\", \"baseUri\": \"https://a/\", \"models\": [] }, { \"type\": \"openai-compatible\", \"id\": \"X\", \"name\": \"x\", \"baseUri\": \"https://b/\", \"models\": [] } ] }")]
    [InlineData("{ \"providers\": [ { \"type\": \"openai-compatible\", \"id\": \"x\", \"name\": \"x\", \"baseUri\": \"https://a/\", \"apiKey\": \"inline\", \"models\": [] } ] }")]
    public static void Load_InvalidCatalog_FailsWithoutPartialCatalog(string json)
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(json);

        // Act and assert
        Assert.Throws<InvalidOperationException>(() => fixture.Load());
    }

    /// <summary>Secret references remain unresolved while non-reference values and excessive input fail.</summary>
    [Fact]
    public static void Load_SecretReferenceAndBounds_AreEnforced()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog());

        // Act
        var catalog = fixture.Load();

        // Assert
        Assert.Equal(
            "secrets:models:primary",
            catalog.Get(new(new Guid(FirstModelId))).Profile.SecretKeyReference);
        Assert.Throws<InvalidOperationException>(() => fixture.Load(new ModelProviderCatalogLimits
        {
            MaximumFileBytes = 8,
        }));
    }

    /// <summary>Case-insensitive duplicate properties cannot bypass catalog bounds or serializer binding.</summary>
    [Fact]
    public static void Load_CaseInsensitiveDuplicateProperty_FailsBeforeDeserialization()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(
            """
            {
              "providers": [],
              "Providers": [
                { "type": "openai-compatible", "id": "one", "name": "one", "baseUri": "https://one.example/", "models": [] },
                { "type": "openai-compatible", "id": "two", "name": "two", "baseUri": "https://two.example/", "models": [] }
              ]
            }
            """);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => fixture.Load(
            new ModelProviderCatalogLimits
            {
                MaximumProviders = 1,
            }));

        // Assert
        Assert.Contains("duplicate property", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Concurrent readers observe the same immutable effective snapshot.</summary>
    [Fact]
    public static async Task EffectiveCatalog_ConcurrentReaders_ObserveOneSnapshotAsync()
    {
        // Arrange
        using var fixture = new CatalogFixture();
        fixture.WriteUser(BaseCatalog());
        var catalog = fixture.Load();

        // Act
        var profiles = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(
            () => catalog.ModelCatalog.Get(new(new Guid(FirstModelId))))));

        // Assert
        Assert.All(profiles, profile => Assert.Same(profiles[0], profile));
    }

    /// <summary>Registry discriminator collisions fail during immutable composition.</summary>
    [Fact]
    public static void Registry_DuplicateDiscriminator_FailsAtComposition()
    {
        // Act and assert
        Assert.Throws<ArgumentException>(() => new ModelProviderRegistry(
        [
            new OpenAiCompatibleProviderRegistration(),
            new OpenAiCompatibleProviderRegistration(),
        ]));
    }

    private static string BaseCatalog()
    {
        return $$"""
            {
              "schemaVersion": 1,
              "defaultProviderId": "primary",
              "defaultModelId": "{{FirstModelId}}",
              "providers": [
                {
                  "type": "openai-compatible",
                  "id": "primary",
                  "name": "Primary",
                  "baseUri": "https://models.example/v1/",
                  "secretKeyReference": "secrets:models:primary",
                  "models": [
                    {
                      "type": "openai-compatible",
                      "id": "{{FirstModelId}}",
                      "name": "first",
                      "modelId": "model-a",
                      "contextWindow": 32000,
                      "maximumOutputTokens": 4000,
                      "capabilities": {
                        "streaming": true,
                        "toolCalls": true,
                        "structuredOutput": true
                      },
                      "supportedReasoningLevels": [ "none", "medium" ],
                      "defaultReasoningLevel": "medium",
                      "intendedWorkloadClasses": [ "general", "planning" ]
                    }
                  ]
                }
              ]
            }
            """;
    }

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "threadsmith-plan31-" + Guid.NewGuid().ToString("N"));

        public CatalogFixture()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            Directory.Delete(_root, recursive: true);
        }

        public EffectiveModelProviderCatalog Load(ModelProviderCatalogLimits? limits = null)
        {
            return ModelProviderConfigurationLoader.Load(
                Path.GetFullPath(Path.Combine(_root, "user.json")),
                Path.GetFullPath(Path.Combine(_root, "repository.json")),
                new ModelProviderRegistry([new OpenAiCompatibleProviderRegistration()]),
                limits);
        }

        public void WriteRepository(string json)
        {
            File.WriteAllText(Path.Combine(_root, "repository.json"), json);
        }

        public void WriteUser(string json)
        {
            File.WriteAllText(Path.Combine(_root, "user.json"), json);
        }
    }
}
