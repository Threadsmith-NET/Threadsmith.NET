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
}
