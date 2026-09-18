namespace Threadsmith.Skills.Tests;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>The selected provider route survives nested invocation and host continuation with refreshed tool authority.</summary>
    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, true, false)]
    public async Task SkillWorkflow_TrustedModelRouteUsesMatchingCatalogAndSurvivesHostContinue(
        bool trustedRoute, bool pause, bool allowTool)
    {
        var request = PermissionPlan().Request with { Selector = "review", ModelUsesTrustedCatalog = !trustedRoute };
        var profileId = ModelProfileId.New();
        var effectiveProfile = RoutingProfile(profileId, "effective", 64_000, 1024, !trustedRoute);
        var trustedProfile = RoutingProfile(profileId, "trusted", 128_000, 2048, trustedRoute);
        var effectiveCatalog = new ConfiguredModelCatalog([effectiveProfile]);
        var trustedCatalog = new ConfiguredModelCatalog([trustedProfile]);
        var effective = new RoutingModelProvider("effective");
        var trusted = new RoutingModelProvider("trusted");
        var selectedProvider = trustedRoute ? trusted : effective;
        var selectedProfile = trustedRoute ? trustedProfile : effectiveProfile;
        var snapshots = new ConversationToolSnapshotStore();
        var tool = new RoutingProbeTool(snapshots);
        var registry = new ToolRegistry([tool]);
        var callerId = snapshots.Capture(
            request.SessionId,
            request.RunId,
            [registry.GetRegistration("permission_probe")],
            PermissionContext() with
            {
                ModelProfileId = profileId,
                ModelReasoningLevel = "medium",
                ModelUsesTrustedCatalog = trustedRoute,
            });
        request = request with { CallerToolSnapshotId = callerId };
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var verifier = new SkillPackageVerifier(new SkillTrustPolicySnapshot());
        var verified = await verifier.VerifyAsync(catalog.Resolve("review"));
        var procedure = Assert.Single(verified.Metadata.Workflow.Steps);
        var candidate = verified with
        {
            Metadata = verified.Metadata with
            {
                Requirements = verified.Metadata.Requirements with { RequiredTools = ["permission_probe"] },
                Workflow = verified.Metadata.Workflow with
                {
                    Steps = pause
                        ? [new SkillWorkflowStep { StepId = "ask", Kind = SkillWorkflowStepKind.AskUserInput }, procedure with { DependsOn = ["ask"] }]
                        : [procedure],
                },
            },
        };
        var sanitizer = new SecretOutputSanitizer();
        await using var events = new DomainEventStream();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance);
        var sessionContextCalls = 0;
        var runner = new ModelSkillProcedureRunner(
            effective,
            registry,
            pipeline,
            sanitizer,
            (_, _) =>
            {
                sessionContextCalls++;
                return Task.FromResult(PermissionContext() with
                {
                    AllowedToolIds = allowTool ? ["permission_probe"] : ["invoke_skill"],
                });
            },
            TestPromptLoader.Instance,
            effectiveCatalog,
            effective,
            snapshots: snapshots,
            trustedModelProvider: trusted,
            trustedCatalog: trustedCatalog,
            trustedProviderInstructionResolver: trusted);
        await using var workflow = new SkillWorkflowOrchestrator(
            new FixedCatalog([candidate]),
            new PassThroughVerifier(),
            new SkillCompatibilityEvaluator(registry, effectiveCatalog, "1.0.0", trustedModels: trustedCatalog),
            new SkillContentLoader(sanitizer, TestPromptLoader.Instance),
            new BoundedJsonSchemaValidator(),
            runner,
            TestPromptLoader.Instance,
            new InMemorySkillStateStore(),
            (_, _) => Task.FromResult(new SkillInvocationHostContext
            {
                Trust = RepositoryTrustLevel.TrustedRead,
                Phase = RunPhase.EvidenceCollection,
                ModelProfileId = profileId,
                ReasoningLevel = "none",
            }),
            events,
            snapshots);

        var result = await workflow.InvokeAsync(request);
        snapshots.Release(callerId);
        if (pause)
        {
            Assert.Equal(SkillInvocationStatus.AwaitingHost, result.Status);
            Assert.Empty(selectedProvider.Requests);
            var checkpoint = JsonSerializer.Deserialize<SkillWorkflowCheckpoint>(JsonSerializer.Serialize(result.Checkpoint));
            Assert.True(Assert.IsType<SkillWorkflowCheckpoint>(checkpoint).ModelUsesTrustedCatalog);
            result = await workflow.ContinueAsync(result.InvocationId, "{}");
        }

        Assert.Equal(SkillInvocationStatus.Completed, result.Status);
        Assert.Equal(trustedRoute, result.Checkpoint.ModelUsesTrustedCatalog);
        Assert.Equal(pause, sessionContextCalls > 0);
        Assert.Equal(2, selectedProvider.Requests.Count);
        Assert.Equal(2, selectedProvider.Preparations);
        Assert.Empty((trustedRoute ? effective : trusted).Requests);
        Assert.Equal(0, (trustedRoute ? effective : trusted).Preparations);
        Assert.All(selectedProvider.Requests, item =>
        {
            Assert.Equal(profileId, item.ResolvedProfileId);
            Assert.Equal(ReasoningLevel.Medium, item.ReasoningLevel);
            Assert.Equal(selectedProfile.EffectiveRequestOutputTokenReserve, item.MaximumOutputTokens);
            Assert.Equal(selectedProfile.Provider, item.ProviderInstructions?.Content);
            Assert.Equal(selectedProfile.Provider, item.Preparation?.WireDigest);
        });
        Assert.Equal(allowTool ? 1 : 0, tool.Contexts.Count);
        Assert.All(tool.Contexts, context =>
        {
            Assert.Equal(trustedRoute, context.Invocation.ModelUsesTrustedCatalog);
            Assert.Throws<InvalidOperationException>(() => snapshots.Resolve(
                context.Invocation.ModelVisibleToolSnapshotId!.Value,
                context.SessionId,
                context.RunId));
        });
    }

    private static ModelProfile RoutingProfile(ModelProfileId id, string provider, int contextWindow, int outputTokens, bool toolCalls) => new()
    {
        Id = id,
        Name = provider,
        Provider = provider,
        Endpoint = new Uri("https://example.test/" + provider),
        ModelId = provider,
        ContextWindow = contextWindow,
        MaximumOutputTokens = outputTokens,
        SensitiveDataPolicy = ModelSensitiveDataPolicy.Allowed,
        Capabilities = new ModelCapabilitySet { Streaming = true, ToolCalls = toolCalls, StructuredOutput = true },
    };

    private sealed class RoutingModelProvider(string route) : IModelProvider, IModelRequestPreparationResolver, IModelProviderInstructionResolver
    {
        private readonly PermissionModelProvider _inner = new() { FinalText = "{\"succeeded\":true,\"delivery\":\"inline\",\"response\":\"Completed.\"}" };

        public List<ModelStreamRequest> Requests => _inner.Requests;

        public int Preparations { get; private set; }

        public ModelProviderInstructions Resolve(ModelProfileId profileId) => new() { SectionId = route, Content = route };

        public ModelStreamRequest Prepare(ModelStreamRequest request)
        {
            Preparations++;
            return request with
            {
                Preparation = new ModelRequestPreparationResult { WireEstimate = request.WireEstimate!, WireDigest = route },
            };
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in _inner.StreamAsync(request, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private sealed class RoutingProbeTool(IConversationToolSnapshotStore snapshots) : Tool<PermissionProbeInput, PermissionProbeOutput>
    {
        public List<ToolExecutionContext> Contexts { get; } = [];

        public override ToolDefinition Definition { get; } = new PermissionProbeTool().Definition;

        public override Task<ToolExecution<PermissionProbeOutput>> ExecuteAsync(
            PermissionProbeInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            Contexts.Add(context);
            var captured = snapshots.ResolveContext(
                context.Invocation.ModelVisibleToolSnapshotId!.Value,
                context.SessionId,
                context.RunId);
            Assert.Equal(context.Invocation.ModelUsesTrustedCatalog, captured?.ModelUsesTrustedCatalog);
            return Task.FromResult(new ToolExecution<PermissionProbeOutput>(new PermissionProbeOutput(), []));
        }

        protected override void ValidateInput(PermissionProbeInput input)
        {
        }
    }
}
