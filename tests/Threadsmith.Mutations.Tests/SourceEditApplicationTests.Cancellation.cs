namespace Threadsmith.Mutations.Tests;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Xunit;

public sealed partial class SourceEditApplicationTests
{
    /// <summary>Cancellation publishes durable effects and releases run waiters independently of another run's exact review.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledRunFinalizesWhileAnotherRunAwaitsApproval(bool earlierAppliedEdit)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var fixture = await EditFixture.CreateAsync(ct);
        var review = new TaskCompletionSource<MutationSetProposed>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new ConcurrentQueue<IDomainEvent>();
        await using var subscription = fixture.Events.Subscribe((item, _) =>
        {
            observed.Enqueue(item);
            if (item is MutationSetProposed { RequiredApproval: MutationApprovalLevel.EntireSet } proposed)
            {
                review.TrySetResult(proposed);
            }

            return Task.CompletedTask;
        });
        Task<SourceEditReceipt>? approvalEdit = null;
        var provider = new CancellationEditProvider(async (runId, cancellationToken) =>
        {
            if (earlierAppliedEdit)
            {
                var applied = await fixture.Application.HandleAsync(fixture.Replace("Example", "Changed", false) with { RunId = runId }, cancellationToken);
                Assert.Equal(SourceEditStatus.Applied, applied.Status);
            }

            approvalEdit = fixture.Application.HandleAsync(fixture.Replace(earlierAppliedEdit ? "Changed" : "Example", "OtherRun", true), ct);
            await review.Task.WaitAsync(cancellationToken);
            var blockedEdit = fixture.Application.HandleAsync(fixture.Replace("Example", "NeverApplied", false) with { RunId = runId }, cancellationToken);
            Assert.False(blockedEdit.IsCompleted);
            waiting.TrySetResult();
            await blockedEdit;
        });
        var sessions = new SessionApplication(
            fixture.Events,
            provider,
            UnboundedBudget.Instance,
            new SecretOutputSanitizer(),
            NullLogger<SessionApplication>.Instance,
            correctiveMessages: new CorrectiveMessageFactory(TestPromptLoader.Instance),
            prompts: TestPromptLoader.Instance,
            sourceEdits: fixture.Application);
        sessions.RegisterRestoredSession(fixture.SessionId);
        var run = await sessions.HandleAsync(new SubmitRequestCommand(fixture.SessionId, "Edit source."), ct);
        try
        {
            await waiting.Task.WaitAsync(ct);
            Assert.True(await sessions.HandleAsync(new CancelRunCommand(fixture.SessionId, run), ct));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sessions.HandleAsync(new WaitForRunCommand(run), ct));

            Assert.False(approvalEdit!.IsCompleted);
            Assert.Contains(observed.OfType<RunCompleted>(), item => item.RunId == run && !item.Succeeded);
            var outcome = await fixture.Store.GetOutcomeAsync(run, ct);
            Assert.NotNull(outcome);
            Assert.Equal(ExecutionCheckpointPhase.Cancelled, outcome.Status);
            Assert.Equal(earlierAppliedEdit ? ["Example.cs"] : Array.Empty<string>(), outcome.ChangedFiles);
            Assert.Null(outcome.FinalDiff);
            Assert.False(outcome.RollbackAvailable);
            Assert.Contains(outcome.ResidualRisks, risk => risk.Contains("were not verified", StringComparison.Ordinal));
            Assert.Equal(earlierAppliedEdit ? 1 : 0, fixture.Commits.Count);
        }
        finally
        {
            if (review.Task.IsCompletedSuccessfully)
            {
                var proposed = await review.Task;
                await fixture.Application.HandleAsync(new RejectSourceEditCommand(fixture.SessionId, proposed.MutationSetId), ct);
                if (approvalEdit is not null)
                {
                    await approvalEdit;
                }
            }
        }
    }

    private sealed class CancellationEditProvider : IModelProvider
    {
        private readonly Func<RunId, CancellationToken, Task> _edit;

        public CancellationEditProvider(Func<RunId, CancellationToken, Task> edit) => _edit = edit;

        public async IAsyncEnumerable<ModelChunk> StreamAsync(ModelStreamRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await _edit(request.RunId, cancellationToken);
            yield return new() { Text = "Done.", FinishReason = ModelFinishReason.Stop };
        }
    }
}
