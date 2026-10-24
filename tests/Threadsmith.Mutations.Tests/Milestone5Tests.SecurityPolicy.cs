namespace Threadsmith.Mutations.Tests;

using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Workspaces;
using Xunit;

public static partial class Milestone5Tests
{
    /// <summary>Staging retains the policy generation and translates roots into the existing isolation workspace.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task SecurityPolicy_IsolatedMutationRejectsStaleApproval(bool changeGeneration)
    {
        await using var original = await TestRepository.CreateAsync(new Dictionary<string, string> { ["src/value.txt"] = "old" });
        await using var isolated = await TestRepository.CreateAsync(new Dictionary<string, string> { ["src/value.txt"] = "old" });
        await using var events = new DomainEventStream();
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = original.Root,
            Fingerprint = "test-policy",
            EditableRoots = [Path.Combine(original.Root, "src")],
            MutationRoots = [original.Root],
        };
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        var approval = new MutationApprovalPolicyService(policyBinding: binding);
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            original.Baseline,
            events,
            new WorkspaceIsolation(WorkspaceIsolationMode.GitWorktree, isolated.Root, null),
            mutationApprovalPolicy: approval,
            cancellationToken: TestContext.Current.CancellationToken);
        var mutation = CreateReplacement(original, "src/value.txt", 0, "old", "new");
        var staged = await workspace.StageAsync(CreateMutationSet(original, [mutation]), TestContext.Current.CancellationToken);
        if (changeGeneration)
        {
            binding.Publish(policy with { Generation = 2 });
        }

        var exactApproval = new MutationApproval { Level = MutationApprovalLevel.EntireSet, ApprovalId = staged.ApprovalId };
        if (changeGeneration)
        {
            var error = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => workspace.CommitAsync(
                staged.MutationSet.MutationSetId, exactApproval, TestContext.Current.CancellationToken));
            Assert.Contains("PolicyGenerationStale", error.Message, StringComparison.Ordinal);
            Assert.Equal("old", await File.ReadAllTextAsync(isolated.PathOf("src/value.txt"), TestContext.Current.CancellationToken));
        }
        else
        {
            await workspace.CommitAsync(staged.MutationSet.MutationSetId, exactApproval, TestContext.Current.CancellationToken);
            Assert.Equal("new", await File.ReadAllTextAsync(isolated.PathOf("src/value.txt"), TestContext.Current.CancellationToken));
        }

        Assert.Equal("old", await File.ReadAllTextAsync(original.PathOf("src/value.txt"), TestContext.Current.CancellationToken));
    }

    /// <summary>Generic mutations cannot bypass the control-file guard with an explicit source approval.</summary>
    [Theory]
    [InlineData(".threadsmith/config.json")]
    [InlineData(".threadsmith/session.json")]
    [InlineData(".threadsmith/extensions.json")]
    [InlineData(".threadsmith/providers.json")]
    public static async Task SecurityPolicy_ControlFileCannotBeStaged(string relativePath)
    {
        await using var repository = await TestRepository.CreateAsync(new Dictionary<string, string> { [relativePath] = "{}" });
        await using var events = new DomainEventStream();
        await using var workspace = await TransactionalWorkspace.CreateAsync(repository.Baseline, events, cancellationToken: TestContext.Current.CancellationToken);
        var mutation = CreateReplacement(repository, relativePath, 0, "{}", "{\"tools\":{}}");
        var error = await Assert.ThrowsAsync<MutationPolicyException>(() => workspace.StageAsync(
            CreateMutationSet(repository, [mutation]), TestContext.Current.CancellationToken));
        Assert.Contains("AuthoritySettingReviewRequired", error.Message, StringComparison.Ordinal);
    }
}
