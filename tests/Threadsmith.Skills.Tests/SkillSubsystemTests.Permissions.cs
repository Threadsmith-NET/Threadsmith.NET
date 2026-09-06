namespace Threadsmith.Skills.Tests;

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
    /// <summary>Verifies declared tools cannot replace host allow, deny, or deny-all policy.</summary>
    [Theory]
    [InlineData(null, false, false, true)]
    [InlineData("permission_probe", false, false, true)]
    [InlineData("PERMISSION_PROBE", false, false, true)]
    [InlineData("invoke_skill", false, false, false)]
    [InlineData("permission_probe", true, false, false)]
    [InlineData("permission_probe", false, true, false)]
    public async Task SkillProcedure_HostToolPolicyControlsAdvertisementAndExecution(
        string? allowedTool, bool denied, bool denyAll, bool expectedAllowed)
    {
        var context = PermissionContext() with
        {
            AllowedToolIds = allowedTool is null ? [] : ["invoke_skill", allowedTool],
            DeniedToolIds = denied ? ["PERMISSION_PROBE"] : [],
            DenyAllTools = denyAll,
        };
        var tool = new PermissionProbeTool();
        var model = new PermissionModelProvider();
        await using var events = new DomainEventStream();
        var runner = CreatePermissionRunner(() => context, tool, model, events);

        await runner.RunAsync(PermissionPlan(), PermissionStep(), 1, [], "{}");

        Assert.Equal(expectedAllowed ? 1 : 0, tool.Executions);
        Assert.Equal(expectedAllowed, model.Requests[0].Tools.Any(item => item.Name == "permission_probe"));
        Assert.Equal(expectedAllowed, model.Requests[0].RequiredCapabilities.ToolCalls);
        Assert.Equal(2, model.Requests.Count);
        if (!expectedAllowed)
        {
            Assert.Contains("denies", model.Requests[1].Input, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Verifies policy is refreshed after model dispatch and for the next advertised tool set.</summary>
    [Fact]
    public async Task SkillProcedure_PolicyTightensDuringModelResponse_BlocksPreviouslyAdvertisedTool()
    {
        var context = PermissionContext();
        var tool = new PermissionProbeTool();
        var model = new PermissionModelProvider
        {
            BeforeToolRequest = () => context = context with { AllowedToolIds = ["invoke_skill"] },
        };
        await using var events = new DomainEventStream();
        var runner = CreatePermissionRunner(() => context, tool, model, events);

        await runner.RunAsync(PermissionPlan(), PermissionStep(), 1, [], "{}");

        Assert.Single(model.Requests[0].Tools);
        Assert.Empty(model.Requests[1].Tools);
        Assert.Equal(0, tool.Executions);
        Assert.Contains("denies", model.Requests[1].Input, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Verifies restored tool declarations cannot undo policy tightened while the workflow was paused.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillWorkflow_RestoreUsesCurrentToolPolicy(bool continueHostAction)
    {
        var plan = PermissionPlan();
        var gate = new SkillWorkflowStep { StepId = "confirm", Kind = SkillWorkflowStepKind.AskUserInput };
        var step = PermissionStep() with { DependsOn = [gate.StepId] };
        var candidate = new SkillCatalogCandidate
        {
            Identity = plan.Package,
            Metadata = new SkillManifestMetadata
            {
                SkillId = plan.Package.SkillId,
                PackageId = plan.Package.PackageId,
                Version = plan.Package.Version,
                Publisher = plan.Package.Publisher,
                DisplayName = "Permission fixture",
                Description = "In-memory workflow permission fixture.",
                License = "Apache-2.0",
                Assets = [],
                Requirements = new SkillRequirementSet
                {
                    MinimumHostVersion = "1.0.0",
                    MaximumHostVersion = "2.0.0",
                    RequiredTools = plan.AvailableToolIds,
                },
                Workflow = new SkillWorkflowDefinition { WorkflowId = "permissions", Steps = [gate, step] },
            },
            Provenance = new SkillPackageProvenance
            {
                Scope = SkillScope.User,
                Source = "test",
                PackageRoot = AppContext.BaseDirectory,
                DiscoveredAt = DateTimeOffset.UtcNow,
            },
            Verification = SkillVerificationState.DigestAllowlisted,
            VerificationReason = "test verified package",
            Enabled = true,
        };
        var compatibility = new CompatibleEvaluator();
        var state = new InMemorySkillStateStore();
        var checkpoint = new SkillWorkflowCheckpoint
        {
            WorkflowId = SkillWorkflowId.New(),
            InvocationId = plan.Request.InvocationId,
            SessionId = plan.Request.SessionId,
            RunId = plan.Request.RunId,
            Package = plan.Package,
            Scope = SkillScope.User,
            InputJson = "{}",
            Trust = plan.Request.Trust,
            Phase = plan.Request.Phase,
            ModelProfileId = Assert.Single(compatibility.Evaluate(candidate, plan.Request).CompatibleModels),
            AvailableToolIds = plan.AvailableToolIds,
            EffectiveBudget = plan.EffectiveBudget,
            Status = continueHostAction ? SkillInvocationStatus.AwaitingHost : SkillInvocationStatus.Cancelled,
            NextAction = "restore fixture",
            RecordedAt = DateTimeOffset.UtcNow,
            Steps =
            [
                new SkillWorkflowStepResult
                {
                    StepId = gate.StepId,
                    Kind = gate.Kind,
                    Iteration = 1,
                    OutputJson = "{}",
                    RecordedAt = DateTimeOffset.UtcNow,
                    HostAction = continueHostAction
                        ? new SkillHostActionProposal
                        {
                            Kind = SkillHostActionKind.AskUserInput,
                            StepId = gate.StepId,
                            PayloadJson = "{}",
                        }
                        : null,
                },
            ],
        };
        await state.SaveCheckpointAsync(checkpoint, expectedVersion: null);
        var context = PermissionContext() with { AllowedToolIds = ["invoke_skill"] };
        var tool = new PermissionProbeTool();
        var model = new PermissionModelProvider();
        await using var events = new DomainEventStream();
        await using var orchestrator = new SkillWorkflowOrchestrator(
            new FixedCatalog([candidate]),
            new PassThroughVerifier(),
            compatibility,
            new SkillContentLoader(new SecretOutputSanitizer()),
            new BoundedJsonSchemaValidator(),
            CreatePermissionRunner(() => context, tool, model, events),
            TestPromptLoader.Instance,
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext { Trust = context.TrustLevel, Phase = plan.Request.Phase }),
            events);

        var result = continueHostAction
            ? await orchestrator.ContinueAsync(checkpoint.InvocationId, "{}")
            : await orchestrator.ResumeAsync(checkpoint.InvocationId);

        Assert.Equal(SkillInvocationStatus.Completed, result.Status);
        Assert.Equal(0, tool.Executions);
        Assert.Empty(model.Requests[0].Tools);
        Assert.Contains("denies", model.Requests[1].Input, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(checkpoint.Generation + 1, result.Checkpoint.Generation);
    }

    private static ModelSkillProcedureRunner CreatePermissionRunner(
        Func<ToolInvocationContext> context, PermissionProbeTool tool, PermissionModelProvider model, IDomainEventStream events)
    {
        var registry = new ToolRegistry([tool]);
        var sanitizer = new SecretOutputSanitizer();
        var pipeline = new ToolInvocationPipeline(
            registry,
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance);
        return new ModelSkillProcedureRunner(
            model,
            registry,
            pipeline,
            sanitizer,
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(context());
            },
            TestPromptLoader.Instance);
    }

    private static ToolInvocationContext PermissionContext() => new()
    {
        RepositoryPath = AppContext.BaseDirectory,
        TrustLevel = RepositoryTrustLevel.TrustedRead,
        RequestedBy = "permission-test",
    };

    private static SkillWorkflowStep PermissionStep() => new()
    {
        StepId = "inspect",
        Kind = SkillWorkflowStepKind.CollectEvidence,
    };

    private static SkillInvocationPlan PermissionPlan()
    {
        var request = new SkillInvocationRequest
        {
            InvocationId = SkillInvocationId.New(),
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Selector = "test-skill",
            InputJson = "{}",
            Trust = RepositoryTrustLevel.TrustedRead,
            Phase = RunPhase.EvidenceCollection,
            HostBudget = new SkillBudget { ModelTurns = 2, ToolCalls = 1 },
        };
        return new SkillInvocationPlan
        {
            Request = request,
            Package = PackageIdentity(),
            Scope = SkillScope.User,
            Verification = SkillVerificationState.DigestAllowlisted,
            Compatibility = new SkillCompatibilityResult { IsCompatible = true },
            ModelProfileId = ModelProfileId.New(),
            AvailableToolIds = ["permission_probe"],
            EffectiveBudget = request.HostBudget,
        };
    }

    private sealed class PermissionModelProvider : IModelProvider
    {
        public List<ModelStreamRequest> Requests { get; } = [];

        public Action? BeforeToolRequest { get; init; }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            if (Requests.Count == 1)
            {
                BeforeToolRequest?.Invoke();
                yield return new ModelChunk { Output = new ToolRequestModelOutput("permission_probe", "{}") };
            }
            else
            {
                yield return new ModelChunk { Text = "{\"summary\":\"ok\"}" };
            }
        }
    }

    private sealed record PermissionProbeInput;

    private sealed record PermissionProbeOutput;

    private sealed class PermissionProbeTool : Tool<PermissionProbeInput, PermissionProbeOutput>
    {
        public int Executions { get; private set; }

        public override ToolDefinition Definition { get; } = new()
        {
            Id = "permission_probe",
            DisplayName = "Permission probe",
            Version = "1",
            Description = "Counts permitted calls.",
            InputSchema = new ToolSchema(nameof(PermissionProbeInput), 1, "{\"type\":\"object\"}"),
            OutputSchema = new ToolSchema(nameof(PermissionProbeOutput), 1, "{\"type\":\"object\"}"),
            Timeout = TimeSpan.FromSeconds(1),
            MaximumOutputBytes = 1024,
        };

        public override Task<ToolExecution<PermissionProbeOutput>> ExecuteAsync(
            PermissionProbeInput input, ToolExecutionContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Executions++;
            return Task.FromResult(new ToolExecution<PermissionProbeOutput>(new PermissionProbeOutput(), []));
        }

        protected override void ValidateInput(PermissionProbeInput input)
        {
            ArgumentNullException.ThrowIfNull(input);
        }
    }
}
