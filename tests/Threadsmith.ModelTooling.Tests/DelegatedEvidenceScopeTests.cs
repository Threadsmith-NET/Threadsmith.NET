namespace Threadsmith.ModelTooling.Tests;

using Threadsmith.Core;
using Threadsmith.Tools;
using Threadsmith.Tools.PullRequests;
using Xunit;

/// <summary>Exercises delegation and captured evidence at their shared path-authority boundary.</summary>
public sealed class DelegatedEvidenceScopeTests : IDisposable
{
    private readonly string _temporary = Directory.CreateTempSubdirectory("threadsmith-delegated-paths-").FullName;

    /// <summary>Assignment roots retain repository spelling before evidence is filtered lexically.</summary>
    [Fact]
    public async Task AssignmentAliasesProduceCanonicalEvidenceScope()
    {
        var repository = Directory.CreateDirectory(Path.Combine(_temporary, "Repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        var alias = Path.Combine(_temporary, "repo", "src");
        Assert.SkipUnless(Directory.Exists(alias), "Requires a case-insensitive filesystem (including case-insensitive Linux mounts).");
        var parent = CreateInvocation(repository);
        var child = Scope(parent, alias, repository);
        Assert.Equal(["src"], child.ApprovedRoots);
        await using var registry = new PrEvidenceRegistry();
        var session = SessionId.New();
        var run = RunId.New();
        var inside = CreateSnapshot(new PullRequestFile("src/Example.cs", null, "modified"));
        registry.Register(session, run, inside);

        var result = registry.Read(session, run, inside.SnapshotId, child);

        Assert.Contains("src/Example.cs", result.Content, StringComparison.Ordinal);
        var outside = CreateSnapshot(new PullRequestFile("other/Example.cs", null, "modified"));
        registry.Register(session, run, outside);
        Assert.Throws<UnauthorizedAccessException>(() => registry.Read(session, run, outside.SnapshotId, child));
    }

    /// <summary>Policy rebasing canonicalizes parent aliases before computing relative paths.</summary>
    [Fact]
    public void PolicyAliasesRebaseInsideChildRepository()
    {
        var repository = Directory.CreateDirectory(Path.Combine(_temporary, "Repo")).FullName;
        Directory.CreateDirectory(Path.Combine(repository, "src"));
        var alias = Path.Combine(_temporary, "repo", "src");
        Assert.SkipUnless(Directory.Exists(alias), "Requires a case-insensitive filesystem.");
        var childRoot = Directory.CreateDirectory(Path.Combine(_temporary, "Child")).FullName;
        var parent = CreateInvocation(repository) with
        {
            AllowDirectWrites = true,
            SecurityPolicy = new EffectiveSessionPolicy
            {
                BindingId = Guid.NewGuid(),
                Generation = 1,
                RepositoryPath = repository,
                Fingerprint = "test",
                ReadFolders = [alias],
                WriteFolders = [alias],
                EditableRoots = [alias],
                MutationRoots = [alias],
            },
        };

        var child = Scope(parent, ".", childRoot);

        var policy = Assert.IsType<EffectiveSessionPolicy>(child.SecurityPolicy);
        var expected = Path.Combine(childRoot, "src");
        Assert.Equal([expected], policy.ReadFolders);
        Assert.Equal([expected], policy.WriteFolders);
        Assert.Equal([expected], policy.EditableRoots);
        Assert.Equal([expected], policy.MutationRoots);
    }

    /// <summary>Captured reads do not need current repository contents and retain rename restrictions.</summary>
    [Theory]
    [InlineData("src/Example.cs", null, true)]
    [InlineData("src/Example.cs", "outside/Old.cs", false)]
    [InlineData("outside/Example.cs", null, false)]
    public async Task CapturedReadsEnforceScopeAfterLiveRepositoryDisappears(string path, string? previousPath, bool allowed)
    {
        var repository = Directory.CreateDirectory(Path.Combine(_temporary, "Repo")).FullName;
        var invocation = CreateInvocation(repository) with { ApprovedRoots = ["src"] };
        await using var registry = new PrEvidenceRegistry();
        var session = SessionId.New();
        var run = RunId.New();
        var snapshot = CreateSnapshot(new PullRequestFile(path, previousPath, "modified"));
        registry.Register(session, run, snapshot);
        Directory.Delete(repository);

        if (allowed)
        {
            Assert.Contains(path, registry.Read(session, run, snapshot.SnapshotId, invocation).Content, StringComparison.Ordinal);
        }
        else
        {
            Assert.Throws<UnauthorizedAccessException>(() => registry.Read(session, run, snapshot.SnapshotId, invocation));
        }

        Assert.Contains(path, registry.Read(session, run, snapshot.SnapshotId, invocation with { ApprovedRoots = ["."] }).Content, StringComparison.Ordinal);
    }

    /// <summary>A live filesystem case alias never bypasses captured evidence's ordinal authority.</summary>
    [Fact]
    public async Task CapturedReadDoesNotTreatCaseAliasGrantAsUnrestricted()
    {
        var repository = Directory.CreateDirectory(Path.Combine(_temporary, "Repo")).FullName;
        var alias = Path.Combine(_temporary, "repo");
        Assert.SkipUnless(Directory.Exists(alias), "Requires a case-insensitive filesystem.");
        var invocation = CreateInvocation(repository) with { ApprovedRoots = [alias] };
        await using var registry = new PrEvidenceRegistry();
        var session = SessionId.New();
        var run = RunId.New();
        var snapshot = CreateSnapshot(new PullRequestFile("src/Example.cs", null, "modified"));
        registry.Register(session, run, snapshot);

        Assert.Throws<UnauthorizedAccessException>(() => registry.Read(session, run, snapshot.SnapshotId, invocation));
        Directory.Delete(repository);
        Assert.Throws<UnauthorizedAccessException>(() => registry.Read(session, run, snapshot.SnapshotId, invocation));
    }

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(_temporary, recursive: true);

    private static ToolInvocationContext CreateInvocation(string repository) => new()
    {
        RepositoryPath = repository,
        RequestedBy = "test",
        ApprovedRoots = ["."],
    };

    private static ToolInvocationContext Scope(ToolInvocationContext parent, string directory, string repository)
    {
        var assignment = new AgentAssignment
        {
            AssignmentId = AgentAssignmentId.New(),
            ChildRunId = RunId.New(),
            Role = AgentRole.Implementer,
            Mode = AgentRunMode.SharedWorkspace,
            Objective = "Inspect assigned evidence",
            OutputSchema = AgentAssignment.ResponseSchema,
            StoppingCondition = "Stop after scope is covered",
            Deadline = DateTimeOffset.MaxValue,
            Scope = new AgentAssignmentScope { Directories = [directory] },
            Policy = new AgentPolicySnapshot
            {
                AllowedToolIds = ["pr_fetch"],
                ModelSelectionRationale = "test",
                ContextPolicyVersion = "test",
                ToolPolicyVersion = "test",
            },
            Budget = new AgentResourceBudget(),
        };
        var plan = new DelegationPlan
        {
            DelegationId = DelegationId.New(),
            Provenance = new DelegationProvenance
            {
                SessionId = SessionId.New(),
                ParentRunId = RunId.New(),
                RepositoryIdentity = "test",
                WorkspaceId = WorkspaceId.New(),
            },
            Assignments = [assignment],
            ParentBudget = assignment.Budget,
            ImplementationAuthorized = true,
            AcceptedAt = DateTimeOffset.UtcNow,
        };
        return AgentToolPolicy.Scope(parent, plan, assignment, repository);
    }

    private static PrFetchOutput CreateSnapshot(PullRequestFile file) => new(
        "test",
        PrFetchKind.Diff,
        Guid.NewGuid(),
        DateTimeOffset.UtcNow,
        new PullRequestMetadata("https://example.com/pr/1", "repo", "1", "Test", string.Empty, "open", "repo", "source", "repo", "target", "revision", 1),
        new PullRequestPage("diff", [file], "captured diff", []),
        false,
        true);
}
