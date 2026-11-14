namespace Threadsmith.Skills.Tests;

using Threadsmith.Core;
using Threadsmith.Skills;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>The routing gate restores persisted policy and observes disable/re-enable changes.</summary>
    [Fact]
    public async Task DocumentationSkillEnablement_RestoresPolicyAndTracksChanges()
    {
        // Arrange: newly discovered maintained packages have not been verified yet.
        var cancellationToken = TestContext.Current.CancellationToken;
        using var package = TemporaryPackage.CopyMaintained("threadsmith-docs-help");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.Combine(package.Root, "claude"), "user:claude")]));
        await catalog.RefreshAsync(cancellationToken);
        var policyPath = Path.Combine(package.Root, "policy.json");
        var policy = new FileSkillTrustPolicyProvider(policyPath, new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        Assert.Equal(SkillVerificationState.Unverified, Assert.Single(catalog.Snapshot.Candidates).Verification);

        // Act / Assert: the prompt gate uses normal listing restoration, without an inspect call.
        Assert.True(await application.IsDocumentationSkillEnabledAsync(cancellationToken));
        await application.HandleAsync(new SetSkillEnabledCommand("threadsmith-docs-help", false), cancellationToken);
        Assert.False(await application.IsDocumentationSkillEnabledAsync(cancellationToken));

        // Rediscovery and a fresh policy provider must preserve a persisted disable.
        await catalog.RefreshAsync(cancellationToken);
        application = CreateCatalogApplication(
            catalog,
            new FileSkillTrustPolicyProvider(policyPath, new SkillTrustPolicySnapshot()),
            package.Root);
        Assert.False(await application.IsDocumentationSkillEnabledAsync(cancellationToken));

        await application.HandleAsync(new SetSkillEnabledCommand("threadsmith-docs-help", true), cancellationToken);
        Assert.True(await application.IsDocumentationSkillEnabledAsync(cancellationToken));
    }

    /// <summary>Other skills and non-maintained lookalikes cannot enable documentation routing.</summary>
    [Theory]
    [InlineData("review", SkillScope.Maintained)]
    [InlineData("threadsmith-docs-help", SkillScope.User)]
    public async Task DocumentationSkillEnablement_RequiresMaintainedDocsPackage(string packageName, SkillScope scope)
    {
        // Arrange
        var cancellationToken = TestContext.Current.CancellationToken;
        using var package = TemporaryPackage.CopyMaintained(packageName);
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(scope),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.Combine(package.Root, "claude"), "user:claude")]));
        await catalog.RefreshAsync(cancellationToken);
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);

        // Act
        var enabled = await application.IsDocumentationSkillEnabledAsync(cancellationToken);

        // Assert
        Assert.False(enabled);
    }
}
