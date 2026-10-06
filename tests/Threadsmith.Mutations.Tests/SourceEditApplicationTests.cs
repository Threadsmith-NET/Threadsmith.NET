namespace Threadsmith.Mutations.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Exercises direct editing through the real writer and durable effect store.</summary>
public sealed partial class SourceEditApplicationTests
{
    /// <summary>Temporary compiler errors apply and durable retry identity prevents duplicate effects.</summary>
    [Fact]
    public async Task Edit_AppliesTemporaryCompilerErrors_AndReplayCannotWriteTwice()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var command = fixture.Replace("class Example { }", "class Example { Missing value;", requireReview: false);

        var first = await fixture.Application.HandleAsync(command, ct);
        var replay = await fixture.CreateApplication().HandleAsync(command, ct);
        var repair = await fixture.Application.HandleAsync(fixture.Replace("class Example { Missing value;", "class Example { }", false), ct);

        Assert.Equal(SourceEditStatus.Applied, first.Status);
        Assert.Equal(first.MutationSetId, replay.MutationSetId);
        Assert.Equal(SourceEditStatus.Applied, repair.Status);
        Assert.Equal(2, fixture.Commits.Count);
        Assert.Equal("class Example { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        Assert.Empty(await fixture.Store.GetUnresolvedEffectsAsync(RepositoryIdentity.Create(fixture.Repository), ct));
        var different = command with
        {
            Instructions = command.Instructions with { Rationale = "Different instructions cannot reuse an effect identity." },
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Application.HandleAsync(different, ct));
    }

    /// <summary>Delayed exact approval cannot overwrite an external edit.</summary>
    [Fact]
    public async Task ExactReview_PreservesExternalChangesDuringApprovalDelay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var review = new TaskCompletionSource<MutationSetProposed>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is MutationSetProposed proposed)
            {
                review.TrySetResult(proposed);
            }

            return Task.CompletedTask;
        });
        var edit = fixture.Application.HandleAsync(fixture.Replace("Example", "Changed", true), ct);
        var proposed = await review.Task.WaitAsync(ct);
        Assert.False(edit.IsCompleted);
        Assert.Equal(MutationApprovalLevel.EntireSet, proposed.RequiredApproval);
        await File.WriteAllTextAsync(fixture.SourcePath, "class External { }", ct);
        var consent = new AuthorizeSourceEditCommand(fixture.SessionId, proposed.MutationSetId, new() { Level = MutationApprovalLevel.EntireSet, ApprovalId = proposed.ApprovalId });
        var authorization = fixture.Application.HandleAsync(consent, ct);

#pragma warning disable VSTHRD003 // The test started both calls before resolving their shared review boundary.
        Assert.Equal(SourceEditStatus.Conflict, (await edit).Status);
        await Assert.ThrowsAsync<WorkspaceConflictException>(() => authorization);
#pragma warning restore VSTHRD003
        Assert.Equal("class External { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        Assert.Equal(0, fixture.Commits.Count);
    }

    /// <summary>A persisted denial is immutable across retries and does not reopen exact authorization.</summary>
    [Fact]
    public async Task RejectedEditReplay_DoesNotRequestReviewOrWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var reviews = 0;
        var review = new TaskCompletionSource<MutationSetProposed>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is MutationSetProposed proposed)
            {
                Interlocked.Increment(ref reviews);
                review.TrySetResult(proposed);
            }

            return Task.CompletedTask;
        });
        var command = fixture.Replace("Example", "Changed", true);
        var edit = fixture.Application.HandleAsync(command, ct);
        var proposed = await review.Task.WaitAsync(ct);
        Assert.True(await fixture.Application.HandleAsync(new RejectSourceEditCommand(fixture.SessionId, proposed.MutationSetId), ct));
#pragma warning disable VSTHRD003 // The test owns the pending edit resolved by its exact rejection.
        Assert.Equal(SourceEditStatus.Denied, (await edit).Status);
