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

    /// <summary>Compaction prepares its own receiving-model assets without changing parent selection.</summary>
    [Fact]
    public async Task Compaction_UsesItsOwnModelForAllPromptAssetsAndRestoresParentScope()
    {
        using var fixture = await OverrideFixture.CreateAsync();
        string[] names =
        [
            PromptFileNames.ContextActiveTurnCompactionSystem,
            PromptFileNames.ContextActiveTurnCompactionInitial,
            PromptFileNames.ContextActiveTurnCompactionOutputContract,
        ];
        foreach (var name in names)
        {
            await fixture.WriteAsync(true, fixture.FirstModel, name, $"parent-A-{name}");
            await fixture.WriteAsync(true, fixture.SecondModel, name, $"compactor-B-{name}");
        }

        await fixture.WriteAsync(true, fixture.SecondModel, PromptFileNames.ProviderOpenAiCodexInstructions, "current provider instructions B");

        await fixture.ReloadAsync();
        using var parentScope = fixture.Loader.BeginModelScope(fixture.FirstModel);
        var provider = new ModelActiveTurnCompactionCandidateProvider(
            new FakeModelProvider(new ScriptedSession()),
            new ActiveTurnCompactionPolicy(),
            fixture.Loader);

        var prepared = provider.PrepareCandidate(new ActiveTurnCompactionRequest
        {
            RunId = RunId.New(),
            ProfileId = fixture.FirstModel,
            CandidateProfile = new ActiveTurnCompactionCandidateProfile
            {
                ProfileId = fixture.SecondModel,
                ContextWindowTokens = 128000,
                OutputReserveTokens = 16384,
                ReasoningLevel = ReasoningLevel.None,
                SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
                Cost = new ModelCostMetadata(),
                ProviderInstructions = new ModelProviderInstructions
                {
                    SectionId = "provider-instructions",
                    PromptFileName = PromptFileNames.ProviderOpenAiCodexInstructions,
                    Content = "stale provider text from before reload",
                },
            },
            FrozenContextIdentity = "override-test",
            TaskObjective = "Preserve the investigation.",
            AcceptanceIntent = [],
            EligiblePrefix =
            [
                new ActiveTurnContinuationGroup
                {
                Sequence = 1,
                CompletedModelRound = 1,
                Messages = [new ModelMessage { Role = ModelMessageRole.User, SectionId = "source", Content = [new ModelContentPart { Content = "A fact." }] }],
                Sources = [],
                FilesRead = [],
                FilesChanged = [],
                EstimatedTokens = 10,
                WasDeliveredVerbatim = true,
                },
            ],
            SelectionConstraints = new ModelSelectionConstraints(),
            ProfileContextWindowTokens = 128000,
            ProfileOutputReserveTokens = 16384,
            BeforeInputTokens = 50000,
            PressureTargetTokens = 40000,
        }).PreparedRequest;

        Assert.NotNull(prepared);
        Assert.Equal(fixture.SecondModel, prepared.ResolvedProfileId);
        var system = Assert.Single(prepared.Messages, message => message.Role == ModelMessageRole.System).GetModelVisibleContent();
        Assert.All(names, name => Assert.Contains($"compactor-B-{name}", system, StringComparison.Ordinal));
        Assert.DoesNotContain("parent-A-", system, StringComparison.Ordinal);
        Assert.Equal("current provider instructions B", prepared.ProviderInstructions?.Content);
        Assert.True(prepared.WireEstimate?.WireInputTokens > 0);
        Assert.Equal($"parent-A-{names[0]}", fixture.Loader.Get(names[0]));
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
