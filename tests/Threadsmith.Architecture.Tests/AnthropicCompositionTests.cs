namespace Threadsmith.Architecture.Tests;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;
using Threadsmith.Models.OpenAiCompatible;
using Threadsmith.Tools;
using Xunit;

/// <summary>Exercises actual composition ordering and credential authority with isolated SDK HTTP fixtures.</summary>
public sealed class AnthropicCompositionTests
{
    /// <summary>Hydration occurs once before explicit defaults and repository model policy are materialized.</summary>
    [Fact]
    public async Task OneTrustedDiscoveryFeedsIndependentCatalogsAndExplicitDefault()
    {
        using var fixture = new Fixture();
        var id = AnthropicProfileIdentifiers.Create("primary", "claude-opus-5");
        await fixture.WriteUserAsync(new ModelProviderCatalogConfiguration
        {
            Providers = [Provider("primary")], DefaultProviderId = "primary", DefaultModelId = id,
        });
        await File.WriteAllTextAsync(
            fixture.Paths.RepositoryProviderCatalog,
            $$"""{"providers":[{"id":"primary","models":[{"id":"{{id.Value:D}}","name":"Repository Claude","defaultReasoningLevel":"low"}]}]}""",
            TestContext.Current.CancellationToken);
        var (ordinary, trusted) = await fixture.LoadAsync();
        Assert.Equal(1, fixture.Handler.Calls);
        Assert.Single(fixture.Secrets.Requests);
        Assert.Equal(SecretProviderTrust.UserOwned, fixture.Secrets.Requests[0].MinimumTrust);
        Assert.Equal(id, ordinary?.DefaultModelId);
        Assert.Equal(id, trusted?.DefaultModelId);
        Assert.Equal("Repository Claude", Assert.Single(ordinary?.ModelCatalog.Profiles ?? []).Name);
        Assert.Equal("Fixture Claude [primary/claude-opus-5]", Assert.Single(trusted?.ModelCatalog.Profiles ?? []).Name);
        Assert.Equal(ReasoningLevel.Low, ordinary?.ModelCatalog.Profiles[0].DefaultReasoningLevel);
        Assert.Equal(ReasoningLevel.High, trusted?.ModelCatalog.Profiles[0].DefaultReasoningLevel);
        Assert.Equal("secrets:models:primary", ordinary?.ModelCatalog.Profiles[0].SecretKeyReference);
    }

    /// <summary>Different user instances cannot share profile identities or secret-resolution requests.</summary>
    [Fact]
    public async Task TwoInstancesRemainDistinctAndRequestOnlyUserOwnedCredentials()
    {
        using var fixture = new Fixture();
        await fixture.WriteUserAsync(new ModelProviderCatalogConfiguration { Providers = [Provider("primary"), Provider("secondary")] });
        var (ordinary, trusted) = await fixture.LoadAsync();
        Assert.Equal(2, fixture.Handler.Calls);
        Assert.Equal(2, fixture.Secrets.Requests.Count);
        Assert.All(fixture.Secrets.Requests, request => Assert.Equal(SecretProviderTrust.UserOwned, request.MinimumTrust));
        Assert.Equal(2, ordinary?.ModelCatalog.Profiles.Select(profile => profile.Id).Distinct().Count());
        Assert.Equal(2, trusted?.ModelCatalog.Profiles.Count);
        Assert.Equal(2, Directory.GetFiles(fixture.Directory, "anthropic-models-*.json").Length);
    }