#pragma warning restore VSTHRD003
        Assert.Equal(SourceEditStatus.Denied, (await fixture.CreateApplication().HandleAsync(command, ct)).Status);
        Assert.Equal(1, reviews);
        Assert.Equal(0, fixture.Commits.Count);
        Assert.Empty(await fixture.Store.GetUnresolvedEffectsAsync(RepositoryIdentity.Create(fixture.Repository), ct));
        Assert.False((await fixture.Store.GetEffectAsync(command.EffectId, ct))!.HasWriteIntent);
    }

    /// <summary>Cancellation after the writer returns cannot erase an applied disk outcome.</summary>
    [Fact]
    public async Task CancellationAfterDiskCommit_ReconcilesAppliedOutcome()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        fixture.Commits.AfterCommit = cancellation.Cancel;

        var receipt = await fixture.Application.HandleAsync(fixture.Replace("Example", "Changed", false), cancellation.Token);

        Assert.Equal(SourceEditStatus.Applied, receipt.Status);
        Assert.Equal("class Changed { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        Assert.Equal(SourceEditStatus.Applied, (await fixture.Store.GetEffectAsync(receipt.EffectId, ct))!.Receipt!.Status);
        Assert.Equal(1, fixture.Commits.Count);
    }

    /// <summary>Restart reads exact intent identities and never replays an interrupted write.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedIntent_ReconcilesExactBytes_WithoutReplay(bool committed)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var command = fixture.Replace("Example", "Changed", false);
        var snapshot = new MutationEffectSnapshot(MutationSetId.New(), [MutationId.New()], [new("Example.cs", Hash("class Example { }"), Hash("class Changed { }"))]);
        var artifact = await fixture.Artifacts.PublishAsync(fixture.SessionId, "mutationEffectSnapshot", JsonSerializer.Serialize(snapshot), ct);
        var intent = new MutationEffectRecord
        {
            EffectId = command.EffectId, SessionId = command.SessionId, RunId = command.RunId, WorkspaceId = command.WorkspaceId,
            RepositoryIdentity = RepositoryIdentity.Create(fixture.Repository),
            RequestIdentity = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command.Instructions))),
            MutationSetId = snapshot.MutationSetId, SnapshotArtifact = artifact,
        };
        Assert.True(await fixture.Store.TryBeginEffectAsync(intent, ct));
        if (committed)
        {
            await File.WriteAllTextAsync(fixture.SourcePath, "class Changed { }", ct);
        }

        var recovered = await fixture.CreateApplication().HandleAsync(command, ct);

        Assert.Equal(committed ? SourceEditStatus.Applied : SourceEditStatus.NotApplied, recovered.Status);
        Assert.Equal(0, fixture.Commits.Count);
        Assert.Empty(await fixture.Store.GetUnresolvedEffectsAsync(RepositoryIdentity.Create(fixture.Repository), ct));
    }

    /// <summary>Protected paths fail before source capture or mismatch hints can expose their contents.</summary>
    [Theory]
    [InlineData(".env")]
    [InlineData(".threadsmith/secrets/key.txt")]
    [InlineData("sub/../.threadsmith/secrets/key.txt")]
    public async Task ProtectedPaths_AreRejectedBeforeReading(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var command = fixture.Replace("missing anchor", "replacement", false);
        var instructions = command.Instructions with
        {
            Mutations = [new ReplaceTextMutationProposal { RelativePath = path, ExpectedText = "missing anchor", ReplacementText = "replacement" }],
        };
        await Assert.ThrowsAsync<MutationPolicyException>(() => fixture.Application.HandleAsync(command with { Instructions = instructions }, ct));
        Assert.Equal(0, fixture.Commits.Count);
    }

    /// <summary>Cancelled or unsupported approval does not resolve the pending exact review.</summary>
    [Fact]
    public async Task InvalidAuthorization_DoesNotWrite_AndCancelledEditDiscardsStaging()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var review = new TaskCompletionSource<MutationSetProposed>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is MutationSetProposed proposed)
            {
                review.TrySetResult(proposed);
            }

            return Task.CompletedTask;
        });
        using var editCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var edit = fixture.Application.HandleAsync(fixture.Replace("Example", "Changed", true), editCancellation.Token);
        var proposed = await review.Task.WaitAsync(ct);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var consent = new AuthorizeSourceEditCommand(fixture.SessionId, proposed.MutationSetId, new() { Level = MutationApprovalLevel.EntireSet, ApprovalId = proposed.ApprovalId });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Application.HandleAsync(consent, cancelled.Token));
        var trial = consent with { Approval = consent.Approval with { Level = MutationApprovalLevel.ApplyThenAccept } };
        await Assert.ThrowsAsync<NotSupportedException>(() => fixture.Application.HandleAsync(trial, ct));
        Assert.False(edit.IsCompleted);
        await editCancellation.CancelAsync();
