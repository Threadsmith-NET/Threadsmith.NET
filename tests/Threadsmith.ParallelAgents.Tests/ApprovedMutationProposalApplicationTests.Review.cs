namespace Threadsmith.ParallelAgents.Tests;

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Tools;
using Xunit;

public sealed partial class ApprovedMutationProposalApplicationTests
{
    /// <summary>A separately delegated read-only review of a prepared candidate cannot authorize its application.</summary>
    [Fact]
    public async Task PreparedProposal_ReadOnlyReviewerJoinsBeforeExactApprovalAndApplication()
    {
        await using var fixture = await Fixture.CreateAsync();
        var staged = await fixture.Application.HandleAsync(fixture.Command);
        var baseline = fixture.Workspaces.GetWorkspace(fixture.Command.WorkspaceId).Baseline;
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));

        await using var events = new DomainEventStream();
        var sanitizer = new TestSanitizer();
        var evidence = new EvidenceStore(events, sanitizer);
        var diffEvidenceId = EvidenceId.New();
        await evidence.AddAsync(new Evidence
        {
            EvidenceId = diffEvidenceId,
            SessionId = fixture.Command.SessionId,
            RunId = fixture.Command.RunId,
            Kind = EvidenceKind.SourceExcerpt,
            Content = staged.Preview.UnifiedDiff,
            Provenance = new EvidenceProvenance
            {
                Source = "approved-candidate-diff",
                SourcePath = "example.txt",
                BaselineIdentity = WorkspaceBaselineIdentity.Create(baseline),
            },
            CollectedAt = DateTimeOffset.UtcNow,
            Relevance = 1,
        });
        var profile = CreateProfile() with { IntendedWorkloadClasses = [WorkloadClass.Review] };
        var catalog = new ConfiguredModelCatalog([profile]);
        var selection = new AgentModelSelector(catalog, new DefaultModelSelectionPolicy(catalog));
        var plan = CreateCandidateReviewPlan(fixture, profile.Id, selection);
        var provider = new CandidateReviewModel(diffEvidenceId);
        var runner = CreateCandidateReviewer(plan, selection, provider, evidence, events, sanitizer);
        await using var scheduler = new AgentRunScheduler();
        var checkpoints = new CheckpointStore();
        var coordinator = new DelegationCoordinator(scheduler, checkpoints, events);

        var reviewed = await coordinator.StartAsync(plan, runner);

        Assert.Equal(DelegationCheckpointPhase.ReviewsJoined, reviewed.Phase);
        var outcome = Assert.Single(reviewed.ChildOutcomes);
        Assert.Equal(AgentRunStatus.Completed, outcome.Status);
        Assert.Equal(AgentRole.TestReviewer, outcome.Role);
        Assert.NotEqual(fixture.Command.RunId, outcome.ChildRunId);
        Assert.Equal(provider.Response, outcome.Response);
        Assert.Null(outcome.Findings);
        Assert.Null(outcome.Review);
        Assert.Equal(0, outcome.Usage.Corrections);
        Assert.Contains(diffEvidenceId, outcome.DeliveredEvidenceIds);
        Assert.Equal(0, outcome.Usage.Mutations);
        Assert.Equal(0, outcome.Usage.ToolCalls);
        Assert.Null(outcome.ChangeSet);
        Assert.Null(outcome.Implementation);
        var request = Assert.Single(provider.Requests);
        Assert.Equal(outcome.ChildRunId, request.RunId);
        Assert.Equal(WorkloadClass.Review, request.WorkloadClass);
        Assert.Empty(request.Tools);
        Assert.Contains(request.Messages, message => message.SectionId == "child-initial-evidence"
            && message.GetModelVisibleContent().Contains(staged.Preview.UnifiedDiff, StringComparison.Ordinal));
        Assert.False(plan.ImplementationAuthorized);
        Assert.Equal(AgentRunMode.ReadOnlyReview, Assert.Single(reviewed.Assignments).Mode);
        Assert.Empty(reviewed.Assignments[0].Policy.AllowedToolIds);
        Assert.Equal(1, fixture.Workspaces.StageCalls);
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));

        var workspace = fixture.Workspaces.GetWorkspace(fixture.Command.WorkspaceId);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.CommitAsync(
            staged.MutationSet.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.CommitAsync(
            staged.MutationSet.MutationSetId,
            new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = ApprovalId.New() }));
        Assert.Equal(OriginalText, await File.ReadAllTextAsync(fixture.FilePath));

        await workspace.CommitAsync(staged.MutationSet.MutationSetId, new MutationApproval
        {
            Level = MutationApprovalLevel.EntireSet,
            ApprovalId = staged.ApprovalId,
        });
        Assert.Equal(ChangedText, await File.ReadAllTextAsync(fixture.FilePath));
    }

    private static DelegationPlan CreateCandidateReviewPlan(
        Fixture fixture,
        ModelProfileId profileId,
        AgentModelSelector selection)
    {
        var acceptedAt = DateTimeOffset.UtcNow;
        var assignment = new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.TestReviewer,
            Mode = AgentRunMode.ReadOnlyReview,
            Objective = "Review the prepared candidate diff before exact approval.",
            Tasks = ["Identify a focused assertion for the changed text; do not apply mutations or run tests."],
            OutputSchema = AgentAssignment.ResponseSchema,
            StoppingCondition = "Return your review of the prepared candidate.",
            Deadline = acceptedAt.AddMinutes(1),
            Scope = new AgentAssignmentScope { Files = ["example.txt"], IsOwnershipProven = true },
            Budget = new AgentResourceBudget { WallTime = TimeSpan.FromMinutes(1) },
            Policy = new AgentPolicySnapshot
            {
                AllowedToolIds = [],
                TrustCeiling = RepositoryTrustLevel.TrustedRead,
                ModelProfileId = profileId,
                ReasoningLevel = nameof(ReasoningLevel.None),
                ModelSelectionRationale = "Review the approved candidate with the test model.",
                ContextPolicyVersion = "agent-context/2",
                ToolPolicyVersion = "delegate-agents-read-only/1",
            },
        };
        assignment = assignment with { Policy = selection.FreezePolicy(assignment) };
        return new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = fixture.Command.SessionId,
                ParentRunId = fixture.Command.RunId,
                WorkspaceId = fixture.Command.WorkspaceId,
                RepositoryIdentity = fixture.Root,
                BaselineIdentity = WorkspaceBaselineIdentity.Create(fixture.Workspaces.GetWorkspace(fixture.Command.WorkspaceId).Baseline),
            },
            Assignments = [assignment],
            ParentBudget = assignment.Budget,
            ImplementationAuthorized = false,
            AcceptedAt = acceptedAt,
        };
    }

    private static ModelExplorerAssignmentRunner CreateCandidateReviewer(
        DelegationPlan plan,
        AgentModelSelector selection,
        IModelProvider provider,
        IEvidenceStore evidence,
        IDomainEventStream events,
        IOutputSanitizer sanitizer)
    {
        var parent = new ToolExecutionContext(
            ToolInvocationId.New(),
            plan.Provenance.SessionId,
            plan.Provenance.ParentRunId,
            new ToolInvocationContext
            {
                WorkspaceId = plan.Provenance.WorkspaceId,
                RepositoryPath = plan.Provenance.RepositoryIdentity,
                TrustLevel = RepositoryTrustLevel.TrustedRead,
                ApprovedRoots = ["example.txt"],
                AllowedToolIds = [],
                RequestedBy = "host:approved-candidate-review",
            });
        var tools = new ToolInvocationPipeline(
            new ToolRegistry([]),
            new DefaultPolicyEngine(),
            new DenyApprovalPolicy(),
            events,
            sanitizer,
            NullLogger<ToolInvocationPipeline>.Instance,
            UnboundedBudget.Instance);
        return new ModelExplorerAssignmentRunner(
            new AgentContextAssembler(evidence),
            new AgentFindingAdmission(evidence),
            selection,
            provider,
            tools,
            evidence,
            new CandidateReviewInstructions(),
            sanitizer,
            new DelegateAgentsOptions(),
            parent,
            [],
            TestPromptLoader.Instance);
    }

    private sealed class CandidateReviewInstructions : IChildAgentInstructionProvider
    {
        public Task<RepositoryInstructionBundle> GetAsync(
            DelegationPlan plan,
            AgentAssignment assignment,
            ToolInvocationContext parentContext,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new RepositoryInstructionBundle
            {
                RepositoryRoot = parentContext.RepositoryPath,
                WorkingScope = "example.txt",
                Digest = "approved-candidate-review",
            });
        }
    }

    private sealed class CandidateReviewModel : IModelProvider
    {
        private readonly string _response;

        public CandidateReviewModel(EvidenceId evidenceId)
        {
            _response = JsonSerializer.Serialize(new
            {
                summary = "The prepared replacement needs a focused text assertion.",
                findings = new[]
                {
                    new
                    {
                        severity = "info",
                        confidence = 1.0,
                        title = "Assert the exact replacement after application.",
                        category = "validation",
                        path = "example.txt",
                        line = 1,
                        evidenceIds = new[] { evidenceId.Value.ToString("D") },
                        consequence = "A substring-only assertion would miss newline changes.",
                        recommendation = "Validate the complete resulting text after authorized application.",
                        expectedAssertion = "The file equals after followed by one newline.",
                    },
                },
                unresolvedQuestions = Array.Empty<string>(),
                coverageNotes = new[] { "Reviewed the staged diff only; no files changed and no tests executed." },
            });
        }

        public List<ModelStreamRequest> Requests { get; } = [];

        /// <summary>Gets the exact advisory response emitted by the test provider.</summary>
        public string Response => _response;

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            await Task.CompletedTask;
            yield return new ModelChunk
            {
                Output = new TextModelOutput(_response),
                Usage = new ModelUsage(11, 7),
            };
        }
    }
}
