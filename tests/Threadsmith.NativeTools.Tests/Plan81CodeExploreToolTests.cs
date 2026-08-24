namespace Threadsmith.NativeTools.Tests;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
            CancellationToken cancellationToken = default)
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

        public bool IsPathAllowed(string path)
        {
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
