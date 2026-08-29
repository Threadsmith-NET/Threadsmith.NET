namespace Threadsmith.NativeTools.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.DotNet;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Xunit;

/// <summary>Verifies Plan-81 exact code exploration source, bounds, policy, and tool-adapter contracts.</summary>
public sealed class Plan81CodeExploreToolTests
{
    /// <summary>Exact symbol anchors return current line-numbered source and content digests.</summary>
    [Fact]
    public async Task CodeExplore_ExactSymbolAnchor_ReturnsNumberedSourceAndDigests()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Example.Worker",
                ExactSymbolAnchors = ["Example.Worker"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var resolution = Assert.Single(result.ResolvedAnchors);
        Assert.Equal(CodeExploreResolutionOutcome.Resolved, resolution.Outcome);
        var selectedSymbol = resolution.SelectedSymbol ?? throw new InvalidOperationException("Expected a selected symbol.");
        var section = Assert.Single(result.FileSections);
        Assert.Equal("src/Library/Code.cs", section.FilePath);
        Assert.Contains(section.Source.NumberedLines, line => line.Contains("public sealed class Worker", StringComparison.Ordinal));
        Assert.All(section.Source.NumberedLines, line => Assert.Matches("^[0-9]+: ", line));
        AssertSectionDigests(fixture, section);
        Assert.True(result.Coverage.SourceComplete);
        Assert.Empty(result.ContinuationTargets);

        var replayedById = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = selectedSymbol.Id,
                SymbolIds = [selectedSymbol.Id],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var replayedResolution = Assert.Single(replayedById.ResolvedAnchors);
        Assert.Equal(CodeExploreResolutionOutcome.Resolved, replayedResolution.Outcome);
        Assert.Equal(selectedSymbol.Id, replayedResolution.SelectedSymbol?.Id);
        Assert.Single(replayedById.FileSections);
    }

    /// <summary>Source guarantees describe model-visible source as sanitized instead of byte-verbatim.</summary>
    [Fact]
    public async Task CodeExplore_SourceGuarantees_DoNotClaimVerbatimModelVisibleBytes()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Example.Worker",
                ExactSymbolAnchors = ["Example.Worker"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var guarantee = Assert.Single(result.Presentation?.SourceGuarantees ?? []);
        Assert.Equal(CodeExploreSourceGuaranteeKind.ReadEquivalent, guarantee.Kind);
        Assert.True(guarantee.IsCurrent);
        Assert.False(guarantee.IsVerbatim);
        Assert.True(guarantee.IsLineNumbered);
        Assert.True(guarantee.IsReadEquivalent);
        Assert.Contains("host output sanitization", guarantee.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("verbatim", guarantee.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Simple exact overload anchors tolerate conventional spaces and include ref/out/in/ref-readonly parameter kinds.</summary>
    [Fact]
    public async Task CodeExplore_SimpleOverloadAnchors_CanonicalizeWhitespaceAndRefKinds()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();

        var ordinary = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Handle",
                ExactSymbolAnchors = ["Handle(int, string)"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var byRef = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Handle",
                ExactSymbolAnchors = ["Handle(ref int, out string)"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var byReadonlyRef = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Inspect",
                ExactSymbolAnchors = ["Inspect(in int, ref readonly string)"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        Assert.Equal(CodeExploreResolutionOutcome.Resolved, Assert.Single(ordinary.ResolvedAnchors).Outcome);
        Assert.Contains(
            Assert.Single(ordinary.FileSections).Source.NumberedLines,
            line => line.Contains("Handle(int value, string name)", StringComparison.Ordinal));
        Assert.Equal(CodeExploreResolutionOutcome.Resolved, Assert.Single(byRef.ResolvedAnchors).Outcome);
        Assert.Contains(
            Assert.Single(byRef.FileSections).Source.NumberedLines,
            line => line.Contains("Handle(ref int value, out string name)", StringComparison.Ordinal));
        Assert.Equal(CodeExploreResolutionOutcome.Resolved, Assert.Single(byReadonlyRef.ResolvedAnchors).Outcome);
        Assert.Contains(
            Assert.Single(byReadonlyRef.FileSections).Source.NumberedLines,
            line => line.Contains("Inspect(in int value, ref readonly string name)", StringComparison.Ordinal));
    }

    /// <summary>Source budgets that cannot fit the first selected line still return a replayable exact-range cursor.</summary>
    [Fact]
    public async Task CodeExplore_FirstLineExceedsBudget_ReturnsReplayableExactRangeContinuation()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var truncated = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "src/Library/Code.cs",
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = "src/Library/Code.cs",
                        SelectionMode = CodeExplorePathSelectionMode.WholeFile,
                    },
                ],
                Limits = CreateLimits(maximumSourceCharacters: 1, maximumPerFileSourceCharacters: 1),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var truncatedSection = Assert.Single(truncated.FileSections);
        Assert.Equal(CodeExploreSourceCompleteness.Omitted, truncatedSection.Source.Completeness);
        Assert.Empty(truncatedSection.Source.NumberedLines);
        var continuation = Assert.Single(truncated.ContinuationTargets);
        Assert.Equal(CodeExplorePathSelectionMode.ExactLineRange, continuation.SelectionMode);
        Assert.Equal(1, continuation.StartLine);
        Assert.True(continuation.EndLine > continuation.StartLine);
        Assert.Equal(truncated.WorkspaceGeneration, continuation.WorkspaceGeneration);
        AssertSha256(continuation.ExpectedFileSha256);

        var replayed = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = continuation.Anchor,
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = continuation.FilePath ?? continuation.Anchor,
                        Line = continuation.StartLine,
                        EndLine = continuation.EndLine,
                        StartAtLine = continuation.StartAtLine,
                        SelectionMode = continuation.SelectionMode ?? CodeExplorePathSelectionMode.ExactLineRange,
                        ExpectedFileSha256 = continuation.ExpectedFileSha256,
                        ExpectedWorkspaceGeneration = continuation.WorkspaceGeneration,
                    },
                ],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var replayedSection = Assert.Single(replayed.FileSections);
        Assert.Equal(CodeExploreSourceCompleteness.Complete, replayedSection.Source.Completeness);
        Assert.Contains(replayedSection.Source.NumberedLines, line => line.StartsWith("1: ", StringComparison.Ordinal));
    }

