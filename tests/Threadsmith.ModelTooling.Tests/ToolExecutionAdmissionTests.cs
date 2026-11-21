namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Xunit;

/// <summary>Checks cancellation while clarification re-entry waits for source admission.</summary>
public static class ToolExecutionAdmissionTests
{
    /// <summary>Either execution owner can cancel re-entry without waiting for the competing lease.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public static async Task ClarificationReentryObservesBothCancellationTokens(bool cancelNestedSkill)
    {
        // Arrange: the nested skill and enclosing tool have independent cancellation owners.
        var testCancellation = TestContext.Current.CancellationToken;
        using var nestedCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        var limiter = new ToolSourceConcurrencyLimiter();
        var source = new ToolConcurrencyScope(new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "fixture"), "INVOKE_SKILL");
        var revalidating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validations = 0;
        using var admission = new ToolExecutionAdmission(
            limiter,
            source,
            1,
            () =>
            {
                validations++;
                revalidating.TrySetResult();
            },
            executionCancellation.Token);
        await admission.AcquireAsync(testCancellation);
        IDisposable? competitor = null;

        // Act: take the released slot before answering, so re-entry must wait for admission.
        var resuming = UserInteractionScope.WaitAsync(
            async token =>
            {
                competitor = await limiter.AcquireAsync(source, 1, token);
                return true;
            },
            nestedCancellation.Token);
        try
        {
            await revalidating.Task.WaitAsync(testCancellation);
            Assert.NotNull(competitor);
            Assert.False(resuming.IsCompleted);
            var cancelled = cancelNestedSkill ? nestedCancellation : executionCancellation;
            var unaffected = cancelNestedSkill ? executionCancellation : nestedCancellation;
            await cancelled.CancelAsync();

            // Assert: cancellation finishes while the other invocation still owns its slot.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resuming.WaitAsync(testCancellation));
            Assert.False(unaffected.IsCancellationRequested);
            Assert.Equal(1, validations); // No post-acquisition validation or execution.
        }
        finally
        {
            competitor?.Dispose();
            await nestedCancellation.CancelAsync();
            await executionCancellation.CancelAsync();
            try
            {
                await resuming;
            }
            catch (OperationCanceledException)
            {
                // Join the deliberately cancelled re-entry before disposing its owner.
            }
        }

        // A cancelled waiter must not retain source admission after the competitor exits.
        using var next = await limiter.AcquireAsync(source, 1, testCancellation);
    }

    /// <summary>Cancellation during adapter admission reports no dispatch and prevents a later handoff.</summary>
    [Fact]
    public static async Task ExternalWriteCancellation_ClosesDispatchBeforeHandoff()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        var limiter = new ToolSourceConcurrencyLimiter();
        var source = new ToolConcurrencyScope(new ToolActivitySource(ToolActivitySourceKind.Extension, "fixture"), null);
        using var admission = new ToolExecutionAdmission(limiter, source, 1, static () => { }, executionCancellation.Token);
        await admission.AcquireAsync(testCancellation);
        var completion = new ToolSuspensionCompletion(null, null, "test", TimeProvider.System);
        var reachedAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdmission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handoff = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var pending = admission.AwaitExternalWriteAsync(
            async (markDispatched, _) =>
            {
                var dispatched = false;
                reachedAdmission.SetResult();
                try
                {
                    await releaseAdmission.Task.WaitAsync(testCancellation);
                    markDispatched();
                    dispatched = true;
                    return new ToolExecutionEnvelope(new object(), [], IsTruncated: false);
                }
                finally
                {
                    handoff.SetResult(dispatched);
                }
            },
            completion,
            executionCancellation.Token);

        try
        {
            Assert.Same(reachedAdmission.Task, await Task.WhenAny(reachedAdmission.Task, pending).WaitAsync(testCancellation));
            await executionCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(testCancellation));
            Assert.False(completion.ExternalWriteDispatched);
        }
        finally
        {
            releaseAdmission.SetResult();
        }

        Assert.False(await handoff.Task.WaitAsync(testCancellation));
    }

    /// <summary>Cancellation does not await a synchronous prefix or release its slot early.</summary>
    [Fact]
    public static async Task ExternalWriteCancellation_RetainsSourceUntilUnderlyingWorkFinishes()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(testCancellation);
        var limiter = new ToolSourceConcurrencyLimiter();
        var source = new ToolConcurrencyScope(new ToolActivitySource(ToolActivitySourceKind.Extension, "fixture"), null);
        using var admission = new ToolExecutionAdmission(limiter, source, 1, static () => { }, executionCancellation.Token);
        await admission.AcquireAsync(testCancellation);
        var completion = new ToolSuspensionCompletion(null, null, "test", TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefixExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var underlying = new TaskCompletionSource<ToolExecutionEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePrefix = new ManualResetEventSlim();
        using var inCallingFrame = new ThreadLocal<bool>();
        inCallingFrame.Value = true;
        var pending = admission.AwaitExternalWriteAsync(
            (markDispatched, _) =>
            {
                Assert.False(inCallingFrame.Value);
                markDispatched();
                entered.SetResult();
                try
                {
                    releasePrefix.Wait(testCancellation);
                }
                finally
                {
                    prefixExited.SetResult();
                }

                return underlying.Task.WaitAsync(testCancellation);
            },
            completion,
            executionCancellation.Token);
        inCallingFrame.Value = false;
        Task<IDisposable>? competitor = null;
        try
        {
            Assert.Same(entered.Task, await Task.WhenAny(entered.Task, pending).WaitAsync(testCancellation));
            await executionCancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(testCancellation));
            Assert.True(completion.ExternalWriteDispatched);
            Assert.False(prefixExited.Task.IsCompleted);

            competitor = limiter.AcquireAsync(source, 1, testCancellation).AsTask();
            Assert.False(competitor.IsCompleted);
            releasePrefix.Set();
            await prefixExited.Task.WaitAsync(testCancellation);
            Assert.False(competitor.IsCompleted);
        }
        finally
        {
            releasePrefix.Set();
            if (entered.Task.IsCompletedSuccessfully)
            {
                await prefixExited.Task.WaitAsync(testCancellation);
            }

            underlying.SetResult(new ToolExecutionEnvelope(new object(), [], IsTruncated: false));
            if (competitor is not null)
            {
                using var next = await competitor.WaitAsync(testCancellation);
            }
        }
    }
}
