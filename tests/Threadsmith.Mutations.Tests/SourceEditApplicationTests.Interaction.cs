namespace Threadsmith.Mutations.Tests;

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Contracts;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Interaction.Presentation;
using Threadsmith.Interaction.Runs;
using Threadsmith.Interaction.Sessions;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SourceEditApplicationTests
{
    /// <summary>Interactive exact review resolves the ordinary edit tool without a second execution loop.</summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    [InlineData("cancel")]
    [InlineData("conflict")]
    public async Task Interaction_ExactReviewPreservesConversationOwnership(string decision)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct, approvalPolicy: MutationApprovalPolicy.ReviewAll);
        var edits = fixture.Application;
        var provider = new ReviewedEditProvider();
        var sessions = CreateReviewedSession(fixture, provider);
        var projections = new InMemoryProjectionStore();
        var dispatcher = new CommandDispatcher([sessions, edits]);
        var controller = new InteractionController(new InteractionPresenter(dispatcher, projections));
        await controller.OpenAsync("exact review", ct);
        var review = new TaskCompletionSource<MutationSetProposed>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            if (item is MutationSetProposed proposed)
            {
                review.TrySetResult(proposed);
            }
            else if (item is RunCompleted && !review.Task.IsCompleted)
            {
                review.TrySetException(new InvalidOperationException("Run ended before exact review. " + provider.LastRequest));
            }

            return Task.CompletedTask;
        });
        var run = await controller.SubmitAsync("Rename the type and add a field.", ct);
        try
        {
            _ = await Task.WhenAny(review.Task, provider.Continuation.Task).WaitAsync(ct);
            if (!review.Task.IsCompleted)
            {
                provider.Finish.TrySetResult();
            }

            Assert.True(review.Task.IsCompleted, provider.LastRequest);
            var proposed = await review.Task.WaitAsync(ct);
            var staged = await controller.LoadMutationReviewAsync(proposed.MutationSetId, ct);
            Assert.Equal(run, staged.MutationSet.RunId);
            Assert.Equal("class Example { }", await File.ReadAllTextAsync(fixture.SourcePath, ct));
            if (decision == "cancel")
            {
                Assert.True(await controller.CancelActiveRunAsync(ct));
                Assert.Null(controller.ActiveRunId);
            }
            else
            {
                if (decision == "deny")
                {
                    Assert.True(await controller.RejectSourceEditAsync(proposed.MutationSetId, ct));
                }
                else
                {
                    if (decision == "conflict")
                    {
                        await File.WriteAllTextAsync(fixture.SourcePath, "class External { }", ct);
                    }

                    var approval = new MutationApproval
                    {
                        Level = MutationApprovalLevel.SelectedMutations,
                        SelectedMutations = [staged.MutationSet.Mutations[0].MutationId],
                        ApprovalId = proposed.ApprovalId,
                    };
                    if (decision == "conflict")
                    {
                        await Assert.ThrowsAsync<WorkspaceConflictException>(() => controller.CommitMutationSetAsync(proposed.MutationSetId, approval, ct));
                    }
                    else
                    {
                        var committed = await controller.CommitMutationSetAsync(proposed.MutationSetId, approval, ct);
                        Assert.Equal(approval.SelectedMutations, committed.AppliedMutations);
                        Assert.False(committed.RequiresAcceptance);
                    }
                }

                await provider.Continuation.Task.WaitAsync(ct);
                Assert.Equal(run, controller.ActiveRunId);
                provider.Finish.TrySetResult();
                Assert.True(await controller.WaitForActiveRunAsync(ct));
                Assert.Null(controller.ActiveRunId);
            }

            Assert.Equal(
                decision switch
                {
                    "approve" => "class Changed { }",
                    "conflict" => "class External { }",
                    _ => "class Example { }",
                },
                await File.ReadAllTextAsync(fixture.SourcePath, ct));
            await Assert.ThrowsAsync<SourceEditReviewUnavailableException>(() => dispatcher.DispatchAsync(
                new GetMutationReviewCommand(controller.SessionId ?? throw new InvalidOperationException(), proposed.MutationSetId), ct));

            // Every terminal path releases the previous staged review, allowing an ordinary next request.
            provider.Finish.TrySetResult();
            _ = await controller.SubmitAsync("Report the current state.", ct);
            Assert.True(await controller.WaitForActiveRunAsync(ct));
        }
        finally
        {
            provider.Finish.TrySetResult();
            await controller.CancelActiveRunAsync(CancellationToken.None);
        }
    }

    /// <summary>A real pending edit transfers active-run input into the existing selection modal and resumes once.</summary>
    [Theory]
    [InlineData("approve")]
    [InlineData("deny")]
    [InlineData("escape")]
    public async Task Interaction_CoordinatorPresentsMutationSelectionAfterReleasingRunInput(string decision)
    {
        var token = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(token, approvalPolicy: MutationApprovalPolicy.ReviewAll);
        var provider = new ReviewedEditProvider();
        provider.Finish.TrySetResult();
        var sessions = CreateReviewedSession(fixture, provider);
        var projections = new InMemoryProjectionStore();
        await using var projectionSubscription = fixture.Events.Subscribe(projections.ApplyAsync);
        var dispatcher = new CommandDispatcher([sessions, fixture.Application]);
        var surface = new MutationReviewSurface(decision);
        var coordinator = new InteractionCoordinator(new InteractionPresenter(dispatcher, projections), fixture.Events, surface);

        await coordinator.RunAsync(modelStatus: "Fixture", cancellationToken: token);

        var selection = Assert.Single(surface.Selections);
        Assert.Contains("Mutation review", selection.Title, StringComparison.Ordinal);
        Assert.Equal(["Apply approved set", "Discard"], selection.Options.Select(option => option.Label));
        Assert.Contains("Example", selection.Details, StringComparison.Ordinal);
        Assert.Contains("Changed", selection.Details, StringComparison.Ordinal);
        Assert.All(surface.ComposerRequests, request => Assert.Equal(ComposerPurpose.Conversation, request.Purpose));
        Assert.Equal(decision == "approve" ? "class Changed { int Value; }" : "class Example { }", await File.ReadAllTextAsync(fixture.SourcePath, token));
        Assert.True(provider.Continuation.Task.IsCompletedSuccessfully);
    }

    private static SessionApplication CreateReviewedSession(EditFixture fixture, ReviewedEditProvider provider)
    {
        var edits = fixture.Application;
        var registry = new ToolRegistry([new SourceEditTool(edits, TestPromptLoader.Instance)]);
        var sanitizer = new SecretOutputSanitizer();
        var pipeline = new ToolInvocationPipeline(registry, new DefaultPolicyEngine(), new UnexpectedApproval(), fixture.Events, sanitizer, NullLogger<ToolInvocationPipeline>.Instance);
        var evidence = new EvidenceStore(fixture.Events, sanitizer);
        var assembler = new ContextAssembler(evidence, new TokenEstimator(), new ContextPolicy(), new PromptAppendLoader(sanitizer), sanitizer, fixture.Events, TestPromptLoader.Instance);
        return new SessionApplication(
            fixture.Events,
            provider,
            new ExecutionBudget(new BudgetDimensions(100_000, 10, TimeSpan.FromMinutes(1))),
            sanitizer,
            NullLogger<SessionApplication>.Instance,
            pipeline,
            (_, _) => Task.FromResult(new ToolInvocationContext
            {
                RepositoryPath = fixture.Repository,
                WorkspaceId = fixture.WorkspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedMutation,
                RequestedBy = "model",
            }),
            contextAssembler: assembler,
            evidenceStore: evidence,
            defaultModelProfileId: ModelProfileId.New(),
            toolRegistry: registry,
            prompts: TestPromptLoader.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            sourceEdits: edits);
    }

    private sealed class MutationReviewSurface : IInteractionSurface
    {
        private readonly string _decision;
        private MutationRunInput? _input;

        internal MutationReviewSurface(string decision) => _decision = decision;

        public InteractionSurfaceCapabilities Capabilities { get; } = new(SupportsRetainedActivity: true);

        public List<InteractionSelectionRequest> Selections { get; } = [];

        public List<ComposerRequest> ComposerRequests { get; } = [];

        public Task<InteractionInput> ReadComposerAsync(ComposerRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComposerRequests.Add(request);
            return Task.FromResult(new InteractionInput(true, ComposerRequests.Count == 1 ? "Rename the type and add a field." : "/quit", cancellationToken));
        }

        public Task<InteractionSelectionResult> SelectAsync(InteractionSelectionRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.NotNull(_input);
            Assert.True(_input.IsDisposed);
            Selections.Add(request);
            return Task.FromResult(_decision == "escape"
                ? new InteractionSelectionResult(null, IsCancelled: true)
                : new InteractionSelectionResult(request.Options[_decision == "approve" ? 0 : 1].Id));
        }

        public IActiveRunInputLease BeginActiveRunInput(TimeProvider timeProvider)
        {
            _input = new MutationRunInput();
            return _input;
        }

        public Task PresentAsync(PresentationBatch batch, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentSessionStatusAsync(SessionStatusSnapshot status, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PresentActivityUntilAsync(InteractionActivity activity, Task operation, CancellationToken cancellationToken = default) => operation.WaitAsync(cancellationToken);
    }

    private sealed class MutationRunInput : IActiveRunInputLease
    {
        private readonly TaskCompletionSource<ActiveRunInputSignal> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsDisposed { get; private set; }

        public Task<ActiveRunInputSignal> ReadAsync(CancellationToken cancellationToken = default) => _pending.Task.WaitAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _pending.TrySetCanceled();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReviewedEditProvider : IModelProvider
    {
        private int _requests;

        public TaskCompletionSource Continuation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string LastRequest { get; private set; } = string.Empty;

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LastRequest = request.Input;
            if (Interlocked.Increment(ref _requests) == 1)
            {
                yield return new ModelChunk
                {
                    Output = new ToolRequestModelOutput("edit_source", """
                        {"rationale":"Rename and add field","mutations":[
                        {"type":"ReplaceText","relativePath":"Example.cs","expectedText":"Example","replacementText":"Changed"},
                        {"type":"ReplaceText","relativePath":"Example.cs","expectedText":"{ }","replacementText":"{ int Value; }"}]}
                        """),
                    FinishReason = ModelFinishReason.ToolCalls,
                };
                yield break;
            }

            Continuation.TrySetResult();
            await Finish.Task.WaitAsync(cancellationToken);
            yield return new ModelChunk { Text = "The exact review outcome is recorded.", FinishReason = ModelFinishReason.Stop };
        }
    }
}
