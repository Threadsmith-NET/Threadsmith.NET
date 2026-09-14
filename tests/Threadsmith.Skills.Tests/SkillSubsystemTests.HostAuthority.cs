namespace Threadsmith.Skills.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Initial invocation uses active session authority and never silently changes the requested workspace.</summary>
    [Theory]
    [InlineData(RepositoryTrustLevel.UntrustedInspection, RunPhase.EvidenceCollection, false, "insufficient-repository-trust")]
    [InlineData(RepositoryTrustLevel.TrustedRead, RunPhase.Mutation, false, "phase-ineligible")]
    [InlineData(RepositoryTrustLevel.TrustedRead, RunPhase.EvidenceCollection, true, "workspace")]
    [InlineData(RepositoryTrustLevel.TrustedRead, RunPhase.Intake, false, null)]
    public async Task Workflow_InvokeUsesCurrentSessionAuthority(
        RepositoryTrustLevel currentTrust,
        RunPhase currentPhase,
        bool differentWorkspace,
        string? expectedDenial)
    {
        // Use the production compatibility evaluator and a host-only workflow so no model can mask admission errors.
        var catalog = new SkillCatalog([new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true)]);
        await catalog.RefreshAsync();
        var verified = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(catalog.Resolve("review"));
        var candidate = verified with
        {
            Metadata = verified.Metadata with
            {
                Requirements = verified.Metadata.Requirements with { RequiredTools = [], InheritAvailableTools = false },
                Workflow = new SkillWorkflowDefinition
                {
                    WorkflowId = "host-only",
                    Steps = [new SkillWorkflowStep { StepId = "ask", Kind = SkillWorkflowStepKind.AskUserInput }],
                },
            },
        };
        var workspaceId = WorkspaceId.New();
        var state = new InMemorySkillStateStore();
        await using var events = new DomainEventStream();
        await using var workflow = new SkillWorkflowOrchestrator(
            new FixedCatalog([candidate]),
            new PassThroughVerifier(),
            new SkillCompatibilityEvaluator(new ToolRegistry([]), new ConfiguredModelCatalog([]), "1.0.0"),
            new SkillContentLoader(new SecretOutputSanitizer(), TestPromptLoader.Instance),
            new BoundedJsonSchemaValidator(),
            new FixedProcedureRunner("{}"),
            TestPromptLoader.Instance,
            state,
            (_, _) => Task.FromResult(new SkillInvocationHostContext
            {
                WorkspaceId = workspaceId,
                Trust = currentTrust,
                Phase = currentPhase,
            }),
            events);
        var request = PermissionPlan().Request with
        {
            Selector = "review",
            WorkspaceId = differentWorkspace ? WorkspaceId.New() : workspaceId,
            Trust = RepositoryTrustLevel.FullyTrustedAutomation,
            Phase = RunPhase.EvidenceCollection,
        };

        if (expectedDenial is not null)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.InvokeAsync(request));
            Assert.Contains(expectedDenial, exception.Message, StringComparison.Ordinal);
            Assert.Null(await state.GetCheckpointAsync(request.InvocationId));
        }
        else
        {
            var result = await workflow.InvokeAsync(request);
            Assert.Equal(SkillInvocationStatus.AwaitingHost, result.Status);
            var checkpoint = await state.GetCheckpointAsync(request.InvocationId);
            Assert.NotNull(checkpoint);
            Assert.Equal(workspaceId, checkpoint.WorkspaceId);
            Assert.Equal(currentTrust, checkpoint.Trust);
            Assert.Equal(currentPhase, checkpoint.Phase);
        }
    }
}
