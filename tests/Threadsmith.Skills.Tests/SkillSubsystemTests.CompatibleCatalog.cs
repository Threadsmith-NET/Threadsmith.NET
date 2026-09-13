namespace Threadsmith.Skills.Tests;

using Threadsmith.Core;
using Threadsmith.Skills;
using Xunit;

public sealed partial class SkillSubsystemTests
{
    /// <summary>Verification alone publishes maintained enablement without changing external policy.</summary>
    [Theory]
    [InlineData("review", false)]
    [InlineData("review-pr", false)]
    [InlineData("review", true)]
    [InlineData("review-pr", true)]
    public async Task CompatibleCatalog_VerifyMaintained_UpdatesListAndInspect(string skillId, bool disabled)
    {
        // Arrange: include the same native package at another scope and a Claude candidate.
        using var package = TemporaryPackage.CopyMaintained(skillId);
        var claudeRoot = Path.Combine(package.Root, "claude", "portable-review");
        Directory.CreateDirectory(claudeRoot);
        await File.WriteAllTextAsync(
            Path.Combine(claudeRoot, "SKILL.md"),
            "---\nname: portable-review\ndescription: Review repository\n---\nReview this repository.\n");
        var native = new SkillCatalog(
        [
            new SkillCatalogSource(SkillScope.Maintained, MaintainedRoot(), "maintained", IsMaintained: true),
            new SkillCatalogSource(SkillScope.Repository, package.Root, "repository:test"),
        ]);
        var catalog = new CompatibleSkillCatalog(native, new ClaudeSkillCompatibilityCatalog(
            [new ClaudeSkillRoot(SkillScope.Repository, Path.GetDirectoryName(claudeRoot)!, "repository:claude", true)]));
        var initial = await catalog.RefreshAsync();
        var selector = $"Maintained:{skillId}@1.0.0";
        var selected = native.Resolve(selector);
        var policyPath = Path.Combine(package.Root, "policy.json");
        var policy = new FileSkillTrustPolicyProvider(policyPath, new SkillTrustPolicySnapshot
        {
            DisabledSelectors = disabled
                ? new HashSet<string> { $"{selector}+{selected.Identity.Digest.Value}" }
                : new HashSet<string>(),
        });
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        Assert.Equal(SkillVerificationState.Unverified, selected.Verification);
        Assert.False(selected.Enabled);

        // Act: these are the shared commands used by /skills verify, /skills, and /skills inspect.
        var verified = await application.HandleAsync(new VerifySkillCommand(selector));
        var listed = await application.HandleAsync(new ListSkillsCommand(new SkillCatalogQuery()));
        var inspected = await application.HandleAsync(new GetSkillCommand(selector));

        // Assert: no manual enable and no policy writes are needed for maintained skills.
        Assert.Equal(SkillVerificationState.Maintained, verified.Verification);
        Assert.Equal(!disabled, verified.Enabled);
        Assert.Equal(verified, Assert.Single(listed, item =>
            item.Metadata.SkillId.Value == skillId && item.Provenance.Scope == SkillScope.Maintained));
        Assert.Equal(verified, inspected);
        Assert.Equal(verified, await catalog.ResolveAsync(selector));
        Assert.Equal(verified, native.Resolve(selector));
        Assert.Equal(initial.Candidates.Count, listed.Count);
        Assert.All(initial.Candidates.Where(item => item != selected), item => Assert.Contains(item, listed));
        Assert.False(File.Exists(policyPath));
        Assert.Empty(policy.Snapshot.EnabledSelectors);

        // Refresh remains metadata-only; verification must be performed again after rediscovery.
        var refreshed = await application.HandleAsync(new RefreshSkillsCommand());
        Assert.All(refreshed.Candidates, item =>
        {
            Assert.Equal(SkillVerificationState.Unverified, item.Verification);
            Assert.False(item.Enabled);
        });
        Assert.Equal(
            verified.Verification,
            (await application.HandleAsync(new VerifySkillCommand(selector))).Verification);
        Assert.Equal(!disabled, (await application.HandleAsync(new GetSkillCommand(selector))).Enabled);
    }