#pragma warning disable VSTHRD003 // The test owns the pending edit and cancellation boundary.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => edit);
#pragma warning restore VSTHRD003
        await Assert.ThrowsAsync<KeyNotFoundException>(() => fixture.Commits.HandleAsync(new(fixture.SessionId, proposed.MutationSetId, consent.Approval), ct));
        Assert.Equal("class Example { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
    }

    /// <summary>Repeated terminal recording failure blocks subsequent effects while preserving the applied receipt.</summary>
    [Fact]
    public async Task UnrecordedOutcome_BlocksFurtherWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var edits = fixture.CreateApplication(new FailingCompletionStore(fixture.Store));
        var first = await edits.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        Assert.Equal(SourceEditStatus.Applied, first.Status);
        Assert.False(first.DurableOutcomeRecorded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => edits.HandleAsync(fixture.Replace("Changed", "Another", false), ct));
        Assert.Equal(1, fixture.Commits.Count);
        Assert.Equal("class Changed { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        Assert.Single(await fixture.Store.GetUnresolvedEffectsAsync(RepositoryIdentity.Create(fixture.Repository), ct));
    }

    /// <summary>The registered adapter uses pipeline policy and durable tool-call identity.</summary>
    [Fact]
    public async Task Pipeline_UsesExactReviewWithoutGenericApproval_AndDeduplicatesReplay()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var tool = new SourceEditTool(fixture.Application, TestPromptLoader.Instance);
        var pipeline = new ToolInvocationPipeline(new ToolRegistry([tool]), new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, new SecretOutputSanitizer(), NullLogger<ToolInvocationPipeline>.Instance);
        var review = new TaskCompletionSource<MutationSetProposed>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is MutationSetProposed proposed)
            {
                review.TrySetResult(proposed);
            }

            return Task.CompletedTask;
        });
        var request = new ToolInvocationRequest
        {
            SessionId = fixture.SessionId, RunId = fixture.RunId, ToolId = "edit_source", InvocationKey = "call_1",
            ArgumentsJson = """{"rationale":"Change the type name","mutations":[{"relativePath":"Example.cs","type":"ReplaceText","expectedText":"Example","replacementText":"Changed"}]}""",
            Context = new()
            {
                RepositoryPath = fixture.Repository, WorkspaceId = fixture.WorkspaceId, TrustLevel = RepositoryTrustLevel.TrustedMutation,
                RequestedBy = "model", RequireApprovalToolIds = ["edit_source"],
            },
        };
        var invocation = pipeline.InvokeAsync(request, ct);
        var proposed = await review.Task.WaitAsync(ct);
        Assert.False(invocation.IsCompleted);
        var consent = new AuthorizeSourceEditCommand(fixture.SessionId, proposed.MutationSetId, new() { Level = MutationApprovalLevel.EntireSet, ApprovalId = proposed.ApprovalId });
        await fixture.Application.HandleAsync(consent, ct);
#pragma warning disable VSTHRD003 // The test resolves the review owned by this invocation.
        var result = await invocation;
