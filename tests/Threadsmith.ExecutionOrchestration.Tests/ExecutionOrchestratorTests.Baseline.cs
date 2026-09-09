namespace Threadsmith.ExecutionOrchestration.Tests;

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Telemetry;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class ExecutionOrchestratorTests
{
    /// <summary>Independent sessions share current approved bytes, including a manual rollback, and retain later conflict detection.</summary>
    [Fact]
    public async Task NewExecution_AfterManualRollback_RefreshesBeforeProposalAndKeepsLaterConflicts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var path = Path.Combine(root, "src", "Example.cs");
        const string original = "class Example { }\r\n";
        const string edited = "class Example { private const string _test = \"tested!\"; }\r\n";
        try
        {
            await File.WriteAllTextAsync(path, original);
            var fixture = CreateFixture();
            await using var events = fixture.Events;
            await using var workspaces = new TransactionalWorkspaceCoordinator(events);
            var baseline = fixture.StartRequest.Baseline with
            {
                RepositoryPath = root, ApprovedRoots = ["src"],
                Files = [new WorkspaceFileHash("src/Example.cs", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original))), Encoding.UTF8.GetByteCount(original))],
            };
            await workspaces.RegisterBaselineAsync(baseline);
            var proposals = new CurrentBaselineProposal(workspaces, original, edited);
            var orchestrator = new ExecutionOrchestrator(
                proposals,
                workspaces,
                fixture.BaselineHandler,
                fixture.ValidationHandler,
                workspaces,
                fixture.Checkpoints,
                fixture.Artifacts,
                events,
                new SecretOutputSanitizer(),
                NullLogger<ExecutionOrchestrator>.Instance,
                new CorrectiveMessageFactory(TestPromptLoader.Instance));

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var request = fixture.StartRequest with
                {
                    SessionId = SessionId.New(), RunId = RunId.New(), Baseline = workspaces.GetWorkspace(baseline.WorkspaceId).Baseline,
                };
                request = request with
                {
                    ValidationRequest = request.ValidationRequest with
                    {
                        SessionId = request.SessionId, RunId = request.RunId, Baseline = request.Baseline,
                    },
                };
                var pending = await orchestrator.StartAsync(request);
                Assert.Equal(ExecutionCheckpointPhase.MutationApprovalPending, pending.Phase);
                Assert.Equal(original, proposals.Source);
                var review = await workspaces.HandleAsync(new GetMutationReviewCommand(
                    request.SessionId,
                    pending.MutationSetId ?? throw new InvalidOperationException("Missing staged mutation.")));
                Assert.False(review.Conflicts.HasConflicts);
                Assert.Equal(original, await File.ReadAllTextAsync(path));
                var approval = new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = review.ApprovalId };
                if (attempt == 0)
                {
                    await workspaces.HandleAsync(new CommitMutationSetCommand(request.SessionId, review.MutationSet.MutationSetId, approval));
                    await workspaces.PromoteBaselineAsync(baseline.WorkspaceId, ["src/Example.cs"]);
                    Assert.Equal(edited, await workspaces.GetWorkspace(baseline.WorkspaceId).ReadBaselineTextAsync("src/Example.cs"));
                    await File.WriteAllTextAsync(path, original); // User's manual rollback before a fresh session.
                }
                else
                {
                    await File.WriteAllTextAsync(path, "// newer user edit\r\n" + original);
                    await Assert.ThrowsAsync<WorkspaceConflictException>(() => workspaces.HandleAsync(
                        new CommitMutationSetCommand(request.SessionId, review.MutationSet.MutationSetId, approval)));
                    Assert.Equal("// newer user edit\r\n" + original, await File.ReadAllTextAsync(path));
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CurrentBaselineProposal(ITransactionalWorkspaceResolver workspaces, string expected, string replacement)
        : ICommandHandler<ProposeMutationSetCommand, StagedMutationSet>
    {
        public string? Source { get; private set; }

        public async Task<StagedMutationSet> HandleAsync(ProposeMutationSetCommand command, CancellationToken cancellationToken = default)
        {
            var workspace = workspaces.GetWorkspace(command.WorkspaceId);
            Source = await workspace.ReadBaselineTextAsync("src/Example.cs", cancellationToken);
            Assert.Equal(expected, Source);
            var set = new MutationSet
            {
                MutationSetId = MutationSetId.New(), SessionId = command.SessionId, RunId = command.RunId, WorkspaceId = command.WorkspaceId,
                BaselineCapturedAt = workspace.Baseline.CapturedAt, BaselineRevision = workspace.Baseline.GitRevision,
                Rationale = "Add a field.", IsWithinApprovedPlan = true,
                Mutations = [new Mutation { MutationId = MutationId.New(), Type = MutationType.ReplaceText, RelativePath = "src/Example.cs", StartOffset = 0, Length = expected.Length, ExpectedText = expected, ReplacementText = replacement }],
            };
            return await workspaces.StageAsync(set, cancellationToken);
        }
    }
}
