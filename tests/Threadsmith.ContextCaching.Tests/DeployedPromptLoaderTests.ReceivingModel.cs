namespace Threadsmith.ContextCaching.Tests;

using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Xunit;

public sealed partial class DeployedPromptLoaderTests
{
    /// <summary>Resolved fallback rebinds all current instructions before estimating the actual request.</summary>
    [Fact]
    public async Task ContextAssembly_ResolvedFallback_RebuildsPolicyToolDescriptionsAndIntelligenceBeforeAdmission()
    {
        using var fixture = await OverrideFixture.CreateAsync(sourceAssets: true);
        var secondPolicy = "policy-B" + new string('p', 8192);
        var secondDescription = "tool-B" + new string('t', 1024);
        foreach (var (model, label) in new[] { (fixture.FirstModel, "A"), (fixture.SecondModel, "B") })
        {
            await fixture.WriteAsync(true, model, PromptFileNames.SystemSystemPrompt, label == "B" ? secondPolicy : "policy-A");
            await fixture.WriteAsync(true, model, PromptFileNames.ToolListFilesDescription, label == "B" ? secondDescription : "tool-A");
            await fixture.WriteAsync(true, model, PromptFileNames.ContextRepositoryIntelligenceCollection, $"intelligence-{label}");
        }

        await fixture.ReloadAsync();
        var sanitizer = new OverrideSanitizer();
        await using var events = new OverrideEvents();
        var assembler = new ContextAssembler(
            new EvidenceStore(events, sanitizer),
            new TokenEstimator(),
            new ContextPolicy(),
            new PromptAppendLoader(sanitizer),
            sanitizer,
            events,
            fixture.Loader,
            modelResolver: new FixedReceivingModel(fixture.SecondModel));

        var result = await assembler.AssembleAsync(
            new ContextAssemblyRequest
            {
                SessionId = SessionId.New(),
                RunId = RunId.New(),
                Task = new TaskSpecification("Inspect this request.", []),
                RepositoryPath = fixture.Repository,
                DefaultModelProfileId = fixture.FirstModel,
                ToolSchemas =
            [
                new ContextToolSchema("list_files", "tool-A", """{"type":"object","properties":{}}""")
                {
                    DescriptionResolver = () => fixture.Loader.Get(PromptFileNames.ToolListFilesDescription),
                },
            ],
                IntelligenceInstructionResolver = () => fixture.Loader.Get(PromptFileNames.ContextRepositoryIntelligenceCollection),
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(fixture.SecondModel, result.ModelResolution?.ProfileId);
        Assert.Contains("policy-B", result.Messages![0].GetModelVisibleContent(), StringComparison.Ordinal);
        Assert.DoesNotContain("policy-A", result.ModelInput, StringComparison.Ordinal);
        Assert.Contains("intelligence-B", result.ModelInput, StringComparison.Ordinal);
        Assert.DoesNotContain("intelligence-A", result.ModelInput, StringComparison.Ordinal);
        Assert.True(result.WireEstimate?.NativeToolTokens > 0);
        var expectedTools = ModelToolCanonicalizer.Canonicalize(
        [
            new ModelToolDefinition
            {
                Name = "list_files",
                Description = secondDescription,
                ArgumentsJsonSchema = """{"type":"object","properties":{}}""",
            },
        ]);
        Assert.Equal(ModelToolCanonicalizer.ComputeDigest(expectedTools), result.ToolInventoryDigest);
        var expectedEstimate = ModelWireEstimator.Estimate(
            result.Messages,
            expectedTools,
            ToolTransportMode.Native,
            Math.Min(3, result.Messages.Count),
            result.ModelResolution!.EffectiveRequestOutputTokenReserve,
            result.ProviderInstructions,
            fixture.Loader);
        Assert.Equal(expectedEstimate.WireInputTokens, result.WireEstimate!.WireInputTokens);
        Assert.Single(events.Published.OfType<ContextAssembled>());
    }

    private sealed class FixedReceivingModel : IModelResolver
    {
        private readonly ModelProfileId _profileId;

        public FixedReceivingModel(ModelProfileId profileId)
        {
            _profileId = profileId;
        }

        public int MaximumInputTokenBudget => 128000;

        public ModelResolution Resolve(
            WorkloadClass workloadClass,
            ModelCapabilitySet requiredCapabilities,
            ModelSelectionConstraints constraints,
            ModelProfileId? defaultModelProfileId = null)
        {
            return new ModelResolution(_profileId, 128000, 4096, [], [], []);
        }
    }

    private sealed class OverrideSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value;
    }

    private sealed class OverrideEvents : IDomainEventStream, IDomainEventSubscription
    {
        public List<IDomainEvent> Published { get; } = [];

        public Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Published.Add(domainEvent);
            return Task.CompletedTask;
        }

        public IDomainEventSubscription Subscribe(Func<IDomainEvent, CancellationToken, Task> handler, int capacity = 256) => this;

        public Task PublishCommittedBatchAsync(IReadOnlyList<IDomainEvent> domainEvents, Func<bool> tryCommit, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (tryCommit())
            {
                Published.AddRange(domainEvents);
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
