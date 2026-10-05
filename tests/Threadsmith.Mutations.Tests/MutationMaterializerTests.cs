namespace Threadsmith.Mutations.Tests;

using System.Security.Cryptography;
using System.Text;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Verifies shared mutation materialization independently of model planning.</summary>
public sealed class MutationMaterializerTests
{
    /// <summary>Sequential edits use host identities and cannot inherit plan approval.</summary>
    [Fact]
    public static async Task MaterializeAsync_SequentialAnchors_UsesHostIdentityWithoutPlanAuthority()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await MaterializationFixture.CreateAsync("class Example { int Value = 1; }\r\n", cancellationToken);
        var instruction = new MutationProposalSet
        {
            Rationale = "Change the declaration in model order.",
            Mutations =
            [
                new ReplaceTextMutationProposal
                {
                    RelativePath = "Example.cs",
                    ExpectedText = "int Value = 1",
                    ReplacementText = "string Renamed = missing",
                },
                new ReplaceTextMutationProposal
                {
                    RelativePath = "Example.cs",
                    ExpectedText = "missing",
                    ReplacementText = "\"ready\"",
                },
            ],
        };

        var materialized = await fixture.Materializer.MaterializeAsync(
            instruction, fixture.SessionId, fixture.RunId, fixture.Workspace, cancellationToken);
        var staged = await fixture.Workspace.StageAsync(materialized, cancellationToken);

