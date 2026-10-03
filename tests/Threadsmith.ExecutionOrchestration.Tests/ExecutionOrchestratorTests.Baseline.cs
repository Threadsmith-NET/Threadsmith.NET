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
    /// <summary>Completion checks live contents, including after restoring durable execution state.</summary>
    [Theory]
    [InlineData("unchanged", false)]
    [InlineData("unchanged", true)]
    [InlineData("edited", false)]
    [InlineData("edited", true)]
    [InlineData("dependency", false)]
    [InlineData("dependency", true)]
    [InlineData("deleted", false)]
    [InlineData("deleted", true)]
    [InlineData("added", false)]
    [InlineData("added", true)]
    [InlineData("added-project", true)]
    [InlineData("excluded", false)]
    [InlineData("excluded", true)]
    public async Task CompleteObjective_VerifiesLiveBytesBeforeReusingValidation(string change, bool restart)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "src"));
        var path = Path.Combine(root, "src", "Example.cs");
        var dependency = Path.Combine(root, "src", "Dependency.cs");
        const string original = "class Example { }\n";
        const string edited = "class Example { int Value; }\n";
        try
        {
            await File.WriteAllTextAsync(path, original, cancellationToken);
            await File.WriteAllTextAsync(dependency, original, cancellationToken);
            var fixture = CreateFixture();
            await using var events = fixture.Events;
            await using var workspaces = new TransactionalWorkspaceCoordinator(events);
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(original)));
            var baseline = fixture.StartRequest.Baseline with
            {
                RepositoryPath = root,
                ApprovedRoots = ["src"],
                ProhibitedPaths = ["src/private/**"],
                Files = [new WorkspaceFileHash("src/Example.cs", hash, original.Length), new WorkspaceFileHash("src/Dependency.cs", hash, original.Length)],
            };
            await workspaces.RegisterBaselineAsync(baseline, cancellationToken: cancellationToken);
            var proposals = new CurrentBaselineProposal(workspaces, original, edited);
            ExecutionOrchestrator CreateOrchestrator() => new(
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
            var orchestrator = CreateOrchestrator();
            var request = fixture.StartRequest with
            {
                Baseline = baseline,
                AllowPlanContinuation = true,
                ValidationRequest = fixture.StartRequest.ValidationRequest with { Baseline = baseline },
            };
            var pending = await orchestrator.StartAsync(request, cancellationToken);
            var staged = await workspaces.HandleAsync(
                new GetMutationReviewCommand(request.SessionId, pending.MutationSetId!.Value),
                cancellationToken);
            var progress = await orchestrator.ContinueAsync(CreateContinuation(fixture, staged), cancellationToken);
            Assert.Equal(ExecutionCheckpointPhase.PlanContinuationPending, progress.Status);
            Assert.Single(fixture.ValidationHandler.Commands);
            var cachedBaseline = workspaces.GetWorkspace(baseline.WorkspaceId).Baseline;

            if (change is "added" or "added-project")
            {
                var newPath = Path.Combine(root, "src", change == "added" ? "New.cs" : "New.csproj");
                await File.WriteAllTextAsync(newPath, "invalid compilation input", cancellationToken);
            }
            else if (change == "excluded")
            {
                foreach (var directory in new[] { "src/obj", "src/bin", "src/.git", "src/private", "outside" })
                {
                    var fullDirectory = Path.Combine(root, directory);
                    Directory.CreateDirectory(fullDirectory);
                    await File.WriteAllTextAsync(Path.Combine(fullDirectory, "New.cs"), "invalid", cancellationToken);
                }
            }
            else if (change == "deleted")
            {
                File.Delete(path);
            }
            else if (change != "unchanged")
            {
                var changedPath = change == "dependency" ? dependency : path;
                var timestamp = File.GetLastWriteTimeUtc(changedPath);
                var content = await File.ReadAllTextAsync(changedPath, cancellationToken);
                // Preserve size and timestamp: only a live content check can detect this.
                await File.WriteAllTextAsync(changedPath, content.Replace('{', '?'), cancellationToken);
                File.SetLastWriteTimeUtc(changedPath, timestamp);
            }

            if (restart)
            {
                orchestrator = CreateOrchestrator();
            }

            Assert.Same(cachedBaseline, workspaces.GetWorkspace(baseline.WorkspaceId).Baseline);
            if (change is "unchanged" or "excluded")
            {
                var outcome = await orchestrator.CompleteObjectiveAsync(request.SessionId, request.RunId, cancellationToken);
                Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                    orchestrator.CompleteObjectiveAsync(request.SessionId, request.RunId, cancellationToken));
                Assert.Contains("fresh validation is required", error.Message, StringComparison.Ordinal);
                Assert.Null(await fixture.Checkpoints.GetOutcomeAsync(request.RunId, cancellationToken));
            }

            Assert.Single(fixture.ValidationHandler.Commands);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
                RepositoryPath = root,
                ApprovedRoots = ["src"],
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
                    SessionId = SessionId.New(),
                    RunId = RunId.New(),
                    Baseline = workspaces.GetWorkspace(baseline.WorkspaceId).Baseline,
                };
                request = request with
                {
                    ValidationRequest = request.ValidationRequest with
                    {
                        SessionId = request.SessionId,
                        RunId = request.RunId,
                        Baseline = request.Baseline,
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
                MutationSetId = MutationSetId.New(),
                SessionId = command.SessionId,
                RunId = command.RunId,
                WorkspaceId = command.WorkspaceId,
                BaselineCapturedAt = workspace.Baseline.CapturedAt,
                BaselineRevision = workspace.Baseline.GitRevision,
                Rationale = "Add a field.",
                IsWithinApprovedPlan = true,
                Mutations = [new Mutation { MutationId = MutationId.New(), Type = MutationType.ReplaceText, RelativePath = "src/Example.cs", StartOffset = 0, Length = expected.Length, ExpectedText = expected, ReplacementText = replacement }],
            };
            return await workspaces.StageAsync(set, cancellationToken);
        }
    }
}
