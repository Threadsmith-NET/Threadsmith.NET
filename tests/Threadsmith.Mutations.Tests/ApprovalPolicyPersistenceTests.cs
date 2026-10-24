namespace Threadsmith.Mutations.Tests;

using Threadsmith.Core;
using Threadsmith.Persistence;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Local user choices survive restarts without granting authority to repository files.</summary>
public sealed class ApprovalPolicyPersistenceTests
{
    /// <summary>Explicit local choices survive a new service without changing repository data.</summary>
    [Theory]
    [InlineData(MutationApprovalPolicy.ReviewAll)]
    [InlineData(MutationApprovalPolicy.ReviewRisky)]
    [InlineData(MutationApprovalPolicy.AlwaysTrustRepo)]
    public static async Task LocalChoice_RoundTripsWithoutChangingRepositoryConfiguration(MutationApprovalPolicy selected)
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = await fixture.OpenAsync();
        Assert.Equal(MutationApprovalPolicy.ReviewAll, service.CurrentPolicy);
        await service.SetPolicyAsync(selected, TestContext.Current.CancellationToken);
        Assert.Equal(selected, (await fixture.OpenAsync()).CurrentPolicy);
        Assert.Equal(Fixture.Request, await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken));
    }

    /// <summary>Session trust does not replace a persistent choice.</summary>
    [Fact]
    public static async Task SessionChoice_DoesNotReplaceSavedConsent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = await fixture.OpenAsync();
        await service.SetPolicyAsync(MutationApprovalPolicy.ReviewRisky, TestContext.Current.CancellationToken);
        await service.SetPolicyAsync(MutationApprovalPolicy.TrustSession, TestContext.Current.CancellationToken);
        Assert.Equal(MutationApprovalPolicy.TrustSession, service.CurrentPolicy);
        Assert.Equal(MutationApprovalPolicy.ReviewRisky, (await fixture.OpenAsync()).CurrentPolicy);
    }

    /// <summary>A failed revocation disables automation in the current process.</summary>
    [Fact]
    public static async Task FailedStoreSave_DisablesLiveAutomationAndDoesNotClaimPersistence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = await fixture.OpenAsync();
        await service.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(fixture.PermissionPath, "{}", TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.SetPolicyAsync(MutationApprovalPolicy.ReviewAll, TestContext.Current.CancellationToken));
        Assert.Equal(MutationApprovalPolicy.ReviewAll, service.CurrentPolicy);
        Assert.Equal(Fixture.Request, await File.ReadAllTextAsync(fixture.ConfigPath, TestContext.Current.CancellationToken));
    }

    /// <summary>A host without persistence cannot promise remembered consent.</summary>
    [Fact]
    public static async Task MissingStoreCannotRememberAutomation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var service = new MutationApprovalPolicyService(repositoryConfigurationPath: fixture.ConfigPath);
        await service.BindRepositoryAsync(fixture.Repository, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo, TestContext.Current.CancellationToken));
        Assert.Equal(MutationApprovalPolicy.ReviewAll, service.CurrentPolicy);
    }

    /// <summary>Discovery writes preserve consent and repository switches isolate it.</summary>
    [Fact]
    public static async Task DiscoveryUpdatesDoNotEraseConsentAndOtherRepositoryDoesNotInherit()
    {
        await using var first = await Fixture.CreateAsync();
        await using var second = await Fixture.CreateAsync();
        var service = await first.OpenAsync();
        await service.SetPolicyAsync(MutationApprovalPolicy.AlwaysTrustRepo, TestContext.Current.CancellationToken);
        var facts = new RepositoryFacts(WorkspaceId.New(), first.Repository, new RepositoryTrustState(first.Repository, RepositoryTrustLevel.TrustedMutation, DateTimeOffset.UtcNow), null, [], null);
        await first.Store.UpsertAsync(facts, TestContext.Current.CancellationToken);
        await service.BindRepositoryAsync(second.Repository, TestContext.Current.CancellationToken);
        Assert.Equal(MutationApprovalPolicy.ReviewAll, service.CurrentPolicy);
        await service.BindRepositoryAsync(first.Repository, TestContext.Current.CancellationToken);
        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, service.CurrentPolicy);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal const string Request = "{\"Mutation\":{\"ApprovalPolicy\":\"alwaysTrustRepo\",\"LargeDiffThreshold\":50},\"unrelated\":true}";
        private readonly string _root = Directory.CreateTempSubdirectory("threadsmith-local-consent-").FullName;

        private Fixture()
        {
            Repository = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
            ConfigPath = Path.Combine(Directory.CreateDirectory(Path.Combine(Repository, ".threadsmith")).FullName, "config.json");
            Connection = $"Data Source={Path.Combine(_root, "facts.db")};Pooling=False";
            Store = new SqliteRepositoryFactsStore(Connection);
        }

        public string Repository { get; }

        public string ConfigPath { get; }

        public string Connection { get; }

        public SqliteRepositoryFactsStore Store { get; }

        public string PermissionPath => new RepositoryPermissionStore(Path.Combine(_root, "config.json")).GetPath(Repository);

        public static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            await File.WriteAllTextAsync(fixture.ConfigPath, Request, TestContext.Current.CancellationToken);
            await fixture.Store.InitializeAsync(TestContext.Current.CancellationToken);
            return fixture;
        }

        public async Task<MutationApprovalPolicyService> OpenAsync()
        {
            var binding = new SessionPolicyBinding();
            binding.Publish(new EffectiveSessionPolicy
            {
                BindingId = Guid.NewGuid(), Generation = 1, RepositoryPath = Repository, Fingerprint = "fixture",
            });
            var service = new MutationApprovalPolicyService(facts: Store, policyBinding: binding);
            await service.BindRepositoryAsync(Repository, TestContext.Current.CancellationToken);
            return service;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_root, true);
            return ValueTask.CompletedTask;
        }
    }
}