        Assert.Equal(fixture.SessionId, materialized.SessionId);
        Assert.Equal(fixture.RunId, materialized.RunId);
        Assert.Equal(fixture.Workspace.Baseline.WorkspaceId, materialized.WorkspaceId);
        Assert.NotEqual(default, materialized.MutationSetId);
        Assert.Equal(MutationApprovalLevel.EntireSet, staged.MutationSet.RequiredApproval);
        Assert.Equal("class Example { string Renamed = \"ready\"; }\r\n", await fixture.Workspace.ReadStagedTextAsync(
            materialized.MutationSetId, "Example.cs", cancellationToken));
    }

    /// <summary>Missing and ambiguous anchors leave live source unchanged.</summary>
    [Theory]
    [InlineData("Repeated Repeated", "Repeated", null)]
    [InlineData("Original", "Missing", null)]
    [InlineData("Original", "", null)]
    public static async Task MaterializeAsync_UnprovenAnchor_RejectsBeforeStaging(string source, string expected, int? offset)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await MaterializationFixture.CreateAsync(source, cancellationToken);
        var instruction = new MutationProposalSet
        {
            Rationale = "An exact anchor is required.",
            Mutations =
            [
                new ReplaceTextMutationProposal
                {
                    RelativePath = "Example.cs",
                    ExpectedText = expected,
                    StartOffset = offset,
                    ReplacementText = "changed",
                },
            ],
        };

        await Assert.ThrowsAsync<MutationInstructionException>(() => fixture.Materializer.MaterializeAsync(
            instruction, fixture.SessionId, fixture.RunId, fixture.Workspace, cancellationToken));

        Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(fixture.Root, "Example.cs"), cancellationToken));
    }

    /// <summary>A null operation is an instruction error before any source materialization.</summary>
    [Fact]
    public static async Task MaterializeAsync_NullOperation_ProducesRepairableInstructionFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await MaterializationFixture.CreateAsync("class Example {}", cancellationToken);
        var instruction = new MutationProposalSet { Rationale = "Malformed input", Mutations = [null!] };

        await Assert.ThrowsAsync<MutationInstructionException>(() => fixture.Materializer.MaterializeAsync(
            instruction, fixture.SessionId, fixture.RunId, fixture.Workspace, cancellationToken));
    }

    /// <summary>Sequential edits share current text when repository policy identifies path aliases.</summary>
    [Fact]
    public static async Task MaterializeAsync_PathAliases_UseRepositoryIdentity()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await MaterializationFixture.CreateAsync("class Example { int Value = 1; }", cancellationToken);
        var alias = RepositoryPathPolicy.GetPathComparer(fixture.Root).Equals("Example.cs", "example.cs")
            ? "example.cs"
            : "Example.cs";
        var instruction = new MutationProposalSet
        {
            Rationale = "Resolve anchors using repository path identity.",
            Mutations =
            [
                new ReplaceTextMutationProposal { RelativePath = "Example.cs", ExpectedText = "Value", ReplacementText = "Renamed" },
                new ReplaceTextMutationProposal { RelativePath = alias, ExpectedText = "Renamed", ReplacementText = "Final" },
            ],
        };

        var materialized = await fixture.Materializer.MaterializeAsync(
            instruction, fixture.SessionId, fixture.RunId, fixture.Workspace, cancellationToken);
        var staged = await fixture.Workspace.StageAsync(materialized, cancellationToken);

        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Equal("class Example { int Final = 1; }", await fixture.Workspace.ReadStagedTextAsync(
            materialized.MutationSetId, "Example.cs", cancellationToken));
    }

    /// <summary>Later operations follow a case-only move's destination rather than its retired source.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public static async Task MaterializeAsync_CaseOnlyMove_PreservesSubsequentEndpoint(bool deleteAfterMove)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await MaterializationFixture.CreateAsync("class Example {}", cancellationToken);
        Assert.SkipWhen(!RepositoryPathPolicy.GetPathComparer(fixture.Root).Equals("Example.cs", "example.cs"), "Requires case-insensitive repository paths.");
        MutationProposalChange subsequent = deleteAfterMove
            ? new DeleteFileMutationProposal { RelativePath = "example.cs" }
            : new ReplaceTextMutationProposal { RelativePath = "example.cs", ExpectedText = "Example", ReplacementText = "Renamed" };
        var instruction = new MutationProposalSet
        {
            Rationale = "Apply operations to the moved endpoint.",
            Mutations =
            [
                new MoveFileMutationProposal { RelativePath = "Example.cs", DestinationRelativePath = "example.cs" },
                subsequent,
            ],
        };

        var materialized = await fixture.Materializer.MaterializeAsync(
            instruction, fixture.SessionId, fixture.RunId, fixture.Workspace, cancellationToken);
        var staged = await fixture.Workspace.StageAsync(materialized, cancellationToken);

        Assert.Equal("example.cs", materialized.Mutations[1].RelativePath);
        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Equal(deleteAfterMove ? null : "class Renamed {}", await fixture.Workspace.ReadStagedTextAsync(
            materialized.MutationSetId, "example.cs", cancellationToken));
    }

    /// <summary>Lifecycle overlays agree with the transactional staging view.</summary>
    [Fact]
    public static async Task MaterializeAsync_LifecycleOperations_PreserveExactPreviewAndOverlay()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await MaterializationFixture.CreateAsync("class Example {}\n", cancellationToken);
        var instruction = new MutationProposalSet
        {
            Rationale = "Move existing source and add a related type.",
            Mutations =
            [
                new MoveFileMutationProposal { RelativePath = "Example.cs", DestinationRelativePath = "Moved.cs" },
                new CreateFileMutationProposal
                {
                    RelativePath = "Added.cs",
                    Content = new MutationProposalContent { Text = "class Added {}\n" },
                },
            ],
        };

        var materialized = await fixture.Materializer.MaterializeAsync(
            instruction, fixture.SessionId, fixture.RunId, fixture.Workspace, cancellationToken);
        var staged = await fixture.Workspace.StageAsync(materialized, cancellationToken);

        Assert.False(staged.Conflicts.HasConflicts);
        Assert.Null(await fixture.Workspace.ReadStagedTextAsync(materialized.MutationSetId, "Example.cs", cancellationToken));
        Assert.Equal("class Example {}\n", await fixture.Workspace.ReadStagedTextAsync(materialized.MutationSetId, "Moved.cs", cancellationToken));
        Assert.Equal("class Added {}\n", await fixture.Workspace.ReadStagedTextAsync(materialized.MutationSetId, "Added.cs", cancellationToken));

        Assert.NotNull(materialized.Mutations[0].ExpectedIdentity);
        Assert.Equal("class Example {}\n", await File.ReadAllTextAsync(Path.Combine(fixture.Root, "Example.cs"), cancellationToken));
    }

    /// <summary>Logical line breaks normalize without changing source offsets or literal escapes.</summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public static async Task MaterializeAsync_OptionalOffsets_PreserveNewlinesAndLiteralEscapes(string ending)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = "// header 😀" + ending + "class Example" + ending + "{" + ending
            + "    string Value = \"a\\nb\";" + ending + "}" + ending;
        const string expected = "{\r\n    string Value = \"a\\nb\";";
        const string replacement = "{\r\n    string Value = \"c\\nd\";";
        await using var fixture = await MaterializationFixture.CreateAsync(source, cancellationToken);
        var instruction = new MutationProposalSet
        {
            Rationale = "Replace exact source while preserving literal escapes.",
            Mutations =
            [
                new ReplaceTextMutationProposal
                {
                    RelativePath = "Example.cs",
                    ExpectedText = expected,
                    ReplacementText = replacement,
                },
            ],
        };
        var materialized = await fixture.Materializer.MaterializeAsync(
            instruction, fixture.SessionId, fixture.RunId, fixture.Workspace, cancellationToken);
        var change = Assert.Single(materialized.Mutations);
        var actualExpected = expected.ReplaceLineEndings(ending);
        Assert.Equal(source.IndexOf(actualExpected, StringComparison.Ordinal), change.StartOffset);
        Assert.Equal(actualExpected, change.ExpectedText);
        Assert.Equal(replacement.ReplaceLineEndings(ending), change.ReplacementText);
        await fixture.Workspace.StageAsync(materialized, cancellationToken);
        Assert.Equal(
            source.Replace(actualExpected, change.ReplacementText, StringComparison.Ordinal),
            await fixture.Workspace.ReadStagedTextAsync(materialized.MutationSetId, "Example.cs", cancellationToken));
        Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(fixture.Root, "Example.cs"), cancellationToken));
    }

    /// <summary>Escaped-newline guesses never replace unmatched source anchors.</summary>
    [Theory]
    [InlineData("one\ntwo\none\ntwo", "one\\ntwo", "new\\ntext")]
    [InlineData("one\ntwo", "one\\ntwo", "mixed\nencoding")]
    public static async Task MaterializeAsync_EscapedNewlineRecovery_RejectsBeforeStaging(string source, string expected, string replacement)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var fixture = await MaterializationFixture.CreateAsync(source, cancellationToken);
        var instruction = new MutationProposalSet
        {
            Rationale = "Reject an anchor that is absent from actual source.",
            Mutations =
            [
                new ReplaceTextMutationProposal
                {
                    RelativePath = "Example.cs",
                    ExpectedText = expected,
                    ReplacementText = replacement,
                },
            ],
        };
        await Assert.ThrowsAsync<MutationInstructionException>(() => fixture.Materializer.MaterializeAsync(
            instruction, fixture.SessionId, fixture.RunId, fixture.Workspace, cancellationToken));
        Assert.Equal(source, await File.ReadAllTextAsync(Path.Combine(fixture.Root, "Example.cs"), cancellationToken));
    }

    private sealed class MaterializationFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events;

        private MaterializationFixture(string root, TransactionalWorkspace workspace, DomainEventStream events)
        {
            Root = root;
            Workspace = workspace;
            _events = events;
            Materializer = new MutationMaterializer(TestPromptLoader.Instance, new PassthroughSanitizer(), events);
        }

        public string Root { get; }

        public SessionId SessionId { get; } = SessionId.New();

        public RunId RunId { get; } = RunId.New();

        public TransactionalWorkspace Workspace { get; }

        public MutationMaterializer Materializer { get; }

        public static async Task<MaterializationFixture> CreateAsync(string source, CancellationToken cancellationToken)
        {
            var root = Directory.CreateTempSubdirectory("threadsmith-materializer-").FullName;
            var events = new DomainEventStream();
            try
            {
                var bytes = Encoding.UTF8.GetBytes(source);
                await File.WriteAllBytesAsync(Path.Combine(root, "Example.cs"), bytes, cancellationToken);
                var baseline = new WorkspaceBaseline(
                    WorkspaceId.New(),
                    root,
                    DateTimeOffset.UtcNow,
                    [new WorkspaceFileHash("Example.cs", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.Length)],
                    TrustLevel: RepositoryTrustLevel.TrustedMutation);
                var policy = new MutationApprovalPolicyService();
                await policy.SetPolicyAsync(MutationApprovalPolicy.ReviewAll, cancellationToken);
                var workspace = await TransactionalWorkspace.CreateAsync(
                    baseline, events, mutationApprovalPolicy: policy, cancellationToken: cancellationToken);
                return new MaterializationFixture(root, workspace, events);
            }
            catch
            {
                await events.DisposeAsync();
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Workspace.DisposeAsync();
            await _events.DisposeAsync();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class PassthroughSanitizer : IOutputSanitizer
    {
        public string Sanitize(string value) => value;
    }
}
