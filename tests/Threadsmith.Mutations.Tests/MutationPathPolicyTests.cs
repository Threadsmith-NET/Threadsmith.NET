namespace Threadsmith.Mutations.Tests;

using Threadsmith.Core;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Regression coverage for shared path authority contracts.</summary>
public sealed class MutationPathPolicyTests
{
    /// <summary>Verifies the caller's path authority boundary.</summary>
    [Fact]
    public async Task MissingConfigurationIsAllowedAndMutationPathsRetainRestrictions()
    {
        var root = Directory.CreateTempSubdirectory("threadsmith-mutation-path-").FullName;
        try
        {
            var service = new MutationApprovalPolicyService();
            await service.BindRepositoryAsync(root, TestContext.Current.CancellationToken);
            service.ValidatePaths(["missing/Example.cs"], root);
            Assert.Throws<MutationPolicyException>(() => service.ValidatePaths(["."], root));
            Assert.Throws<MutationPolicyException>(() => service.ValidatePaths(["../sibling/file.cs"], root));
            Assert.Throws<MutationPolicyException>(() => service.ValidatePaths([".git/config"], root));
            Assert.Throws<MutationPolicyException>(() => service.ValidatePaths([".env"], root));
            Assert.Throws<MutationPolicyException>(() => service.ValidatePaths([".threadsmith/config.json"], root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Case aliases of the repository root cannot bypass repository-relative prohibitions.</summary>
    [Fact]
    public async Task CaseAliasRepositoryPathCannotBypassProhibitions()
    {
        var root = Directory.CreateTempSubdirectory("threadsmith-mutation-alias-").FullName;
        try
        {
            var repository = Directory.CreateDirectory(Path.Combine(root, "Repo")).FullName;
            var binding = new SessionPolicyBinding();
            binding.Publish(new EffectiveSessionPolicy
            {
                BindingId = Guid.NewGuid(),
                Generation = 1,
                RepositoryPath = repository,
                Fingerprint = "fixture",
                MutationRoots = [repository],
                ProhibitedPaths = ["private/**"],
            });
            var service = new MutationApprovalPolicyService(policyBinding: binding);
            await service.BindRepositoryAsync(repository, TestContext.Current.CancellationToken);
            service.ValidatePaths(["public/Example.cs"], repository);

            // On case-insensitive filesystems the alias is admitted by containment and must
            // still be normalized to the root's spelling before prohibition matching.
            Assert.Throws<MutationPolicyException>(() => service.ValidatePaths(["../repo/private/secret.txt"], repository));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
