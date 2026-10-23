namespace Threadsmith.Architecture.Tests;

using Microsoft.Extensions.Configuration;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

public sealed partial class SecurityPolicyConfigurationTests
{
    /// <summary>Explicit trusted grants and repository filters survive upgrades, even with automatic application.</summary>
    [Theory]
    [InlineData("[]", false)]
    [InlineData("[\"src/\"]", false)]
    [InlineData("[]", true)]
    [InlineData("[\"src/\"]", true)]
    public async Task ExplicitRootsConstrainBaselineAndMutationEnforcement(string roots, bool repositoryFilter)
    {
        var token = TestContext.Current.CancellationToken;
        await File.WriteAllTextAsync(
            _paths.UserConfiguration,
            "{\"mutation\":{\"approvalPolicy\":\"alwaysTrustRepo\"},\"editableRoots\":" + (repositoryFilter ? "[\".\"]" : roots) + "}",
            token);
        if (repositoryFilter)
        {
            await File.WriteAllTextAsync(_paths.RepositoryConfiguration, "{\"editableRoots\":" + roots + "}", token);
        }

        var policy = Load();
        var expectedRoots = roots == "[]" ? Array.Empty<string>() : [Path.Combine(_paths.RepositoryRoot, "src")];
        Assert.Equal(expectedRoots, policy.EditableRoots);
        Assert.Equal(expectedRoots, policy.MutationRoots);
        Assert.Equal(MutationApprovalPolicy.AlwaysTrustRepo, policy.MutationCeiling);
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        await using var events = new DomainEventStream();
        var lifecycle = new RepositoryLifecycle(
            events,
            new EditFactsStore(),
            new DotNetEnvironmentResolver(),
            repositoryOpened: (_, _) =>
            {
                binding.Publish(policy);
                return Task.CompletedTask;
            },
            policyBinding: binding);
        var session = SessionId.New();
        var opened = await lifecycle.HandleAsync(new OpenRepositoryCommand(session, _paths.RepositoryRoot, RepositoryTrustLevel.TrustedMutation), token);
        var baseline = await lifecycle.HandleAsync(new RecordBaselineCommand(session, opened.WorkspaceId), token);
        Assert.Equal(roots == "[]" ? [] : new[] { "src" }, baseline.ApprovedRoots);
        await using var workspace = await TransactionalWorkspace.CreateAsync(
            baseline,
            events,
            mutationApprovalPolicy: new MutationApprovalPolicyService(policyBinding: binding),
            cancellationToken: token);
        var set = new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = session,
            RunId = RunId.New(),
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
            BaselineRevision = baseline.GitRevision,
            Rationale = "Upgrade restriction regression",
            Mutations = [new Mutation { MutationId = MutationId.New(), Type = MutationType.CreateFile, RelativePath = "tests/outside.cs", ReplacementText = "blocked" }],
        };
        await Assert.ThrowsAsync<MutationPolicyException>(() => workspace.StageAsync(set, token));
        if (roots == "[]")
        {
            await Assert.ThrowsAsync<MutationPolicyException>(() => workspace.StageAsync(
                set with { Mutations = [set.Mutations[0] with { RelativePath = "src/inside.cs" }] },
                token));
        }
        else
        {
            workspace.ValidateEditPaths(["src/inside.cs"]);
            var staged = await workspace.StageAsync(set with { Mutations = [set.Mutations[0] with { RelativePath = "src/inside.cs" }] }, token);
            Assert.False(staged.Conflicts.HasConflicts);
        }
    }

    /// <summary>Absent root configuration permits repository edits while protecting authority stores.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultRootsAllowRepositoryEditsButPreserveProtectedStores(bool repositoryTemplate)
    {
        var token = TestContext.Current.CancellationToken;
        const string legacy = """
            {"prohibitedPaths":[".threadsmith/secrets/","**/*.env","**/*.snk"]}
            """;
        await File.WriteAllTextAsync(_paths.UserConfiguration, legacy, token);
        if (repositoryTemplate)
        {
            await File.WriteAllTextAsync(_paths.RepositoryConfiguration, legacy, token);
        }

        var policy = Load();
        Assert.Equal([_paths.RepositoryRoot], policy.EditableRoots);
        Assert.Equal([_paths.RepositoryRoot], policy.MutationRoots);
        var binding = new SessionPolicyBinding();
        binding.Publish(policy);
        var approval = new MutationApprovalPolicyService(policyBinding: binding);
        await using var events = new DomainEventStream();
        var lifecycle = new RepositoryLifecycle(
            events,
            new EditFactsStore(),
            new DotNetEnvironmentResolver(),
            repositoryOpened: (_, _) =>
            {
                binding.Publish(policy);
                return Task.CompletedTask;
            },
            policyBinding: binding);
        var session = SessionId.New();
        var opened = await lifecycle.HandleAsync(new OpenRepositoryCommand(session, _paths.RepositoryRoot, RepositoryTrustLevel.TrustedMutation), token);
        var baseline = await lifecycle.HandleAsync(new RecordBaselineCommand(session, opened.WorkspaceId), token);
        Assert.Equal(["."], baseline.ApprovedRoots);
        await using var workspace = await TransactionalWorkspace.CreateAsync(baseline, events, mutationApprovalPolicy: approval, cancellationToken: token);
        string[] ordinary = ["container/source/AI.Inference.Fusion/Example.cs", "tests/Example.cs", "docs/guide.md", ".inbox/plan.md"];
        workspace.ValidateEditPaths(ordinary);
        var set = new MutationSet
        {
            MutationSetId = MutationSetId.New(),
            SessionId = session,
            RunId = RunId.New(),
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
            BaselineRevision = baseline.GitRevision,
            Rationale = "Default repository-edit regression",
            Mutations = ordinary.Select(path => new Mutation
            {
                MutationId = MutationId.New(),
                Type = MutationType.CreateFile,
                RelativePath = path,
                ReplacementText = "ordinary source edit",
            }).ToArray(),
        };
        var staged = await workspace.StageAsync(set, token);
        Assert.False(staged.Conflicts.HasConflicts);
        await workspace.CommitAsync(
            set.MutationSetId,
            new MutationApproval
            {
                Level = MutationApprovalLevel.EntireSet,
                ApprovalId = staged.ApprovalId,
            },
            token);
        foreach (var path in ordinary)
        {
            Assert.Equal("ordinary source edit", await File.ReadAllTextAsync(Path.Combine(_paths.RepositoryRoot, path), token));
        }

        string[] protectedPaths = [".threadsmith/config.json", ".threadsmith/session.json", ".threadsmith/providers.json",
            ".threadsmith/extensions.json", ".threadsmith/secrets/config.json", "container/source/.threadsmith/config.json",
            "container/source/private.env", "tests/signing.snk", "docs/private.pem", ".git/config", "../escape.txt"];
        foreach (var path in protectedPaths)
        {
            var rejected = set with
            {
                MutationSetId = MutationSetId.New(),
                Mutations = [new Mutation { MutationId = MutationId.New(), Type = MutationType.CreateFile, RelativePath = path, ReplacementText = "blocked" }],
            };
            await Assert.ThrowsAsync<MutationPolicyException>(() => workspace.StageAsync(rejected, token));
        }

        // Even a broad direct-write grant cannot rewrite policy or secret stores.
        var configuration = new ConfigurationBuilder().Build();
        var write = new WriteFileTool(new WriteFileConfiguration(configuration, configuration, _paths.RepositoryRoot), new UnusedConversationStore(), TestPromptLoader.Instance);
        var context = new ToolInvocationContext
        {
            RepositoryPath = _paths.RepositoryRoot,
            RequestedBy = "model:regression",
            TrustLevel = RepositoryTrustLevel.TrustedMutation,
            SecurityPolicy = policy with { WriteFolders = [_root] },
        };
        var execution = new ToolExecutionContext(ToolInvocationId.New(), session, RunId.New(), context);
        foreach (var path in policy.ControlPaths.Where(path => RepositoryPathPolicy.ContainsPath(_root, path)))
        {
            var input = new WriteFileInput(path, "blocked") { Overwrite = true };
            Assert.False(new DefaultPolicyEngine().Evaluate(write, input, context).IsAllowed);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => write.ExecuteAsync(input, execution, token));
        }

        Assert.Equal(legacy, await File.ReadAllTextAsync(_paths.UserConfiguration, token));
    }

    private sealed class EditFactsStore : IRepositoryFactsStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<RepositoryFacts?> GetAsync(string repositoryPath, CancellationToken cancellationToken = default) => Task.FromResult<RepositoryFacts?>(null);

        public Task UpsertAsync(RepositoryFacts facts, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
