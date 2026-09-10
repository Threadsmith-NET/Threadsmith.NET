namespace Threadsmith.AnthropicProvider.Tests;

using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Models.Anthropic;

/// <summary>Exercises native admission through the production configured-provider dispatcher.</summary>
public sealed class AnthropicConfiguredDispatchTests
{
    /// <summary>Only the request admitted by preparation can activate credentials and submit through the SDK.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StreamAsync_PreparedRequest_ChecksDigestBeforeSecretAndSubmission(bool changeRequest)
    {
        var profile = TestAnthropic.Profile();
        var configuration = new AnthropicProviderConfiguration
        {
            Id = "fixture-instance",
            Name = "Native fixture",
            SecretKeyReference = "secrets:anthropic",
            Models =
            [
                new AnthropicModelConfiguration
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    ModelId = "claude-test",
                    Compatibility = TestAnthropic.Compatibility(),
                    ContextWindow = profile.ContextWindow,
                    MaximumOutputTokens = profile.MaximumOutputTokens,
                    RequestOutputTokenReserve = profile.RequestOutputTokenReserve,
                    Capabilities = profile.Capabilities,
                    SupportedReasoningLevels = [ReasoningLevel.None],
                    DefaultReasoningLevel = ReasoningLevel.None,
                },
            ],
        };
        var catalog = new EffectiveModelProviderCatalog(
            new ModelProviderCatalogConfiguration { Providers = [configuration], DefaultProviderId = configuration.Id, DefaultModelId = profile.Id },
            new ModelProviderRegistry([new AnthropicProviderRegistration()]));
        var handler = new TestAnthropicHandler(TestAnthropic.TextStream());
        using var client = new HttpClient(handler);
        var secretResolutions = 0;
        var submissions = 0;
        var provider = new ConfiguredModelProvider(client, catalog, (_, _) =>
        {
            secretResolutions++;
            return Task.FromResult<string?>("resolved-fixture-key");
        });
        var admitted = provider.Prepare(TestAnthropic.Request() with { SubmissionObserver = () => submissions++ });
        Assert.NotNull(admitted.Preparation);
        Assert.True(admitted.Preparation.RequiresInitialInstructionPrefix);
        var digest = admitted.Preparation.WireDigest;
        if (changeRequest)
        {
            var changed = admitted with { Messages = [TestAnthropic.Message(ModelMessageRole.User, "current-user", "changed after admission")] };
            await Assert.ThrowsAsync<ModelProviderException>(() => TestAnthropic.CollectAsync(provider, changed));
            Assert.Equal(0, secretResolutions);
            Assert.Equal(0, submissions);
            Assert.Empty(handler.Requests);
        }
        else
        {
            var chunks = await TestAnthropic.CollectAsync(provider, admitted);
            Assert.Equal("hello", string.Concat(chunks.Select(chunk => chunk.Text)));
            Assert.Equal(1, secretResolutions);
            Assert.Equal(1, submissions);
            Assert.Single(handler.Requests);
        }

        Assert.Equal(digest, admitted.Preparation.WireDigest);
    }
}