#pragma warning restore VSTHRD003
        Assert.True(result.Succeeded, result.Error);
        var replay = await pipeline.InvokeAsync(request, ct);
        Assert.True(replay.Succeeded, replay.Error);
        Assert.Equal(1, fixture.Commits.Count);
    }

    /// <summary>Ordinary edits retain original evidence and publish one cumulative net diff with honest validation coverage.</summary>
    [Fact]
    public async Task SuccessiveEdits_RecordCumulativeNetDiff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        await fixture.Application.HandleAsync(fixture.Replace("Example", "Intermediate", false), ct);
        await fixture.Application.HandleAsync(fixture.Replace("Intermediate", "Final", false), ct);
        var outcome = await fixture.Application.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);
        Assert.NotNull(outcome);
        var diff = await fixture.Artifacts.ReadAsync(outcome.FinalDiff!, ct);
        Assert.Contains("-class Example { }", diff, StringComparison.Ordinal);
        Assert.Contains("+class Final { }", diff, StringComparison.Ordinal);
        Assert.DoesNotContain("Intermediate", diff, StringComparison.Ordinal);
        Assert.Null(outcome.Validation);
        Assert.Contains(outcome.ResidualRisks, risk => risk.Contains("Build and test results require explicit tool invocations", StringComparison.Ordinal));
        Assert.NotNull(await fixture.Store.GetOutcomeAsync(fixture.RunId, ct));
    }

    /// <summary>A later outside edit cannot be presented as part of the run's cumulative diff.</summary>
    [Fact]
    public async Task ExternalChangeOmitsCumulativeDiff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        await fixture.Application.HandleAsync(fixture.Replace("Example", "Changed", false), ct);
        await File.WriteAllTextAsync(fixture.SourcePath, "class Outside { }", ct);
        var outcome = await fixture.Application.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);
        Assert.NotNull(outcome);
        Assert.Null(outcome.FinalDiff);
        Assert.Contains(outcome.ResidualRisks, risk => risk.Contains("differs from this run", StringComparison.Ordinal));
    }

    /// <summary>Outside changes between host edits are not folded into a cumulative run diff.</summary>
    [Fact]
    public async Task InterleavedExternalChangeOmitsCumulativeDiff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        await fixture.Application.HandleAsync(fixture.Replace("Example", "First", false), ct);
        await File.WriteAllTextAsync(fixture.SourcePath, "class First { int External; }", ct);
        await fixture.Application.HandleAsync(fixture.Replace("First", "Second", false), ct);
        var outcome = await fixture.Application.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);
        Assert.Null(outcome!.FinalDiff);
        Assert.Contains(outcome.ResidualRisks, risk => risk.Contains("outside this run between edits", StringComparison.Ordinal));
        Assert.Contains("External", await File.ReadAllTextAsync(fixture.SourcePath, ct), StringComparison.Ordinal);
    }

    /// <summary>Successful writes whose baseline promotion fails cannot produce stale diff text.</summary>
    [Fact]
    public async Task FailedPromotionOmitsCumulativeDiff()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct, limits: new() { MaximumBaselineContentBytes = 20 });
        var applied = await fixture.Application.HandleAsync(fixture.Replace("Example", "MuchLongerThanTheBaselineLimit", false), ct);
        Assert.Equal(SourceEditStatus.Applied, applied.Status);
        Assert.Contains("baseline publication is unavailable", applied.Detail, StringComparison.Ordinal);
        var outcome = await fixture.Application.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);
        Assert.Null(outcome!.FinalDiff);
        Assert.Contains(outcome.ResidualRisks, risk => risk.Contains("baseline does not match", StringComparison.Ordinal));
    }

    /// <summary>Cumulative verification preserves per-edit safety bounds while supporting more endpoints across a run.</summary>
    [Fact]
    public async Task CumulativeVerificationChunksPerEditEndpointLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct, limits: new() { MaximumMutations = 1 });
        for (var index = 0; index < 3; index++)
        {
            var command = fixture.Replace("Example", "Unused", false);
            command = command with
            {
                Instructions = command.Instructions with
                {
                    Mutations = [new CreateFileMutationProposal { RelativePath = $"Added{index}.cs", Content = new() { Text = $"class Added{index} {{ }}" } }],
                },
            };
            Assert.Equal(SourceEditStatus.Applied, (await fixture.Application.HandleAsync(command, ct)).Status);
        }

        var outcome = await fixture.Application.RecordRunOutcomeAsync(fixture.SessionId, fixture.RunId, ExecutionCheckpointPhase.Completed, ct);
        Assert.Equal(3, outcome!.ChangedFiles.Count);
        Assert.NotNull(outcome.FinalDiff);
    }

    /// <summary>A case-only move followed by replacement commits the exact reviewed final bytes.</summary>
    [Fact]
    public async Task MoveThenReplace_CommitsReplacementBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        Assert.SkipWhen(!RepositoryPathPolicy.GetPathComparer(fixture.Repository).Equals("Example.cs", "example.cs"), "Requires case-insensitive repository paths.");
        var destination = "example.cs";
        var command = fixture.Replace("Example", "Changed", false);
        command = command with
        {
            Instructions = command.Instructions with
            {
                Mutations =
                [
                    new MoveFileMutationProposal { RelativePath = "Example.cs", DestinationRelativePath = destination },
                    new ReplaceTextMutationProposal { RelativePath = destination, ExpectedText = "Example", ReplacementText = "Changed" },
                ],
            },
        };
        var receipt = await fixture.Application.HandleAsync(command, ct);
        Assert.Equal(SourceEditStatus.Applied, receipt.Status);
        Assert.Equal("class Changed { }", await File.ReadAllTextAsync(Path.Combine(fixture.Repository, destination), ct));
        Assert.Equal(destination, Path.GetFileName(Assert.Single(Directory.EnumerateFiles(fixture.Repository))));
        Assert.Equal(SourceEditStatus.Applied, (await fixture.Store.GetEffectAsync(command.EffectId, ct))!.Receipt!.Status);
    }

    /// <summary>A differently cased source alias does not authorize a filename change.</summary>
    [Fact]
    public async Task SourceAlias_PreservesActualFileSpelling()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        Assert.SkipWhen(!RepositoryPathPolicy.GetPathComparer(fixture.Repository).Equals("Example.cs", "example.cs"), "Requires case-insensitive repository paths.");
        var command = fixture.Replace("Example", "Changed", false);
        command = command with
        {
            Instructions = command.Instructions with
            {
                Mutations = [new ReplaceTextMutationProposal { RelativePath = "example.cs", ExpectedText = "Example", ReplacementText = "Changed" }],
            },
        };
        var receipt = await fixture.Application.HandleAsync(command, ct);
        Assert.Equal(SourceEditStatus.Applied, receipt.Status);
        Assert.Equal("Example.cs", Path.GetFileName(Assert.Single(Directory.EnumerateFiles(fixture.Repository))));
        Assert.Equal("class Changed { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
    }

    /// <summary>An external edit after intent but before commit is a terminal non-write conflict, not uncertain recovery.</summary>
    [Fact]
    public async Task ConflictAfterIntent_DoesNotBlockNextEdit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var injected = false;
        await using var subscription = fixture.Events.Subscribe(async (item, token) =>
        {
            if (!injected && item is ExecutionSideEffectRecorded recorded && recorded.Kind == "source-edit" && recorded.State == ExecutionOperationState.Pending)
            {
                injected = true;
                await File.WriteAllTextAsync(fixture.SourcePath, "class External { }", token);
            }
        });
        var command = fixture.Replace("Example", "Changed", false);
        var conflict = await fixture.Application.HandleAsync(command, ct);
        Assert.Equal(SourceEditStatus.Conflict, conflict.Status);
        Assert.Equal("class External { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
        Assert.Empty(await fixture.Store.GetUnresolvedEffectsAsync(RepositoryIdentity.Create(fixture.Repository), ct));
        var replay = await fixture.Application.HandleAsync(command, ct);
        Assert.Equal(SourceEditStatus.Conflict, replay.Status);
        var next = await fixture.Application.HandleAsync(fixture.Replace("External", "Following", false), ct);
        Assert.Equal(SourceEditStatus.Applied, next.Status);
        Assert.Equal("class Following { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private sealed class EditFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly TransactionalWorkspaceCoordinator _workspaces;
        private readonly WorkspaceResourceLimits _limits;

        private EditFixture(string root, WorkspaceId workspaceId, DomainEventStream events, TransactionalWorkspaceCoordinator workspaces, ExecutionCheckpointStore store, IExecutionArtifactPublisher artifacts, WorkspaceResourceLimits limits)
        {
            _root = root;
            WorkspaceId = workspaceId;
            Events = events;
            _workspaces = workspaces;
            _limits = limits;
            Store = store;
            Artifacts = artifacts;
            Commits = new CountingCommits(workspaces);
            Application = CreateApplication();
        }

        public string Repository => Path.Combine(_root, "repo");

        public string SourcePath => Path.Combine(Repository, "Example.cs");

        public SessionId SessionId { get; } = SessionId.New();

        public RunId RunId { get; } = RunId.New();

        public WorkspaceId WorkspaceId { get; }

        public DomainEventStream Events { get; }

        public ExecutionCheckpointStore Store { get; }

        public IExecutionArtifactPublisher Artifacts { get; }

        public CountingCommits Commits { get; }

        public SourceEditApplication Application { get; }

        public static async Task<EditFixture> CreateAsync(CancellationToken ct, Func<DomainEventStream, ISemanticHostMutationAttribution?>? attributionFactory = null, WorkspaceResourceLimits? limits = null, MutationApprovalPolicy approvalPolicy = MutationApprovalPolicy.TrustSession)
        {
            var root = Directory.CreateTempSubdirectory("threadsmith-direct-edit-").FullName;
            var repo = Directory.CreateDirectory(Path.Combine(root, "repo")).FullName;
            await File.WriteAllTextAsync(Path.Combine(repo, "Example.cs"), "class Example { }", ct);
            var events = new DomainEventStream();
            var policy = new MutationApprovalPolicyService();
            await policy.SetPolicyAsync(approvalPolicy, ct);
            limits ??= new WorkspaceResourceLimits();
            var workspaces = new TransactionalWorkspaceCoordinator(events, mutationApprovalPolicy: policy, semanticMutationAttribution: attributionFactory?.Invoke(events), resourceLimits: limits);
            var workspaceId = WorkspaceId.New();
            var baseline = new WorkspaceBaseline(workspaceId, repo, DateTimeOffset.UtcNow, [new("Example.cs", Hash("class Example { }"), Encoding.UTF8.GetByteCount("class Example { }"))], TrustLevel: RepositoryTrustLevel.TrustedMutation);
            await workspaces.RegisterBaselineAsync(baseline, cancellationToken: ct);
            var connection = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
            await new MigrationRunner(connection, DefaultMigrations.All).RunAsync(ct);
            await new SqliteEventStore(connection).InitializeAsync(ct);
            var store = new ExecutionCheckpointStore(connection);
            var artifacts = new ExecutionArtifactPublisher(new ArtifactStore(connection, Path.Combine(root, "artifacts"), new SecretOutputSanitizer()));
            return new(root, workspaceId, events, workspaces, store, artifacts, limits);
        }

        public SourceEditApplication CreateApplication(IMutationEffectStore? effects = null, ISourceEditAnalyzer? analyzer = null, ISemanticRefreshCoordinator? refresh = null) => new(
            _workspaces,
            Commits,
            _workspaces,
            Store,
            effects ?? Store,
            Artifacts,
            Events,
            TestPromptLoader.Instance,
            new SecretOutputSanitizer(),
            limits: _limits,
            analyzer: analyzer,
            semanticRefresh: refresh,
            reviews: _workspaces,
            previews: _workspaces);

        public ApplySourceEditCommand Replace(string expected, string replacement, bool requireReview)
        {
            var instructions = new MutationProposalSet
            {
                Rationale = "Apply the requested source edit.",
                Mutations = [new ReplaceTextMutationProposal { RelativePath = "Example.cs", ExpectedText = expected, ReplacementText = replacement }],
            };
            return new(SessionId, RunId, WorkspaceId, Guid.NewGuid(), instructions, requireReview);
        }

        public async ValueTask DisposeAsync()
        {
            await _workspaces.DisposeAsync();
            await Events.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class UnexpectedApproval : IApprovalPolicy
    {
        public Task<bool> IsApprovedAsync(string action, ApprovalLevel level, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Exact edit review must replace generic approval.");
        }
    }

    private sealed class FailingCompletionStore : IMutationEffectStore
    {
        private readonly IMutationEffectStore _store;

        public FailingCompletionStore(IMutationEffectStore store)
        {
            _store = store;
        }

        public Task<bool> TryBeginEffectAsync(MutationEffectRecord record, CancellationToken cancellationToken = default) => _store.TryBeginEffectAsync(record, cancellationToken);

        public Task<MutationEffectRecord?> GetEffectAsync(Guid effectId, CancellationToken cancellationToken = default) => _store.GetEffectAsync(effectId, cancellationToken);

        public Task CompleteEffectAsync(Guid effectId, SourceEditReceipt receipt, CancellationToken cancellationToken = default) => throw new IOException("Simulated terminal store failure.");

        public Task<IReadOnlyList<MutationEffectRecord>> GetUnresolvedEffectsAsync(string repositoryIdentity, CancellationToken cancellationToken = default) => _store.GetUnresolvedEffectsAsync(repositoryIdentity, cancellationToken);
    }

    private sealed class CountingCommits : ICommandHandler<CommitMutationSetCommand, MutationCommitResult>
    {
        private readonly TransactionalWorkspaceCoordinator _workspaces;

        public CountingCommits(TransactionalWorkspaceCoordinator workspaces)
        {
            _workspaces = workspaces;
        }

        public int Count { get; private set; }

        public Action? AfterCommit { get; set; }

        public async Task<MutationCommitResult> HandleAsync(CommitMutationSetCommand command, CancellationToken cancellationToken = default)
        {
            Count++;
            var result = await _workspaces.HandleAsync(command, cancellationToken);
            AfterCommit?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }
}