    /// <summary>Subsequent policy and integrity decisions replace earlier successful verification in every view.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibleCatalog_VerificationChanges_ReplacePreviouslyEnabledState(bool revoked)
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var catalog = new CompatibleSkillCatalog(
            package.CreateCatalog(SkillScope.Maintained),
            new ClaudeSkillCompatibilityCatalog(
                [new ClaudeSkillRoot(SkillScope.Repository, Path.Combine(package.Root, "claude"), "repository:claude", true)]));
        await catalog.RefreshAsync();
        var policyPath = Path.Combine(package.Root, "policy.json");
        var policy = new FileSkillTrustPolicyProvider(policyPath, new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        const string selector = "Maintained:review@1.0.0";
        var verified = await application.HandleAsync(new VerifySkillCommand(selector));
        Assert.True(verified.Enabled);

        var disabled = await application.HandleAsync(new SetSkillEnabledCommand(selector, false));
        Assert.False(disabled.Enabled);
        Assert.Equal(disabled, await application.HandleAsync(new GetSkillCommand(selector)));
        var persistedPolicy = await File.ReadAllTextAsync(policyPath);
        Assert.Equal(disabled, await application.HandleAsync(new VerifySkillCommand(selector)));
        Assert.Equal(persistedPolicy, await File.ReadAllTextAsync(policyPath));
        var enabled = await application.HandleAsync(new SetSkillEnabledCommand(selector, true));
        Assert.True(enabled.Enabled);
        Assert.Equal(enabled, await application.HandleAsync(new GetSkillCommand(selector)));
        persistedPolicy = await File.ReadAllTextAsync(policyPath);

        if (revoked)
        {
            policy = new FileSkillTrustPolicyProvider(policyPath, new SkillTrustPolicySnapshot
            {
                RevokedDigests = new HashSet<string> { verified.Identity.Digest.Value },
            });
            application = CreateCatalogApplication(catalog, policy, package.Root);
        }
        else
        {
            await File.AppendAllTextAsync(Path.Combine(package.PackageRoot, verified.Metadata.Assets[0].Path), "tampered");
        }

        var rejected = await application.HandleAsync(new VerifySkillCommand(selector));

        Assert.Equal(revoked ? SkillVerificationState.Revoked : SkillVerificationState.Invalid, rejected.Verification);
        Assert.False(rejected.Enabled);
        Assert.Equal(rejected, await application.HandleAsync(new GetSkillCommand(selector)));
        Assert.Equal(rejected, await catalog.ResolveAsync(selector));
        Assert.Equal(rejected, Assert.Single(await application.HandleAsync(new ListSkillsCommand(new SkillCatalogQuery()))));
        Assert.Equal(persistedPolicy, await File.ReadAllTextAsync(policyPath));
    }

    /// <summary>A refresh waiting on Claude discovery must not overwrite a newer native verification result.</summary>
    [Fact]
    public async Task CompatibleCatalog_RefreshOverlapsVerification_PreservesNewNativeState()
    {
        using var package = TemporaryPackage.CopyMaintained("review");
        var claude = new DelayedClaudeCatalog();
        var native = package.CreateCatalog(SkillScope.Maintained);
        var catalog = new CompatibleSkillCatalog(native, claude);
        await catalog.RefreshAsync();
        var policy = new FileSkillTrustPolicyProvider(Path.Combine(package.Root, "policy.json"), new SkillTrustPolicySnapshot());
        var application = CreateCatalogApplication(catalog, policy, package.Root);
        claude.DelayRefresh = true;

        var refresh = catalog.RefreshAsync();
        await claude.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        SkillCatalogCandidate verified;
        try
        {
            verified = await application.HandleAsync(new VerifySkillCommand("Maintained:review@1.0.0"));
        }
        finally
        {
            claude.CompleteRefresh.SetResult();
        }

        var refreshed = await refresh.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(verified.Enabled);
        Assert.Equal(verified, Assert.Single(refreshed.Candidates));
        Assert.Equal(verified, Assert.Single(catalog.Snapshot.Candidates));
        Assert.Equal(verified, Assert.Single(native.Snapshot.Candidates));
    }

    private static SkillApplication CreateCatalogApplication(
        CompatibleSkillCatalog catalog,
        ISkillTrustPolicyProvider policy,
        string root)
    {
        return new SkillApplication(
            catalog,
            new CompatibleSkillPackageVerifier(new SkillPackageVerifier(policy), catalog, policy),
            policy,
            new CompatibleEvaluator(),
            new CapturingWorkflowOrchestrator(),
            new InMemorySkillStateStore(),
            new SkillPackageInstaller(Path.Combine(root, "store"), Path.Combine(root, "quarantine")));
    }

    private sealed class DelayedClaudeCatalog : IClaudeSkillCompatibilityCatalog
    {
        public IReadOnlyList<ClaudeSkillCandidate> Candidates => [];

        public async Task<IReadOnlyList<ClaudeSkillCandidate>> RefreshAsync(CancellationToken cancellationToken = default)
        {
            if (DelayRefresh)
            {
                RefreshStarted.TrySetResult();
                await CompleteRefresh.Task.WaitAsync(cancellationToken);
            }

            return Candidates;
        }

        public Task<ClaudeSkillSnapshot> ActivateAsync(ClaudeSkillCandidate candidate, CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        internal bool DelayRefresh { get; set; }

        internal TaskCompletionSource RefreshStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource CompleteRefresh { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
