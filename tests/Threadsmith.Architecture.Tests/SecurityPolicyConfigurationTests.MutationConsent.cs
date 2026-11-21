namespace Threadsmith.Architecture.Tests;

using Microsoft.Data.Sqlite;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Persistence;
using Threadsmith.Telemetry;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Loading local consent cannot temporarily expose a broader trusted automatic mode.</summary>
    [Fact]
    public async Task MutationConsent_RebindKeepsReviewAllWhileStoreReadIsPending()
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_paths.UserConfiguration, """{"mutation":{"approvalPolicy":"alwaysTrustRepo"}}""", ct);
        var binding = new SessionPolicyBinding();
        binding.Publish(Load());
        var facts = new PausingConsentFacts();
        var service = new MutationApprovalPolicyService(policyBinding: binding, facts: facts);
        using var bindCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var bind = service.BindRepositoryAsync(_paths.RepositoryRoot, bindCancellation.Token);
        try
        {
            await WaitForConsentSignalAsync(facts.Started.Task.WaitAsync(ct), bind, "consent store read", ct);
            Assert.Equal(MutationApprovalPolicy.ReviewAll, service.CurrentPolicy);
            facts.Release.TrySetResult();
            await bind.WaitAsync(ct);
            Assert.Equal(MutationApprovalPolicy.ReviewAll, service.CurrentPolicy);
        }
        finally
        {
            facts.Release.TrySetResult();
            await CancelAndDrainConsentOperationAsync(bind, bindCancellation);
        }
    }

    /// <summary>Missing signals fail immediately when the owned operation has already finished.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MutationConsent_SignalWaitObservesEarlyCompletion(bool faulted)
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = faulted ? Task.FromException(new InvalidOperationException("fixture failure")) : Task.CompletedTask;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => WaitForConsentSignalAsync(
            signal.Task.WaitAsync(TestContext.Current.CancellationToken), operation.WaitAsync(TestContext.Current.CancellationToken), "fixture signal", TestContext.Current.CancellationToken));
        Assert.Contains(faulted ? "fixture failure" : "completed before fixture signal", error.Message, StringComparison.Ordinal);
    }

    private static async Task WaitForConsentSignalAsync(Task signal, Task operation, string description, CancellationToken cancellationToken)
    {
        // A watchdog for broken synchronization, not a product latency assertion.
        Task completed;
        try
        {
            completed = await Task.WhenAny(signal, operation).WaitAsync(cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"Timed out waiting for {description}; operation status: {operation.Status}.", exception);
        }

        if (completed == operation)
        {
            await operation.WaitAsync(cancellationToken);
            throw new InvalidOperationException($"Operation completed before {description} was observed.");
        }

        await signal.WaitAsync(cancellationToken);
    }

    private static async Task CancelAndDrainConsentOperationAsync(Task operation, CancellationTokenSource cancellation)
    {
        await cancellation.CancelAsync();
        try
        {
            await operation.WaitAsync(TestContext.Current.CancellationToken);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Expected termination of the operation owned by this fixture.
        }
    }

    private sealed class PausingConsentFacts : IRepositoryFactsStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RepositoryFacts?> GetAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<RepositoryFacts?>(null);

        public Task UpsertAsync(RepositoryFacts facts, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public async Task<MutationApprovalPolicy?> GetMutationPolicyAsync(string repositoryPath, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return MutationApprovalPolicy.ReviewAll;
        }
    }
}