    /// <summary>Repository-only discovery descriptors cannot cause privileged secret resolution or network access.</summary>
    [Fact]
    public async Task RepositoryOnlyProviderIsRejectedBeforeCredentialResolution()
    {
        using var fixture = new Fixture();
        var json = JsonSerializer.Serialize(new ModelProviderCatalogConfiguration { Providers = [Provider("repository")] }, fixture.Registry.CreateSerializerOptions());
        await File.WriteAllTextAsync(fixture.Paths.RepositoryProviderCatalog, json, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.LoadAsync);
        Assert.Empty(fixture.Secrets.Requests);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    /// <summary>Unavailable optional discovery does not prevent unrelated configured models from materializing.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalAnthropicOutagePreservesExistingProvider(bool missingKey)
    {
        using var fixture = new Fixture();
        fixture.Secrets.Available = !missingKey;
        fixture.Handler.Status = HttpStatusCode.ServiceUnavailable;
        var other = new OpenAiCompatibleProviderConfiguration
        {
            Id = "other", Name = "Existing", BaseUri = new Uri("https://other.example.test/v1/"),
            Models = [new OpenAiCompatibleModelConfiguration
            {
                Id = new ModelProfileId(Guid.Parse("6052b50b-57c6-426d-b91d-9ff48c27f80e")), Name = "Existing model", ModelId = "existing",
                ContextWindow = 32000, MaximumOutputTokens = 8192, Capabilities = new ModelCapabilitySet { Streaming = true },
                SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
            }
            ],
        };
        await fixture.WriteUserAsync(new ModelProviderCatalogConfiguration { Providers = [Provider("primary"), other] });
        var (ordinary, trusted) = await fixture.LoadAsync();
        Assert.Equal("Existing model", Assert.Single(ordinary?.ModelCatalog.Profiles ?? []).Name);
        Assert.Equal("Existing model", Assert.Single(trusted?.ModelCatalog.Profiles ?? []).Name);
        Assert.Equal(missingKey ? 0 : 1, fixture.Handler.Calls);
    }

    /// <summary>Disabled setup descriptors parse with defaults and do not resolve a key.</summary>
    [Fact]
    public async Task DisabledSetupDescriptorDoesNotDiscover()
    {
        using var fixture = new Fixture();
        await fixture.WriteUserAsync(new ModelProviderCatalogConfiguration { Providers = [Provider("primary") with { Enabled = false }] });
        var (ordinary, trusted) = await fixture.LoadAsync();
        Assert.Empty(ordinary?.ModelCatalog.Profiles ?? []);
        Assert.Empty(trusted?.ModelCatalog.Profiles ?? []);
        Assert.Empty(fixture.Secrets.Requests);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    /// <summary>Repository provider policy cannot redirect a hydrated credentialed instance.</summary>
    [Theory]
    [InlineData("\"secretKeyReference\":\"secrets:other\"")]
    [InlineData("\"defaults\":{\"retryMaxAttempts\":20}")]
    [InlineData("\"modelOverrides\":[{\"modelId\":\"claude-opus-5\",\"enabled\":false}]")]
    [InlineData("\"enabled\":false")]
    public async Task RepositoryProviderPolicyIsRejected(string property)
    {
        using var fixture = new Fixture();
        await fixture.WriteUserAsync(new ModelProviderCatalogConfiguration { Providers = [Provider("primary")] });
        await File.WriteAllTextAsync(fixture.Paths.RepositoryProviderCatalog, "{\"providers\":[{\"id\":\"primary\"," + property + "}]}", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(fixture.LoadAsync);
    }

    private static AnthropicProviderConfiguration Provider(string id)
    {
        return new AnthropicProviderConfiguration { Id = id, Name = "Anthropic " + id, SecretKeyReference = "secrets:models:" + id, Models = [] };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly IConfigurationRoot _configuration = new ConfigurationBuilder().Build();

        internal Fixture()
        {
            Directory = Path.Combine(Path.GetTempPath(), "threadsmith-anthropic-composition-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Paths = new ConfigurationPaths
            {
                RepositoryRoot = Directory,
                MachineConfiguration = Path.Combine(Directory, "machine.json"),
                UserConfiguration = Path.Combine(Directory, "user-config.json"),
                RepositoryConfigurationDirectory = Directory,
                RepositoryConfigurationDirectoryExistedAtStartup = true,
                RepositoryConfiguration = Path.Combine(Directory, "repo-config.json"),
                UserProviderCatalog = Path.Combine(Directory, "user-providers.json"),
                RepositoryProviderCatalog = Path.Combine(Directory, "repo-providers.json"),
                SessionConfiguration = Path.Combine(Directory, "session.json"),
                SecretsConfiguration = Path.Combine(Directory, "secrets.json"),
            };
            _http = new HttpClient(Handler);
        }

        internal string Directory { get; }

        internal ConfigurationPaths Paths { get; }

        internal ModelProviderRegistry Registry { get; } = new([new AnthropicProviderRegistration(), new OpenAiCompatibleProviderRegistration()]);

        internal DiscoveryHandler Handler { get; } = new();

        internal CapturingSecrets Secrets { get; } = new();

        public void Dispose()
        {
            _http.Dispose();
            (_configuration as IDisposable)?.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }

        internal Task WriteUserAsync(ModelProviderCatalogConfiguration descriptor)
        {
            return File.WriteAllTextAsync(Paths.UserProviderCatalog, JsonSerializer.Serialize(descriptor, Registry.CreateSerializerOptions()), TestContext.Current.CancellationToken);
        }

        internal Task<(EffectiveModelProviderCatalog? Ordinary, EffectiveModelProviderCatalog? Trusted)> LoadAsync()
        {
            return ModelComposition.LoadCatalogsAsync(_configuration, _configuration, Paths, new OpenAiCompatibleProviderRegistration(), Registry, NullLoggerFactory.Instance, Secrets, _http, TestContext.Current.CancellationToken);
        }
    }

    private sealed class CapturingSecrets : ISecretResolver
    {
        internal bool Available { get; set; } = true;

        internal List<SecretResolutionRequest> Requests { get; } = [];

        public Task<SecretResolutionResult> ResolveAsync(SecretResolutionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(new SecretResolutionResult { Value = Available ? new SecretValue("fixture-" + request.Reference.CanonicalName) : null });
        }
    }

    private sealed class DiscoveryHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        internal HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent("""{"data":[{"id":"claude-opus-5","display_name":"Fixture Claude","created_at":"2026-07-24T00:00:00Z","type":"model","max_input_tokens":200000,"max_tokens":32000,"capabilities":null}],"has_more":false,"first_id":"claude-opus-5","last_id":"claude-opus-5"}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
