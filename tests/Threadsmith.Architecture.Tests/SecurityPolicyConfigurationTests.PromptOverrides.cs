namespace Threadsmith.Architecture.Tests;

using Threadsmith.Core;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>The composed policy protects both override trees through the existing mutation guard.</summary>
    [Fact]
    public void PromptOverrides_UserAndApplicationTrees_UseExistingControlDirectoryProtection()
    {
        var policy = Load();
        var user = new RepositoryPermissionStore(_paths.UserConfiguration).DirectoryPath;
        var application = Path.Combine(AppContext.BaseDirectory, "repositories");
        Assert.Contains(user, policy.ControlDirectories);
        Assert.Contains(application, policy.ControlDirectories);

        foreach (var root in new[] { user, application })
        {
            var prompt = Path.Combine(root, "repository-key", "prompts", "Threadsmith.Execution", "models", Guid.NewGuid().ToString("D"), "Context-ExecutionOutcome.md");
            Assert.Throws<UnauthorizedAccessException>(() => AuthorityControlPathGuard.Validate(
                prompt, _paths.RepositoryRoot, policy.ControlPaths, policy.ControlDirectories));
            Assert.Throws<UnauthorizedAccessException>(() => AuthorityControlPathGuard.Validate(
                root, _paths.RepositoryRoot, policy.ControlPaths, policy.ControlDirectories));
            Assert.Throws<UnauthorizedAccessException>(() => AuthorityControlPathGuard.Validate(
                Path.GetDirectoryName(root)!, _paths.RepositoryRoot, policy.ControlPaths, policy.ControlDirectories));
        }
    }
}