    /// <summary>Truncated exact ranges preserve the requested end line and do not replay unrelated tails.</summary>
    [Fact]
    public async Task CodeExplore_TruncatedExactLineRangeContinuation_PreservesEndLine()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var truncated = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "src/Library/Code.cs",
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = "src/Library/Code.cs",
                        SelectionMode = CodeExplorePathSelectionMode.ExactLineRange,
                        Line = 3,
                        EndLine = 9,
                    },
                ],
                Limits = CreateLimits(maximumSourceCharacters: 35, maximumPerFileSourceCharacters: 35),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var continuation = Assert.Single(truncated.ContinuationTargets);
        Assert.Equal(CodeExplorePathSelectionMode.ExactLineRange, continuation.SelectionMode);
        Assert.Equal(9, continuation.EndLine);

        var replayed = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = continuation.Anchor,
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = continuation.FilePath ?? continuation.Anchor,
                        Line = continuation.StartLine,
                        EndLine = continuation.EndLine,
                        StartAtLine = continuation.StartAtLine,
                        SelectionMode = continuation.SelectionMode ?? CodeExplorePathSelectionMode.ExactLineRange,
                        ExpectedFileSha256 = continuation.ExpectedFileSha256,
                        ExpectedWorkspaceGeneration = continuation.WorkspaceGeneration,
                    },
                ],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var replayedSection = Assert.Single(replayed.FileSections);
        Assert.All(
            replayedSection.Source.NumberedLines,
            line => Assert.InRange(GetNumberedLineNumber(line), 3, 9));
        Assert.DoesNotContain(replayedSection.Source.NumberedLines, line => line.Contains("Overloads", StringComparison.Ordinal));
    }

    /// <summary>Continuation generation and digest mismatches are omitted rather than returning stale source.</summary>
    [Fact]
    public async Task CodeExplore_ContinuationGenerationAndDigestMismatch_OmitSource()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var baseline = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "src/Library/Code.cs",
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = "src/Library/Code.cs",
                        SelectionMode = CodeExplorePathSelectionMode.SingleLine,
                        Line = 1,
                    },
                ],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var generationMismatch = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "src/Library/Code.cs",
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = "src/Library/Code.cs",
                        SelectionMode = CodeExplorePathSelectionMode.SingleLine,
                        Line = 1,
                        ExpectedWorkspaceGeneration = baseline.WorkspaceGeneration + 1,
                    },
                ],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var digestMismatch = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "src/Library/Code.cs",
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = "src/Library/Code.cs",
                        SelectionMode = CodeExplorePathSelectionMode.SingleLine,
                        Line = 1,
                        ExpectedFileSha256 = new string('0', 64),
                    },
                ],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        Assert.Equal(CodeExploreResolutionOutcome.Omitted, Assert.Single(generationMismatch.ResolvedAnchors).Outcome);
        Assert.Empty(generationMismatch.FileSections);
        Assert.Contains(generationMismatch.Omissions, omission => omission.Contains("generation", StringComparison.OrdinalIgnoreCase));
        var drifted = Assert.Single(digestMismatch.FileSections);
        Assert.Equal(CodeExploreSourceCompleteness.Drifted, drifted.Source.Completeness);
        Assert.Empty(drifted.Source.NumberedLines);
        Assert.Contains(drifted.Source.OmittedRanges, omission => omission.Contains("digest", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Exact symbol matches whose only source is denied by path policy do not disclose symbol identity metadata.</summary>
    [Fact]
    public async Task CodeExplore_ProhibitedSymbolSource_OmitsIdentityAndAlternatives()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "SecretType",
                ExactSymbolAnchors = ["SecretType"],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader("secret/**"),
            TestContext.Current.CancellationToken);

        var resolution = Assert.Single(result.ResolvedAnchors);
        Assert.Equal(CodeExploreResolutionOutcome.Omitted, resolution.Outcome);
        Assert.Null(resolution.SelectedSymbol);
        Assert.Null(resolution.SelectedLocation);
        Assert.Empty(resolution.Alternatives);
        Assert.Empty(result.FileSections);
    }

    /// <summary>Linked path projections respect maximumAlternatives for alternatives and admitted source sections.</summary>
    [Fact]
    public async Task CodeExplore_LinkedPathAnchor_AppliesMaximumAlternativesToCandidates()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "shared/Linked.cs",
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = "shared/Linked.cs",
                        SelectionMode = CodeExplorePathSelectionMode.WholeFile,
                    },
                ],
                Limits = CreateLimits(maximumAlternatives: 1, maximumFiles: 16),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var resolution = Assert.Single(result.ResolvedAnchors);
        Assert.Equal(CodeExploreResolutionOutcome.Resolved, resolution.Outcome);
        Assert.Single(resolution.Alternatives);
        Assert.Equal(2, result.FileSections.Count);
        Assert.All(result.FileSections, section => Assert.Equal("shared/Linked.cs", section.FilePath));
        Assert.Contains(result.Omissions, omission => omission.Contains("capped", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Natural-language discovery ranks compiler-known multi-term declarations and allocates usable source.</summary>
    [Fact]
    public async Task CodeExplore_NaturalLanguageQuery_RanksCandidatesAndAllocatesSource()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "default temporal filtering response transparency",
                Mode = CodeExploreMode.Survey,
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        var discovery = result.Discovery ?? throw new InvalidOperationException("Expected natural-language discovery metadata.");
        Assert.Empty(result.QueryInterpretation?.ExactIdentifiers ?? []);
        Assert.True(discovery.CandidateCount > 0);
        Assert.Contains(result.CandidateSummaries ?? [], summary =>
            summary.Selected
            && summary.Symbol?.DisplayName.Contains("BuildDefaultTemporalTransparency", StringComparison.Ordinal) == true
            && summary.Reasons.HasFlag(CodeExploreSelectionReason.MultiTerm));
        Assert.Contains(result.FileSections, section =>
            section.FilePath == "src/Library/Code.cs"
            && section.Source.NumberedLines.Any(line =>
                line.Contains("BuildDefaultTemporalTransparency", StringComparison.Ordinal)));
        Assert.True(result.Allocation?.SpentSourceCharacters > 0);
    }

    /// <summary>Natural-language ranking statistics use only entries allowed by the invocation path policy.</summary>
    [Fact]
    public async Task CodeExplore_NaturalLanguageQuery_UsesAllowedEntriesForRankingStatistics()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var request = new CodeExploreRequest
        {
            Query = "secret temporal transparency",
            Mode = CodeExploreMode.Survey,
            Limits = CreateWideLimits(),
        };

        var unrestricted = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            request,
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var restricted = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            request,
            fixture.CreateSourceReader("secret/**"),
            TestContext.Current.CancellationToken);

        Assert.True(restricted.Discovery?.CatalogEntryCount < unrestricted.Discovery?.CatalogEntryCount);
        Assert.DoesNotContain(restricted.CandidateSummaries ?? [], summary =>
            summary.Location?.FilePath.StartsWith("secret/", StringComparison.OrdinalIgnoreCase) == true
            || summary.FilePath?.StartsWith("secret/", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(restricted.FileSections, section =>
            section.FilePath.StartsWith("secret/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Local functions are not cataloged as top-level natural-language candidates with unresolvable stable ids.</summary>
    [Fact]
    public async Task CodeExplore_NaturalLanguageQuery_DoesNotSelectUnresolvableLocalFunctions()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "hidden local temporal helper",
                Mode = CodeExploreMode.Survey,
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.CandidateSummaries ?? [], summary =>
            summary.Symbol?.DisplayName.Contains("HiddenLocalTemporalHelper", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(result.ResolvedAnchors, resolution =>
            resolution.Input.Contains("HiddenLocalTemporalHelper", StringComparison.Ordinal));
    }

    /// <summary>The adapter bounds oversized code-explore metadata to the selected model budget.</summary>
    [Fact]
    public async Task CodeExploreTool_MetadataExceedsModelBudget_IsTrimmedConsistently()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan83-adapter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var identity = new SemanticSymbolIdentity("M:Example.Large.Item", "Example.Large.Item", "Method");
            var location = new CodeExploreLocation(
                "Example",
                "net10.0",
                "src/Example.cs",
                new SourceRange(1, 1, 1, 10),
                false,
                false);
            var fileSections = Enumerable.Range(0, 8)
                .Select(index => new CodeExploreFileSection(
                    $"src/Example{index}.cs",
                    "Example",
                    "net10.0",
                    [identity],
                    new CodeExploreSourceRange(
                        new SourceRange(1, 1, 1, 10),
                        [$"1: {new string('x', 400)}"],
                        new string('a', 64),
                        new string('b', 64),
                        CodeExploreSourceCompleteness.Complete,
                        [],
                        null),
                    false,
                    false,
                    "large test section"))
                .ToArray();
            var summaries = Enumerable.Range(0, 80)
                .Select(index => new CodeExploreCandidateSummary(
                    identity,
                    location,
                    location.FilePath,
                    CodeExploreCandidateTier.MultiTermStructural,
                    CodeExploreSelectionReason.MultiTerm,
                    index + 1,
                    index < 8,
                    new string('r', 200),
                    $"group-{index}"))
                .ToArray();
            var result = new CodeExploreResult(
                1,
                SemanticConfidenceLevel.FullSemantic,
                [new CodeExploreAnchorResolution("Example", CodeExploreAnchorKind.SymbolName, CodeExploreResolutionOutcome.Resolved, identity, location, [], "resolved")],
                fileSections,
                new CodeExploreCoverage(true, true, true, true, []),
                [],
                [],
                Flow: new CodeExploreFlow(
                    [],
                    [new CodeExploreFlowNode(identity, CodeExploreFlowNodeRole.NamedAnchor, 0, 7, true, false, [location])],
                    [],
                    [new CodeExploreDispatchBranch(identity, null, [new CodeExploreDispatchTarget(identity, location, 7)], 1, 1, [])],
                    [],
                    new SemanticTraversalSummary(1, 0, true, false, false, false, false, [])),
                QueryInterpretation: new CodeExploreQueryInterpretation([], [], [], [], ["large"], [], []),
                Discovery: new CodeExploreDiscoverySummary(80, 80, 8, true, false, [], "test"),
                CandidateSummaries: summaries,
                Allocation: new CodeExploreAllocationSummary(
                    50_000,
                    0,
                    fileSections.Sum(section => section.Source.NumberedLines.Sum(line => line.Length)),
                    "test",
                    fileSections.Select(section => new CodeExploreAllocationFileSummary(
                        section.FilePath,
                        16_384,
                        section.Source.NumberedLines.Sum(line => line.Length),
                        section.Source.Completeness,
                        true,
                        null)).ToArray()));
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                new ToolInvocationContext
                {
                    RepositoryPath = repositoryPath,
                    WorkspaceId = WorkspaceId.New(),
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    ApprovedRoots = ["."],
                    RequestedBy = "plan-83-tests",
                    ModelEffectiveInputBudgetTokens = 600,
                });

            var execution = await new CodeExploreTool(new StubCodeExploreService(result)).ExecuteAsync(
                new CodeExploreRequest { Query = "large" },
                context,
                TestContext.Current.CancellationToken);

            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(execution.Value);
            Assert.InRange(jsonBytes.Length, 0, 600 * 3);
            Assert.Contains(execution.Value.Omissions, omission => omission.Contains("selected model request budget", StringComparison.Ordinal));
            var retainedSectionCount = execution.Value.FileSections.Count;
            Assert.All(execution.Value.Flow?.Nodes ?? [], node =>
                Assert.True(node.SourceSectionIndex is null || node.SourceSectionIndex < retainedSectionCount));
            Assert.All(execution.Value.Flow?.DispatchBranches ?? [], branch =>
                Assert.All(branch.Implementations, target =>
                    Assert.True(target.SourceSectionIndex is null || target.SourceSectionIndex < retainedSectionCount)));
            Assert.All(execution.Value.Allocation?.Files ?? [], file =>
                Assert.Contains(execution.Value.FileSections, section => section.FilePath == file.FilePath));
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    /// <summary>Repeated complete unchanged source can be replaced with a precise current-request back-reference.</summary>
    [Fact]
    public async Task CodeExplore_VisibleFrontierSuppressesCompleteUnchangedSource()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var request = new CodeExploreRequest
        {
            Query = "Code.cs",
            PathAnchors = [new CodeExplorePathAnchor { Path = "src/Library/Code.cs", SelectionMode = CodeExplorePathSelectionMode.WholeFile }],
            Limits = CreateWideLimits(),
        };
        var first = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            request,
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var frontier = CreateVisibleFrontier(fixture, first);

        var repeated = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            request,
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken,
            frontier);

        Assert.Empty(repeated.FileSections);
        var backReference = Assert.Single(repeated.BackReferences ?? []);
        Assert.Equal("src/Library/Code.cs", backReference.FilePath);
        Assert.Equal(first.FileSections[0].Source.FileSha256, backReference.FileSha256);
        Assert.Equal(first.FileSections[0].Source.Range, backReference.Range);
        Assert.Equal(1, repeated.Deduplication?.SuppressedRanges);
        Assert.True(repeated.Deduplication?.ReclaimedCharacters > 0);
    }

    /// <summary>A covered subset back-reference hashes the exact advertised line range, not the larger covering section.</summary>
    [Fact]
    public async Task CodeExplore_VisibleFrontierSubsetBackReference_UsesSubsetRangeDigest()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var first = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Code.cs",
                PathAnchors = [new CodeExplorePathAnchor { Path = "src/Library/Code.cs", SelectionMode = CodeExplorePathSelectionMode.WholeFile }],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var frontier = CreateVisibleFrontier(fixture, first);
        var lines = await File.ReadAllLinesAsync(fixture.ResolvePath("src/Library/Code.cs"), TestContext.Current.CancellationToken);
        const int startLine = 1;
        const int endLine = 30;
        Assert.True(lines.Length > endLine);

        var repeated = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Code.cs subset",
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = "src/Library/Code.cs",
                        Line = startLine,
                        EndLine = endLine,
                        SelectionMode = CodeExplorePathSelectionMode.ExactLineRange,
                    },
                ],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken,
            frontier);

        Assert.Empty(repeated.FileSections);
        var backReference = Assert.Single(repeated.BackReferences ?? []);
        Assert.Equal(new SourceRange(startLine, 1, endLine, lines[endLine - 1].Length + 1), backReference.Range);
        Assert.Equal(ComputeLineRangeSha256(fixture, "src/Library/Code.cs", startLine, endLine), backReference.RangeSha256);
        Assert.NotEqual(first.FileSections[0].Source.RangeSha256, backReference.RangeSha256);
        Assert.Equal(1, repeated.Deduplication?.SuppressedRanges);
        Assert.Equal(0, repeated.Deduplication?.UsedForNewSourceCharacters);
    }

    /// <summary>Suppression reports only the later source admitted by the reclaimed budget, capped by reclaimed capacity.</summary>
    [Fact]
    public async Task CodeExplore_VisibleFrontierSuppression_ReportsReclaimedBudgetUsedForLaterSource()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var first = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "Code.cs",
                PathAnchors = [new CodeExplorePathAnchor { Path = "src/Library/Code.cs", SelectionMode = CodeExplorePathSelectionMode.WholeFile }],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var frontier = CreateVisibleFrontier(fixture, first);
        const string laterPath = "src/Library/Deeply/Nested/Verbose/Context/DeduplicationFragmentationCandidateWithExtraLongRepositoryRelativePath.cs";

        var repeated = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "reclaimed source budget",
                PathAnchors =
                [
                    new CodeExplorePathAnchor { Path = "src/Library/Code.cs", SelectionMode = CodeExplorePathSelectionMode.WholeFile },
                    new CodeExplorePathAnchor { Path = laterPath, SelectionMode = CodeExplorePathSelectionMode.WholeFile },
                ],
                Limits = CreateLimits(maximumSourceCharacters: 900, maximumPerFileSourceCharacters: 900),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken,
            frontier);

        Assert.DoesNotContain(repeated.FileSections, section => section.FilePath == "src/Library/Code.cs");
        Assert.Contains(repeated.FileSections, section => section.FilePath == laterPath);
        Assert.Single(repeated.BackReferences ?? []);
        var deduplication = Assert.IsType<CodeExploreDedupSummary>(repeated.Deduplication);
        Assert.True(deduplication.ReclaimedCharacters > 0);
        Assert.InRange(deduplication.UsedForNewSourceCharacters, 1, deduplication.ReclaimedCharacters);
        Assert.True(deduplication.UsedForNewSourceCharacters <= repeated.Allocation?.SpentSourceCharacters);
    }

    /// <summary>Changed current file content invalidates visible frontier coverage and omits stale source without counting re-emission.</summary>
    [Fact]
    public async Task CodeExplore_VisibleFrontierDigestMismatch_OmitsStaleSourceWithoutReEmissionCount()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var request = new CodeExploreRequest
        {
            Query = "Code.cs",
            PathAnchors = [new CodeExplorePathAnchor { Path = "src/Library/Code.cs", SelectionMode = CodeExplorePathSelectionMode.WholeFile }],
            Limits = CreateWideLimits(),
        };
        var first = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            request,
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var frontier = CreateVisibleFrontier(fixture, first);
        await File.AppendAllTextAsync(
            fixture.ResolvePath("src/Library/Code.cs"),
            Environment.NewLine + "// edited after visible source" + Environment.NewLine,
            TestContext.Current.CancellationToken);

        var repeated = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            request,
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken,
            frontier);

        var section = Assert.Single(repeated.FileSections);
        Assert.Empty(repeated.BackReferences ?? []);
        Assert.Equal(CodeExploreSourceCompleteness.Drifted, section.Source.Completeness);
        Assert.Empty(section.Source.NumberedLines);
        Assert.NotEqual(first.FileSections[0].Source.FileSha256, section.Source.FileSha256);
        Assert.Equal(0, repeated.Deduplication?.ReEmittedRanges);
        Assert.Equal(0, repeated.Deduplication?.UsedForNewSourceCharacters);
        Assert.Empty(repeated.Emissions ?? []);
        Assert.Contains(repeated.Deduplication?.Reasons ?? [], reason =>
            reason.Contains("Current file identity could not be proven", StringComparison.Ordinal)
            || reason.Contains("No exact unchanged visible source coverage", StringComparison.Ordinal));
    }

    /// <summary>Subset coverage is re-emitted when a back-reference would be larger than the candidate source.</summary>
    [Fact]
    public async Task CodeExplore_VisibleFrontierPartialOverlap_ReEmitsWhenPointerWouldFragment()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        const string path = "src/Library/Deeply/Nested/Verbose/Context/DeduplicationFragmentationCandidateWithExtraLongRepositoryRelativePath.cs";
        var first = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "fragmentation whole file",
                PathAnchors = [new CodeExplorePathAnchor { Path = path, SelectionMode = CodeExplorePathSelectionMode.WholeFile }],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var frontier = CreateVisibleFrontier(fixture, first);
        var lines = await File.ReadAllLinesAsync(fixture.ResolvePath(path), TestContext.Current.CancellationToken);
        var startLine = Array.FindIndex(lines, line => line.Contains("fragmentation-one", StringComparison.Ordinal)) + 1;
        Assert.True(startLine > 0);

        var repeated = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "fragmentation subset",
                PathAnchors =
                [
                    new CodeExplorePathAnchor
                    {
                        Path = path,
                        Line = startLine,
                        EndLine = startLine + 2,
                        SelectionMode = CodeExplorePathSelectionMode.ExactLineRange,
                    },
                ],
                Limits = CreateWideLimits(),
            },
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken,
            frontier);

        Assert.NotEmpty(repeated.FileSections);
        Assert.Empty(repeated.BackReferences ?? []);
        Assert.Equal(1, repeated.Deduplication?.ReEmittedRanges);
        Assert.Equal(0, repeated.Deduplication?.ReclaimedCharacters);
        Assert.Equal(0, repeated.Deduplication?.UsedForNewSourceCharacters);
        Assert.NotEmpty(repeated.Emissions ?? []);
        Assert.Contains(repeated.Deduplication?.Reasons ?? [], reason =>
            reason.Contains("would not save context", StringComparison.Ordinal));
    }

    /// <summary>Short repeated source spans are re-emitted instead of fragmented into larger pointers.</summary>
    [Fact]
    public async Task CodeExplore_VisibleFrontierShortSpan_ReEmitsSource()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var request = new CodeExploreRequest
        {
            Query = "Code.cs line",
            PathAnchors =
            [
                new CodeExplorePathAnchor
                {
                    Path = "src/Library/Code.cs",
                    Line = 1,
                    SelectionMode = CodeExplorePathSelectionMode.SingleLine,
                },
            ],
            Limits = CreateWideLimits(),
        };
        var first = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            request,
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken);
        var frontier = CreateVisibleFrontier(fixture, first);

        var repeated = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            request,
            fixture.CreateSourceReader(),
            TestContext.Current.CancellationToken,
            frontier);

        Assert.NotEmpty(repeated.FileSections);
        Assert.Empty(repeated.BackReferences ?? []);
        Assert.Equal(0, repeated.Deduplication?.UsedForNewSourceCharacters);
        Assert.Contains(repeated.Deduplication?.Reasons ?? [], reason =>
            reason.Contains("too small", StringComparison.Ordinal));
    }

    /// <summary>An opened but not-yet-loaded workspace returns actionable availability instead of throwing.</summary>
    [Fact]
    public async Task CodeExplore_UnloadedWorkspace_ReturnsAvailabilityInsteadOfThrowing()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan81-unavailable-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        await using var events = new DomainEventStream();
        await using var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
        try
        {
            var workspaceId = WorkspaceId.New();
            var service = new AdvancedSemanticQueryService(registry);
            var reader = new TestCodeExploreSourceReader(new ToolInvocationContext
            {
                RepositoryPath = repositoryPath,
                WorkspaceId = workspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                RequestedBy = "plan-81-tests",
            });

            var result = await service.QueryCodeExploreAsync(
                workspaceId,
                new CodeExploreRequest { Query = "Example.Worker", Limits = CreateWideLimits() },
                reader,
                TestContext.Current.CancellationToken);

            Assert.Equal(CodeExploreAvailabilityStatus.SemanticWorkspaceUnavailable, result.Availability?.Status);
            Assert.Equal(SemanticConfidenceLevel.None, result.Confidence);
            Assert.Empty(result.FileSections);
            Assert.Contains(
                result.Availability?.RecommendedActions ?? [],
                action => action.Kind == CodeExploreNextActionKind.WaitForWorkspace);
            Assert.Equal(0, reader.IsPathAllowedCallCount);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    /// <summary>Caller cancellation is observed before workspace-scale path inspection begins.</summary>
    [Fact]
    public async Task CodeExplore_CancelledBeforeScale_DoesNotInspectWorkspacePaths()
    {
        await using var fixture = await CodeExploreFixture.CreateAsync();
        var reader = fixture.CreateSourceReader();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await fixture.Service.QueryCodeExploreAsync(
                fixture.WorkspaceId,
                new CodeExploreRequest { Query = "Example.Worker", Limits = CreateWideLimits() },
                reader,
                cancellation.Token));

        Assert.Equal(0, reader.IsPathAllowedCallCount);
    }

    /// <summary>The tool adapter fails closed for null-location symbol metadata returned across the service boundary.</summary>
    [Fact]
    public async Task CodeExploreTool_NullLocationSymbolMetadata_IsOmittedByPolicyAdapter()
    {
        var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan81-adapter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repositoryPath);
        try
        {
            var identity = new SemanticSymbolIdentity("M:Secret.SecretType.Hidden", "Secret.SecretType.Hidden", "Method");
            var service = new StubCodeExploreService(new CodeExploreResult(
                1,
                SemanticConfidenceLevel.FullSemantic,
                [
                    new CodeExploreAnchorResolution(
                        "SecretType",
                        CodeExploreAnchorKind.SymbolName,
                        CodeExploreResolutionOutcome.Resolved,
                        identity,
                        null,
                        [new CodeExploreAlternative(identity, null)],
                        "stubbed null-location metadata"),
                ],
                [],
                new CodeExploreCoverage(true, true, true, true, []),
                [],
                []));
            var context = new ToolExecutionContext(
                ToolInvocationId.New(),
                SessionId.New(),
                RunId.New(),
                new ToolInvocationContext
                {
                    RepositoryPath = repositoryPath,
                    WorkspaceId = WorkspaceId.New(),
                    TrustLevel = RepositoryTrustLevel.TrustedBuild,
                    ApprovedRoots = ["."],
                    RequestedBy = "plan-81-tests",
                });

            var execution = await new CodeExploreTool(service).ExecuteAsync(
                new CodeExploreRequest
                {
                    Query = "SecretType",
                    ExactSymbolAnchors = ["SecretType"],
                },
                context,
                TestContext.Current.CancellationToken);

            var resolution = Assert.Single(execution.Value.ResolvedAnchors);
            Assert.Equal(CodeExploreResolutionOutcome.Omitted, resolution.Outcome);
            Assert.Null(resolution.SelectedSymbol);
            Assert.Empty(resolution.Alternatives);
            Assert.True(execution.IsTruncated);
        }
        finally
        {
            Directory.Delete(repositoryPath, recursive: true);
        }
    }

    private static CodeExploreLimits CreateWideLimits()
    {
        return CreateLimits();
    }

    private static CodeExploreLimits CreateLimits(
        int maximumAlternatives = 20,
        int maximumFiles = 8,
        int maximumSourceCharacters = 50_000,
        int maximumPerFileSourceCharacters = 16_384)
    {
        return new CodeExploreLimits
        {
            MaximumAlternatives = maximumAlternatives,
            MaximumFiles = maximumFiles,
            MaximumSourceCharacters = maximumSourceCharacters,
            MaximumPerFileSourceCharacters = maximumPerFileSourceCharacters,
            TimeoutMilliseconds = 10_000,
        };
    }

    private static void AssertSectionDigests(CodeExploreFixture fixture, CodeExploreFileSection section)
    {
        var fileText = File.ReadAllText(fixture.ResolvePath(section.FilePath));
        Assert.Equal(ComputeSha256(fileText), section.Source.FileSha256);
        var sourceText = SourceText.From(fileText, Encoding.UTF8);
        var startLineIndex = section.Source.Range.StartLine - 1;
        var endLineIndex = section.Source.Range.EndLine - 1;
        var returnedSpan = TextSpan.FromBounds(
            sourceText.Lines[startLineIndex].Start,
            sourceText.Lines[endLineIndex].EndIncludingLineBreak);
        Assert.Equal(ComputeSha256(sourceText.GetSubText(returnedSpan).ToString()), section.Source.RangeSha256);
    }

    private static int GetNumberedLineNumber(string numberedLine)
    {
        var delimiter = numberedLine.IndexOf(':', StringComparison.Ordinal);
        return int.Parse(numberedLine[..delimiter], CultureInfo.InvariantCulture);
    }

    private static void AssertSha256(string? value)
    {
        Assert.NotNull(value);
        Assert.Equal(64, value.Length);
        Assert.All(value, character => Assert.True(Uri.IsHexDigit(character)));
    }

    private static ModelVisibleSourceFrontier CreateVisibleFrontier(
        CodeExploreFixture fixture,
        CodeExploreResult result)
    {
        var entries = result.FileSections
            .Where(section => section.Source.Completeness == CodeExploreSourceCompleteness.Complete
                && section.Source.FileSha256 is not null)
            .Select(section => new ModelVisibleSourceEntry(
                "tool-result",
                "visible-tool-call",
                fixture.RepositoryPath,
                fixture.WorkspaceId,
                result.WorkspaceGeneration,
                section.FilePath,
                section.Source.Range,
                section.Source.FileSha256 ?? string.Empty,
                section.Source.RangeSha256,
                CountNumberedLineCharacters(section.Source.NumberedLines)))
            .ToArray();
        return new ModelVisibleSourceFrontier(
            fixture.RepositoryPath,
            fixture.WorkspaceId,
            0,
            entries,
            entries.Length,
            entries.Length,
            entries.Sum(entry => entry.EmittedCharacters));
    }

    private static string ComputeLineRangeSha256(
        CodeExploreFixture fixture,
        string path,
        int startLine,
        int endLine)
    {
        var fileText = File.ReadAllText(fixture.ResolvePath(path));
        var sourceText = SourceText.From(fileText, Encoding.UTF8);
        var returnedSpan = TextSpan.FromBounds(
            sourceText.Lines[startLine - 1].Start,
            sourceText.Lines[endLine - 1].EndIncludingLineBreak);
        return ComputeSha256(sourceText.GetSubText(returnedSpan).ToString());
    }

    private static int CountNumberedLineCharacters(IReadOnlyList<string> numberedLines)
    {
        var total = 0;
        for (var index = 0; index < numberedLines.Count; index++)
        {
            total += numberedLines[index].Length;
            if (index > 0)
            {
                total += Environment.NewLine.Length;
            }
        }

        return total;
    }

    private static string ComputeSha256(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class StubCodeExploreService : ICodeExploreService
    {
        private readonly CodeExploreResult _result;

        internal StubCodeExploreService(CodeExploreResult result)
        {
            ArgumentNullException.ThrowIfNull(result);
            _result = result;
        }

        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed class TestCodeExploreSourceReader : ICodeExploreSourceReader
    {
        private readonly ToolInvocationContext _context;

        internal TestCodeExploreSourceReader(ToolInvocationContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            _context = context;
        }

        public int IsPathAllowedCallCount { get; private set; }

        public bool IsPathAllowed(string path)
        {
            IsPathAllowedCallCount++;
            try
            {
                _ = NormalizeAndValidate(path);
                return true;
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException
                or IOException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
            {
                return false;
            }
        }

        public async Task<CodeExploreSourceText> ReadTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            var normalized = NormalizeAndValidate(path);
            var bytes = await File.ReadAllBytesAsync(normalized, cancellationToken);
            if (bytes.Length > maximumBytes)
            {
                throw new InvalidOperationException($"The source file exceeds the {maximumBytes}-byte code exploration read limit.");
            }

            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return new CodeExploreSourceText(normalized, text, ComputeSha256(bytes));
        }

        private string NormalizeAndValidate(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_context.RepositoryPath));
            var normalized = Path.GetFullPath(Path.IsPathRooted(path)
                ? path
                : Path.Combine(repositoryPath, path));
            if (!normalized.Equals(repositoryPath, PathComparison)
                && !normalized.StartsWith(repositoryPath + Path.DirectorySeparatorChar, PathComparison))
            {
                throw new UnauthorizedAccessException("Path is outside the repository.");
            }

            var relativePath = Path.GetRelativePath(repositoryPath, normalized).Replace('\\', '/');
            if (_context.ProhibitedPaths.Any(pattern => MatchesProhibitedPath(pattern, relativePath)))
            {
                throw new UnauthorizedAccessException("Path is prohibited by the test policy.");
            }

            return normalized;
        }

        private static bool MatchesProhibitedPath(string pattern, string relativePath)
        {
            var normalizedPattern = pattern.Replace('\\', '/');
            if (normalizedPattern.EndsWith("/**", StringComparison.Ordinal))
            {
                var prefix = normalizedPattern[..^3];
                return relativePath.Equals(prefix, PathComparison)
                    || relativePath.StartsWith(prefix + "/", PathComparison);
            }

            return relativePath.Equals(normalizedPattern, PathComparison);
        }

        private static string ComputeSha256(byte[] bytes)
        {
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private sealed class CodeExploreFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events;
        private readonly string _repositoryPath;

        private CodeExploreFixture(
            string repositoryPath,
            DomainEventStream events,
            SemanticEngineRegistry registry,
            WorkspaceId workspaceId)
        {
            _repositoryPath = repositoryPath;
            _events = events;
            Registry = registry;
            WorkspaceId = workspaceId;
            Service = new AdvancedSemanticQueryService(registry);
        }

        public AdvancedSemanticQueryService Service { get; }

        public SemanticEngineRegistry Registry { get; }

        public WorkspaceId WorkspaceId { get; }

        public string RepositoryPath => _repositoryPath;

        public static async Task<CodeExploreFixture> CreateAsync()
        {
            var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan81-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repositoryPath);
            Write(repositoryPath, "Repo.slnx", """
                <Solution>
                  <Project Path="src/Library/Library.csproj" />
                  <Project Path="src/LinkedOne/LinkedOne.csproj" />
                  <Project Path="src/LinkedTwo/LinkedTwo.csproj" />
                  <Project Path="src/LinkedThree/LinkedThree.csproj" />
                </Solution>
                """);
            Write(repositoryPath, "src/Library/Library.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="../../secret/Secret.cs" Link="Secret.cs" /></ItemGroup>
                </Project>
                """);
            Write(repositoryPath, "src/Library/Code.cs", """
                namespace Example;

                public sealed class Worker
                {
                    public string Run()
                    {
                        return "ok";
                    }
                }

                public sealed class TemporalTransparencyFlow
                {
                    public string BuildDefaultTemporalTransparency()
                    {
                        return ApplyDefaultTemporalFilter(CreateResponseTransparency());

                        static string HiddenLocalTemporalHelper()
                        {
                            return "local";
                        }
                    }

                    private static string ApplyDefaultTemporalFilter(string responseTransparency)
                    {
                        return $"temporal:{responseTransparency}";
                    }

                    private static string CreateResponseTransparency()
                    {
                        return "transparent";
                    }
                }

                public sealed class Overloads
                {
                    public string Handle(int value, string name)
                    {
                        return $"{value}:{name}";
                    }

                    public string Handle(ref int value, out string name)
                    {
                        value++;
                        name = value.ToString();
                        return name;
                    }

                    public string Inspect(in int value, ref readonly string name)
                    {
                        return $"{value}:{name}";
                    }
                }
                """);
            Write(repositoryPath, "src/Library/Deeply/Nested/Verbose/Context/DeduplicationFragmentationCandidateWithExtraLongRepositoryRelativePath.cs", """
                namespace Example.Deeply.Nested.Verbose.Context;

                public sealed class DeduplicationFragmentationCandidateWithExtraLongRepositoryRelativePath
                {
                    public string FragmentationCandidate()
                    {
                        // fragmentation-one alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu
                        // fragmentation-two alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu
                        // fragmentation-three alpha beta gamma delta epsilon zeta eta theta iota kappa lambda mu
                        return "fragment";
                    }
                }
                """);
            Write(repositoryPath, "secret/Secret.cs", """
                namespace Secret;

                public sealed class SecretType
                {
                    public string Hidden() => "hidden";
                }
                """);
            Write(repositoryPath, "shared/Linked.cs", """
                namespace Linked;

                public sealed class LinkedThing
                {
                    public string Value => "linked";
                }
                """);
            WriteLinkedProject(repositoryPath, "LinkedOne");
            WriteLinkedProject(repositoryPath, "LinkedTwo");
            WriteLinkedProject(repositoryPath, "LinkedThree");
            var events = new DomainEventStream();
            var registry = new SemanticEngineRegistry(events, NullLoggerFactory.Instance);
            var workspaceId = WorkspaceId.New();
            var load = await registry.LoadAsync(
                new SemanticLoadRequest(
                    SessionId.New(),
                    workspaceId,
                    repositoryPath,
                    Path.Combine(repositoryPath, "Repo.slnx"),
                    RepositoryTrustLevel.TrustedBuild),
                TestContext.Current.CancellationToken);
            Assert.True(load.Confidence >= SemanticConfidenceLevel.PartialCompilation, string.Join(Environment.NewLine, load.Diagnostics));
            return new(repositoryPath, events, registry, workspaceId);
        }

        public string ResolvePath(string relativePath)
        {
            return Path.Combine(_repositoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        public TestCodeExploreSourceReader CreateSourceReader(params string[] prohibitedPaths)
        {
            return new TestCodeExploreSourceReader(new ToolInvocationContext
            {
                RepositoryPath = _repositoryPath,
                WorkspaceId = WorkspaceId,
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                ProhibitedPaths = prohibitedPaths,
                RequestedBy = "plan-81-tests",
            });
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await _events.DisposeAsync();
            Directory.Delete(_repositoryPath, recursive: true);
        }

        private static void WriteLinkedProject(string root, string projectName)
        {
            Write(root, $"src/{projectName}/{projectName}.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><Compile Include="../../shared/Linked.cs" Link="Linked.cs" /></ItemGroup>
                </Project>
                """);
        }

        private static void Write(string root, string relativePath, string content)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? root);
            File.WriteAllText(fullPath, content);
        }
    }
}
