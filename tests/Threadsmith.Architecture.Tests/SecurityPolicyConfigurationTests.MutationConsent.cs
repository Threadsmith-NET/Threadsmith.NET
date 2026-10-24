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
    /// <summary>The real loader, review suspension, writer and local store enforce per-repository consent.</summary>
    [Fact]
    public async Task MutationConsent_RealLoaderAndWriterRequireLocalChoiceAndRememberIt()
    {
        var ct = TestContext.Current.CancellationToken;
        const string request = """{"mutation":{"approvalPolicy":"alwaysTrustRepo"}}""";
        await File.WriteAllTextAsync(_paths.RepositoryConfiguration, request, ct);
        await File.WriteAllTextAsync(_paths.UserConfiguration, """{"editableRoots":["src/"]}""", ct);
        var policy = Load();
        Assert.True(policy.AllowedTools.IsDefault);
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        var connection = $"Data Source={Path.Combine(_root, "consent.db")};Pooling=False";
        var facts = new SqliteRepositoryFactsStore(connection);
        await facts.InitializeAsync(ct);
        var approval = new MutationApprovalPolicyService(policyBinding: binding, facts: facts);
        await approval.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(MutationApprovalPolicy.ReviewAll, approval.CurrentPolicy);

        await using var events = new DomainEventStream();
        await using var workspaces = new TransactionalWorkspaceCoordinator(events, mutationApprovalPolicy: approval);
        var session = SessionId.New();
        var workspaceId = WorkspaceId.New();
        var baseline = new WorkspaceBaseline(workspaceId, _paths.RepositoryRoot, DateTimeOffset.UtcNow, [], TrustLevel: RepositoryTrustLevel.TrustedMutation) { SecurityPolicy = policy };
        await workspaces.RegisterBaselineAsync(baseline, cancellationToken: ct);
        await new MigrationRunner(connection, DefaultMigrations.All).RunAsync(ct);
        await new SqliteEventStore(connection).InitializeAsync(ct);
        var checkpoints = new ExecutionCheckpointStore(connection);
        var sanitizer = new SecretOutputSanitizer();
        var artifacts = new ExecutionArtifactPublisher(new ArtifactStore(connection, Path.Combine(_root, "artifacts"), sanitizer));
        var app = new SourceEditApplication(workspaces, workspaces, workspaces, checkpoints, checkpoints, artifacts, events, TestPromptLoader.Instance, sanitizer, reviews: workspaces, previews: workspaces);
        var proposed = new TaskCompletionSource<MutationSetProposed>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = events.Subscribe((item, _) =>
        {
            if (item is MutationSetProposed mutation)
            {
                proposed.TrySetResult(mutation);
            }

            return Task.CompletedTask;
        });
        ApplySourceEditCommand Command(string path) => new(session, RunId.New(), workspaceId, Guid.NewGuid(), new MutationProposalSet { Rationale = "Consent regression", Mutations = [new CreateFileMutationProposal { RelativePath = path, Content = new() { Text = "safe fixture" } }] }, false);
        using var reviewCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var blocked = app.HandleAsync(Command("container/source/before.txt"), reviewCancellation.Token);
        try
        {
            await WaitForConsentSignalAsync(proposed.Task, blocked, "MutationSetProposed", ct);
            var pending = await proposed.Task;
            Assert.Equal(MutationApprovalLevel.EntireSet, pending.RequiredApproval);
            Assert.False(blocked.IsCompleted);
            Assert.False(File.Exists(Path.Combine(_paths.RepositoryRoot, "container/source/before.txt")));
            await app.HandleAsync(new RejectSourceEditCommand(session, pending.MutationSetId), ct);
            Assert.Equal(SourceEditStatus.Denied, (await blocked.WaitAsync(TimeSpan.FromMinutes(1), ct)).Status);
        }
        finally
        {
            await CancelAndDrainConsentOperationAsync(blocked, reviewCancellation);
        }

        await approval.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo, ct);
        foreach (var path in new[] { "container/source/after.txt", "tests/after.txt", "docs/after.txt", ".inbox/after.txt" })
        {
            using var applyCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var applying = app.HandleAsync(Command(path), applyCancellation.Token);
            try
            {
                Assert.Equal(SourceEditStatus.Applied, (await applying.WaitAsync(TimeSpan.FromMinutes(1), ct)).Status);
                Assert.Equal("safe fixture", await File.ReadAllTextAsync(Path.Combine(_paths.RepositoryRoot, path), ct));
            }
            finally
            {
                await CancelAndDrainConsentOperationAsync(applying, applyCancellation);
            }
        }

        // Restart and ordinary source changes do not revoke a local per-repository choice.
        var restarted = new MutationApprovalPolicyService(policyBinding: binding, facts: new SqliteRepositoryFactsStore(connection));
        await restarted.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, restarted.CurrentPolicy);
        var captured = restarted.SecurityPolicy;
        await restarted.SetPolicyAsync(MutationApprovalPolicy.ReviewAll, ct);
        Assert.Throws<MutationPolicyException>(() => restarted.ValidatePolicyGeneration(captured));
        await approval.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(MutationApprovalPolicy.ReviewAll, approval.CurrentPolicy);
        Assert.Equal(request, await File.ReadAllTextAsync(_paths.RepositoryConfiguration, ct));

        var copied = Directory.CreateDirectory(Path.Combine(_root, "copied")).FullName;
        Directory.CreateDirectory(Path.Combine(copied, ".threadsmith"));
        await File.WriteAllTextAsync(Path.Combine(copied, ".threadsmith", "config.json"), request, ct);
        await restarted.BindRepositoryAsync(copied, ct);
        Assert.Equal(MutationApprovalPolicy.ReviewAll, restarted.CurrentPolicy);
    }

    /// <summary>Local consent never raises an explicit trusted ceiling.</summary>
    [Theory]
    [InlineData("reviewAll", MutationApprovalPolicy.ReviewAll)]
    [InlineData("reviewRisky", MutationApprovalPolicy.ReviewRisky)]
    [InlineData("alwaysTrustRepo", MutationApprovalPolicy.AlwaysTrustRepo)]
    [InlineData("trustSession", MutationApprovalPolicy.ReviewAll)]
    public async Task MutationConsent_ExplicitTrustedCeilingWins(string ceiling, MutationApprovalPolicy expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(_paths.MachineConfiguration, "{\"enforced\":{\"mutation\":{\"approvalPolicy\":\"" + ceiling + "\"}}}", ct);
        var binding = new SessionPolicyBinding();
        binding.Publish(Load());
        var facts = new SqliteRepositoryFactsStore($"Data Source={Path.Combine(_root, "consent.db")};Pooling=False");
        await facts.InitializeAsync(ct);
        var service = new MutationApprovalPolicyService(policyBinding: binding, facts: facts);
        await service.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.Equal(MutationApprovalPolicy.ReviewAll, service.CurrentPolicy);
        await service.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo, ct);
        Assert.Equal(expected, service.CurrentPolicy);
        Assert.Equal(expected != MutationApprovalPolicy.AlwaysTrustRepo, service.RequiresApproval(new MutationRiskAssessment { HasDeletions = true }));
    }

    /// <summary>Session choices survive same-repository reload, expire on restart, and corrupt records fail closed.</summary>
    [Fact]
    public async Task MutationConsent_SessionTrustExpiresAndCorruptStoreFailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var binding = new SessionPolicyBinding();
        binding.Publish(Load());
        var connection = $"Data Source={Path.Combine(_root, "consent.db")};Pooling=False";
        var facts = new SqliteRepositoryFactsStore(connection);
        await facts.InitializeAsync(ct);
        var service = new MutationApprovalPolicyService(policyBinding: binding, facts: facts);
        await service.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        await service.SetPolicyAsync(MutationApprovalPolicy.TrustSession, ct);
        Assert.False(service.RequiresApproval(new MutationRiskAssessment()));
        Assert.Null(await facts.GetMutationPolicyAsync(_paths.RepositoryRoot, ct));
        await service.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.False(service.RequiresApproval(new MutationRiskAssessment()));
        var restarted = new MutationApprovalPolicyService(policyBinding: binding, facts: facts);
        await restarted.BindRepositoryAsync(_paths.RepositoryRoot, ct);
        Assert.True(restarted.RequiresApproval(new MutationRiskAssessment()));
        await service.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo, ct);
        var permissionPath = new RepositoryPermissionStore(Path.Combine(_root, "config.json")).GetPath(_paths.RepositoryRoot);
        await File.WriteAllTextAsync(permissionPath, "{}", ct);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.BindRepositoryAsync(_paths.RepositoryRoot, ct));
        Assert.True(service.RequiresApproval(new MutationRiskAssessment()));
    }

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
            await bind.WaitAsync(TimeSpan.FromMinutes(1), ct);
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
            completed = await Task.WhenAny(signal, operation).WaitAsync(TimeSpan.FromMinutes(1), cancellationToken);
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
            await operation.WaitAsync(TimeSpan.FromMinutes(1), CancellationToken.None);
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
