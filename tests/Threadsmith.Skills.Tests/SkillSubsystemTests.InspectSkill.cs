namespace Threadsmith.Skills.Tests;

using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Skills;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Listing all skills uses an explicit bounded subset and tells the model to narrow broad queries.</summary>
    [Fact]
    public async Task InspectSkillTool_ListsBeyondDefaultCatalogLimit()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude");
        for (var index = 0; index < 105; index++)
        {
            var directory = Path.Combine(claudeRoot, $"skill-{index}");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), $"---\nname: skill-{index}\ndescription: Audit changes\n---\nInstructions.\n");
        }

        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, claudeRoot, "user:claude", false)]));
        await catalog.RefreshAsync();
        foreach (var candidate in catalog.Snapshot.Candidates)
        {
            catalog.UpdateCandidate(candidate with { Enabled = true, Verification = SkillVerificationState.DigestAllowlisted });
        }

        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        var result = await tool.ExecuteAsync(new InspectSkillInput(), new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext()));
        Assert.Equal(32, result.Value.Skills.Count);
        Assert.All(result.Value.Skills, entry => Assert.Null(entry.InputSchema));
    }

    /// <summary>Unavailable skills in the first catalog window do not hide later enabled skills.</summary>
    [Fact]
    public async Task InspectSkillTool_FiltersAvailabilityBeforeOutputLimit()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude");
        for (var index = 0; index < 150; index++)
        {
            var directory = Path.Combine(claudeRoot, $"aaa-disabled-{index:D3}");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), $"---\nname: aaa-disabled-{index:D3}\ndescription: Audit changes\n---\nInstructions.\n");
        }

        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, claudeRoot, "user:claude", false)]));
        await catalog.RefreshAsync();
        foreach (var candidate in catalog.Snapshot.Candidates)
        {
            var availableReview = candidate.Metadata.SkillId.Value == "review";
            catalog.UpdateCandidate(candidate with
            {
                Enabled = availableReview,
                Verification = availableReview
                    ? SkillVerificationState.Maintained
                    : SkillVerificationState.Unverified,
            });
        }

        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());

        var result = await tool.ExecuteAsync(new InspectSkillInput(), new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext()));

        var entry = Assert.Single(result.Value.Skills);
        Assert.Equal("review", entry.Name);
        Assert.Null(entry.InputSchema);
    }

    /// <summary>Both discovery variations hide unavailable native and Claude skills without changing their state.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("review")]
    public async Task InspectSkillTool_DiscoveryRequiresEnabledAndVerified(string? query)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claudeRoot = Path.Combine(package.Root, "claude", "portable-review");
        Directory.CreateDirectory(claudeRoot);
        await File.WriteAllTextAsync(Path.Combine(claudeRoot, "SKILL.md"), "---\nname: portable-review\ndescription: Review changes\n---\nInstructions.\n");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.User),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.GetDirectoryName(claudeRoot)!, "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext());
        foreach (var verification in Enum.GetValues<SkillVerificationState>())
        {
            foreach (var enabled in new[] { false, true })
            {
                foreach (var candidate in catalog.Snapshot.Candidates)
                {
                    catalog.UpdateCandidate(candidate with { Enabled = enabled, Verification = verification });
                }

                var result = await tool.ExecuteAsync(new InspectSkillInput { Query = query }, context);
                var expected = enabled && verification is (SkillVerificationState.Maintained
                    or SkillVerificationState.SignedTrusted or SkillVerificationState.DigestAllowlisted) ? 2 : 0;
                Assert.Equal(expected, result.Value.Skills.Count);
                Assert.All(result.Value.Skills, entry => Assert.Equal("Enabled", entry.Availability));
                Assert.All(catalog.Snapshot.Candidates, candidate =>
                {
                    Assert.Equal(enabled, candidate.Enabled);
                    Assert.Equal(verification, candidate.Verification);
                });
            }
        }
    }

    /// <summary>Inspection rechecks current enablement and package integrity.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InspectSkillTool_RejectsDisabledOrTamperedPackage(bool tampered)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.Combine(package.Root, "claude"), "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        var input = new InspectSkillInput { Selector = "review" };
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext());
        await tool.ExecuteAsync(input, context);
        if (tampered)
        {
            await File.AppendAllTextAsync(Path.Combine(package.PackageRoot, "schemas", "input.json"), " ");
        }
        else
        {
            await application.HandleAsync(new SetSkillEnabledCommand("review", false));
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tool.ExecuteAsync(input, context));
        var discovery = await tool.ExecuteAsync(new InspectSkillInput(), context);
        Assert.Empty(discovery.Value.Skills);
    }

    /// <summary>Selection and search are bounded, exclusive, and cancellation reaches both paths.</summary>
    [Fact]
    public async Task InspectSkillTool_ValidatesSelectorAndPropagatesCancellation()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog([new ClaudeSkillRoot(SkillScope.User, Path.Combine(package.Root, "claude"), "user:claude", false)]));
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        var tool = new InspectSkillTool(application, application, TestPromptLoader.Instance, new BoundedJsonSchemaValidator());
        foreach (var invalid in new[]
        {
            "{\"selector\":\"\"}", "{\"query\":\" \"}", "{\"selector\":\"review\",\"query\":\"review\"}",
            "{\"selector\":\"review\",\"input\":{}}", JsonSerializer.Serialize(new { selector = new string('x', 1025) }),
            JsonSerializer.Serialize(new { query = new string('x', 1025) }),
        })
        {
            Assert.Throws<ToolArgumentValidationException>(() => tool.DeserializeInput(invalid));
        }

        var input = Assert.IsType<InspectSkillInput>(tool.DeserializeInput("{\"selector\":\"review\"}"));
        Assert.Equal("inspect review", tool.GetActivityDetail(input));
        Assert.Equal("search review", tool.GetActivityDetail(new InspectSkillInput { Query = "review" }));
        Assert.Equal("list enabled verified skills", tool.GetActivityDetail(new InspectSkillInput()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var context = new ToolExecutionContext(ToolInvocationId.New(), SessionId.New(), RunId.New(), PermissionContext());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(input, context, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.ExecuteAsync(new InspectSkillInput(), context, cancellation.Token));
    }

    /// <summary>The shared schema reader also checks changes after initial verification.</summary>
    [Fact]
    public async Task SkillSchemaAssets_RejectsChangesAfterVerification()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var catalog = package.CreateCatalog(SkillScope.Maintained);
        await catalog.RefreshAsync();
        var verified = await new SkillPackageVerifier(new SkillTrustPolicySnapshot()).VerifyAsync(catalog.Resolve("review"));
        await File.AppendAllTextAsync(Path.Combine(package.PackageRoot, "schemas", "input.json"), " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => SkillSchemaAssets.ReadAsync(verified, "schemas/input.json", CancellationToken.None));
    }
}
