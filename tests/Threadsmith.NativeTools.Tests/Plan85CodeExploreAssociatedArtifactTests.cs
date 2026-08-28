namespace Threadsmith.NativeTools.Tests;

using System.ComponentModel;
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

/// <summary>Verifies Plan-85 associated non-C# artifact discovery, projection, and tool-policy boundaries.</summary>
public sealed class Plan85CodeExploreAssociatedArtifactTests
{
    /// <summary>Associated artifacts remain separate relationship-labeled supplements to the selected C# semantic slice.</summary>
    [Fact]
    public async Task CodeExplore_AssociatedArtifacts_ReturnProjectLiteralExactNameAndLogicalEvidence()
    {
        await using var fixture = await CodeExploreArtifactFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse prompt configuration project resource artifact",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
                Limits = CreateWideArtifactLimits(),
            },
            fixture.CreateArtifactReader(),
            TestContext.Current.CancellationToken);

        var source = Assert.Single(result.FileSections);
        Assert.Equal("src/App/ResponseBuilder.cs", source.FilePath);
        Assert.Contains(source.Source.NumberedLines, line => line.Contains("BuildWelcomeResponse", StringComparison.Ordinal));
        var artifacts = RequireArtifacts(result);

        AssertArtifact(
            artifacts,
            "src/App/Prompts/Welcome.prompt",
            CodeExploreArtifactRelationshipKind.PromptReference,
            CodeExploreArtifactEvidenceLevel.SourceLiteral,
            CodeExploreArtifactMediaKind.Prompt,
            "Hello {{name}}");
        AssertArtifact(
            artifacts,
            "src/App/Additional/Guide.md",
            CodeExploreArtifactRelationshipKind.AdditionalDocument,
            CodeExploreArtifactEvidenceLevel.ProjectProven,
            CodeExploreArtifactMediaKind.Markdown,
            "Additional guidance");
        AssertArtifact(
            artifacts,
            "src/App/App.csproj",
            CodeExploreArtifactRelationshipKind.ProjectItem,
            CodeExploreArtifactEvidenceLevel.ProjectProven,
            CodeExploreArtifactMediaKind.ProjectMetadata,
            "EmbeddedResource");
        AssertArtifact(
            artifacts,
            "src/App/Resources/Welcome.resx",
            CodeExploreArtifactRelationshipKind.ProjectResource,
            CodeExploreArtifactEvidenceLevel.BoundedTextualInference,
            CodeExploreArtifactMediaKind.Xml,
            "Welcome resource");
        AssertArtifact(
            artifacts,
            "src/App/Templates/SharedTemplate.prompt",
            CodeExploreArtifactRelationshipKind.BoundedExactNameInference,
            CodeExploreArtifactEvidenceLevel.BoundedTextualInference,
            CodeExploreArtifactMediaKind.Prompt,
            "Shared template");
        Assert.Contains(artifacts, artifact => artifact.Relationship == CodeExploreArtifactRelationshipKind.AnalyzerConfiguration
            && artifact.Evidence == CodeExploreArtifactEvidenceLevel.ProjectProven
            && artifact.FilePath == "src/App/.editorconfig");
        Assert.Contains(artifacts, artifact => artifact is
        {
            FilePath: null,
            LogicalName: "WelcomeEmail",
            Relationship: CodeExploreArtifactRelationshipKind.PromptReference,
            Evidence: CodeExploreArtifactEvidenceLevel.SourceLiteral,
            Content: null,
        });
        Assert.Contains(artifacts, artifact => artifact is
        {
            FilePath: null,
            LogicalName: "FeatureFlags:WelcomeMode",
            Relationship: CodeExploreArtifactRelationshipKind.ConfigurationReference,
            Evidence: CodeExploreArtifactEvidenceLevel.SourceLiteral,
            Content: null,
        });
        Assert.DoesNotContain(artifacts, artifact => string.Equals(artifact.LogicalName, "{{name}}", StringComparison.Ordinal));
        Assert.DoesNotContain(artifacts, artifact => string.Equals(artifact.LogicalName, "disabled", StringComparison.Ordinal));
        Assert.All(artifacts.Where(artifact => artifact.FilePath is not null), artifact =>
        {
            Assert.False(Path.IsPathRooted(artifact.FilePath));
            Assert.NotNull(artifact.Content);
            Assert.NotNull(artifact.Content.FileSha256);
            Assert.Equal(64, artifact.Content.FileSha256.Length);
        });
        var coverage = RequireArtifactCoverage(result);
        Assert.Equal(1, coverage.InspectedSourceAnchors);
        Assert.Equal(1, coverage.InspectedProjects);
        Assert.True(coverage.CandidateCount >= artifacts.Count);
        Assert.Equal(artifacts.Count, coverage.ReturnedCount);
        Assert.False(coverage.TimeLimitReached);
    }

    /// <summary>Default artifact limits prioritize selected-source references over broad project metadata.</summary>
    [Fact]
    public async Task CodeExplore_AssociatedArtifactsDefaultLimit_PrioritizesSelectedSourceEvidence()
    {
        await using var fixture = await CodeExploreArtifactFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse prompt configuration project resource artifact",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
            },
            fixture.CreateArtifactReader(),
            TestContext.Current.CancellationToken);

        var artifacts = RequireArtifacts(result);
        Assert.Equal(4, artifacts.Count);
        Assert.Contains(artifacts, artifact => artifact.FilePath == "src/App/Prompts/Welcome.prompt"
            && artifact.Relationship == CodeExploreArtifactRelationshipKind.PromptReference
            && artifact.Evidence == CodeExploreArtifactEvidenceLevel.SourceLiteral);
        Assert.Contains(artifacts, artifact => artifact.LogicalName == "WelcomeEmail"
            && artifact.Relationship == CodeExploreArtifactRelationshipKind.PromptReference);
        Assert.Contains(artifacts, artifact => artifact.Relationship == CodeExploreArtifactRelationshipKind.ConfigurationReference);
        Assert.DoesNotContain(artifacts, artifact => artifact.FilePath == "src/App/App.csproj");
        Assert.True(RequireArtifactCoverage(result).FileLimitReached);
    }

    /// <summary>Drifted semantic source is not reused to infer associated artifacts from stale Roslyn literals.</summary>
    [Fact]
    public async Task CodeExplore_DriftedSource_DoesNotSeedAssociatedArtifacts()
    {
        await using var fixture = await CodeExploreArtifactFixture.CreateAsync();
        var sourcePath = Path.Combine(fixture.RepositoryPath, "src", "App", "ResponseBuilder.cs");
        var source = await File.ReadAllTextAsync(sourcePath, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            sourcePath,
            source.Replace("Prompts/Welcome.prompt", "Prompts/Changed.prompt", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse prompt",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
                Limits = CreateWideArtifactLimits(),
            },
            fixture.CreateArtifactReader(),
            TestContext.Current.CancellationToken);

        var sourceSection = Assert.Single(result.FileSections);
        Assert.Equal(CodeExploreSourceCompleteness.Drifted, sourceSection.Source.Completeness);
        Assert.True(result.AssociatedArtifacts is null || result.AssociatedArtifacts.Count == 0);
        Assert.True(result.ArtifactCoverage is null || result.ArtifactCoverage.InspectedSourceAnchors == 0);
    }

    /// <summary>Associated artifact candidate limits are enforced before path probing and stop later discovery phases.</summary>
    [Fact]
    public async Task CodeExplore_AssociatedArtifactCandidateLimit_StopsBeforeExtraProbes()
    {
        await using var fixture = await CodeExploreArtifactFixture.CreateAsync();
        var reader = fixture.CreateArtifactReader();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse prompt configuration project resource artifact",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
                Limits = CreateWideArtifactLimits(
                    maximumAssociatedArtifactCharacters: 0,
                    maximumPerAssociatedArtifactCharacters: 0) with
                {
                    MaximumAssociatedArtifactCandidates = 2,
                },
            },
            reader,
            TestContext.Current.CancellationToken);

        var coverage = RequireArtifactCoverage(result);
        Assert.Equal(2, coverage.CandidateCount);
        Assert.True(coverage.CandidateLimitReached);
        Assert.True(reader.ProbeCalls <= coverage.CandidateCount, $"Expected path probes to stay within the candidate bound, got {reader.ProbeCalls} probes for {coverage.CandidateCount} candidates.");
        Assert.Contains(coverage.Omissions, omission => omission.Contains("candidate limit", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.AssociatedArtifacts ?? [], artifact => artifact.FilePath == "src/App/App.csproj");
    }

    /// <summary>The opt-out mode leaves the C# semantic source result unchanged and omits artifact metadata.</summary>
    [Fact]
    public async Task CodeExplore_AssociatedArtifactsDisabled_ReturnsOnlySemanticSource()
    {
        await using var fixture = await CodeExploreArtifactFixture.CreateAsync();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse prompt configuration project resource artifact",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Disabled,
                Limits = CreateWideArtifactLimits(),
            },
            fixture.CreateArtifactReader(),
            TestContext.Current.CancellationToken);

        Assert.Single(result.FileSections);
        Assert.Null(result.AssociatedArtifacts);
        Assert.Null(result.ArtifactCoverage);
        Assert.True(result.Coverage.SourceComplete);
    }

    /// <summary>Explicit artifact ranges carry current digests and bounded continuations independent of C# source limits.</summary>
    [Fact]
    public async Task CodeExplore_ExplicitAssociatedArtifactBounds_ReturnDigestAndContinuation()
    {
        await using var fixture = await CodeExploreArtifactFixture.CreateAsync();
        var full = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse prompt",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
                AssociatedArtifactPathAnchors =
                [
                    new CodeExploreArtifactPathAnchor
                    {
                        Path = "src/App/Prompts/Welcome.prompt",
                        Line = 1,
                        EndLine = 1,
                    },
                ],
                Limits = CreateWideArtifactLimits(maximumAssociatedArtifacts: 1),
            },
            fixture.CreateArtifactReader(),
            TestContext.Current.CancellationToken);

        var exactArtifact = Assert.Single(RequireArtifacts(full));
        Assert.Equal(CodeExploreArtifactRelationshipKind.ExplicitPath, exactArtifact.Relationship);
        Assert.Equal("src/App/Prompts/Welcome.prompt", exactArtifact.FilePath);
        var exactContent = exactArtifact.Content ?? throw new InvalidOperationException("Expected exact artifact content.");
        Assert.Equal(new SourceRange(1, 1, 1, 15), exactContent.Range);
        Assert.Contains("1: Hello {{name}}", exactContent.NumberedLines);
        Assert.Equal(64, exactContent.FileSha256.Length);
        Assert.Equal(CodeExploreSourceCompleteness.Complete, exactContent.Completeness);

        var bounded = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse prompt",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
                AssociatedArtifactPathAnchors =
                [
                    new CodeExploreArtifactPathAnchor
                    {
                        Path = "src/App/Prompts/Welcome.prompt",
                    },
                ],
                Limits = CreateWideArtifactLimits(
                    maximumAssociatedArtifacts: 1,
                    maximumAssociatedArtifactCharacters: 1,
                    maximumPerAssociatedArtifactCharacters: 1),
            },
            fixture.CreateArtifactReader(),
            TestContext.Current.CancellationToken);

        var coverage = RequireArtifactCoverage(bounded);
        Assert.True(coverage.CharacterLimitReached);
        var continuation = Assert.Single(coverage.ContinuationTargets, target => target.FilePath == "src/App/Prompts/Welcome.prompt"
            && target.ExpectedFileSha256 is not null);
        Assert.Equal(1, continuation.StartLine);
        Assert.True(continuation.EndLine >= continuation.StartLine);
        Assert.Equal(bounded.WorkspaceGeneration, continuation.WorkspaceGeneration);
        AssertSha256(continuation.ExpectedFileSha256);
    }

    /// <summary>Exact-name lookup is bounded across many distinct missing file-name literals.</summary>
    [Fact]
    public async Task CodeExplore_ExactNameLookupBounds_SkipExcessMissingLiterals()
    {
        await using var fixture = await CodeExploreArtifactFixture.CreateAsync(extraMissingExactNameLiterals: 40);
        var reader = fixture.CreateArtifactReader();

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse prompt templates",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
                Limits = CreateWideArtifactLimits(),
            },
            reader,
            TestContext.Current.CancellationToken);

        Assert.True(reader.ExactNameSearchCalls < 40, $"Expected bounded exact-name lookup calls, got {reader.ExactNameSearchCalls}.");
        Assert.Contains(
            RequireArtifactCoverage(result).Omissions,
            omission => omission.Contains("literal query bound", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Policy-denied associated candidates are reported with classified safe omissions and without sensitive path disclosure.</summary>
    [Fact]
    public async Task CodeExplore_PolicyDeniedAssociatedArtifact_ReportsSafeClassifiedOmission()
    {
        await using var fixture = await CodeExploreArtifactFixture.CreateAsync(includeSensitiveProjectItem: true);

        var result = await fixture.Service.QueryCodeExploreAsync(
            fixture.WorkspaceId,
            new CodeExploreRequest
            {
                Query = "BuildWelcomeResponse project artifact",
                ExactSymbolAnchors = ["ArtifactSample.ResponseBuilder.BuildWelcomeResponse"],
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
                Limits = CreateWideArtifactLimits(),
            },
            fixture.CreateArtifactReader(),
            TestContext.Current.CancellationToken);

        var coverage = RequireArtifactCoverage(result);
        Assert.False(coverage.Complete);
        var omission = Assert.Single(coverage.Omissions, item => item.Contains("sensitivity policy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("secrets/Hidden.prompt", omission, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.RepositoryPath, omission, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The tool adapter's artifact reader uses host-owned Git inventory, deterministic caps, policy probes, and strict textual decoding.</summary>
    [Fact]
    public async Task CodeExploreTool_ArtifactPolicyReader_UsesGitInventoryAndRejectsUnsafeText()
    {
        using var repository = new TemporaryRepository();
        repository.Write("Prompts/Valid.prompt", "safe prompt");
        repository.WriteBytes("Prompts/Control.prompt", [.. Encoding.UTF8.GetBytes("safe"), 0]);
        repository.WriteBytes("Prompts/Malformed.prompt", [0xFF, 0xFE, 0x00, 0xD8]);
        repository.Write("Templates/A/SharedTemplate.prompt", "A");
        repository.Write("Templates/Z/SharedTemplate.prompt", "Z");
        repository.Write("bin/Generated.prompt", "generated");
        repository.Write(".env", "not projected");
        var processManager = new RecordingProcessManager
        {
            Result = new ProcessExecutionResult(
                123,
                0,
                JsonSerializer.Serialize(new[]
                {
                    "Templates/Z/SharedTemplate.prompt",
                    "Templates/A/SharedTemplate.prompt",
                }),
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(1)),
        };
        var service = new ArtifactReaderInspectingService(repository.Path);
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = repository.Path,
                WorkspaceId = WorkspaceId.New(),
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                RequestedBy = "plan-85-tests",
            });

        var execution = await new CodeExploreTool(service, processManager).ExecuteAsync(
            new CodeExploreRequest
            {
                Query = "artifact policy probe",
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
            },
            context,
            TestContext.Current.CancellationToken);

        Assert.NotNull(execution.Value);
        Assert.True(service.ValidProbe?.IsSupported);
        Assert.Equal(CodeExploreArtifactMediaKind.Prompt, service.ValidProbe?.MediaKind);
        Assert.False(service.MissingProbe?.IsSupported);
        Assert.Contains("does not exist", service.MissingProbe?.RejectionReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.GeneratedProbe?.IsSupported);
        Assert.Contains("build-output", service.GeneratedProbe?.RejectionReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.SecretProbe?.IsSupported);
        Assert.Contains("sensitivity", service.SecretProbe?.RejectionReason ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidOperationException>(service.ControlArtifactException);
        Assert.IsType<DecoderFallbackException>(service.MalformedArtifactException);
        var exactMatch = Assert.Single(service.ExactNameSearch?.Matches ?? []);
        Assert.Equal(Path.Combine(repository.Path, "Templates", "A", "SharedTemplate.prompt"), exactMatch.Path);
        Assert.True(service.ExactNameSearch?.Truncated);
        Assert.Empty(service.MissingExactNameSearch?.Matches ?? [new CodeExploreArtifactFileMatch("unexpected", CodeExploreArtifactMediaKind.Text)]);
        var request = Assert.Single(processManager.Requests);
        Assert.Equal("git", request.FileName);
        Assert.Contains("ls-files", request.Arguments);
        Assert.Contains("--cached", request.Arguments);
        Assert.Contains("--others", request.Arguments);
        Assert.Contains("--exclude-standard", request.Arguments);
        Assert.Contains("-z", request.Arguments);
        Assert.Equal(ProcessRequestOrigin.Host, request.Origin);
        Assert.Contains("--literal-pathspecs", request.Arguments);
        Assert.Equal(ProcessStandardOutputFormat.NullDelimitedJsonArray, request.StandardOutputFormat);
        Assert.Equal("0", request.EnvironmentVariables["GIT_OPTIONAL_LOCKS"]);
        Assert.Equal("0", request.EnvironmentVariables["GIT_TERMINAL_PROMPT"]);
    }

    /// <summary>Git-backed exact-name inventory passes metacharacter directories under literal pathspec mode.</summary>
    [Fact]
    public async Task CodeExploreTool_GitInventory_UsesLiteralPathspecForMetacharacterDirectory()
    {
        using var repository = new TemporaryRepository();
        repository.Write("Meta[Dir]/Literal.prompt", "literal");
        var processManager = new RecordingProcessManager
        {
            Result = new ProcessExecutionResult(
                123,
                0,
                JsonSerializer.Serialize(new[] { "Meta[Dir]/Literal.prompt" }),
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(1)),
        };
        var service = new LiteralPathspecInspectingService(repository.Path);
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = repository.Path,
                WorkspaceId = WorkspaceId.New(),
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                RequestedBy = "plan-85-tests",
            });

        _ = await new CodeExploreTool(service, processManager).ExecuteAsync(
            new CodeExploreRequest
            {
                Query = "literal pathspec",
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
            },
            context,
            TestContext.Current.CancellationToken);

        var request = Assert.Single(processManager.Requests);
        Assert.Contains("--literal-pathspecs", request.Arguments);
        Assert.Equal("Meta[Dir]", request.Arguments[^1]);
        Assert.NotNull(service.SearchResult);
    }

    /// <summary>Model-budget artifact trimming preserves artifact continuations and returned-character accounting.</summary>
    [Fact]
    public async Task CodeExploreTool_ModelBudgetTrimmedArtifactContent_PreservesContinuationAndAccounting()
    {
        using var repository = new TemporaryRepository();
        var context = new ToolExecutionContext(
            ToolInvocationId.New(),
            SessionId.New(),
            RunId.New(),
            new ToolInvocationContext
            {
                RepositoryPath = repository.Path,
                WorkspaceId = WorkspaceId.New(),
                TrustLevel = RepositoryTrustLevel.TrustedBuild,
                ApprovedRoots = ["."],
                RequestedBy = "plan-85-tests",
                ModelEffectiveInputBudgetTokens = 2_000,
                ModelRequestOutputReserveTokens = 256,
            });

        var execution = await new CodeExploreTool(new OversizedArtifactResultService()).ExecuteAsync(
            new CodeExploreRequest
            {
                Query = "trim associated artifact",
                AssociatedArtifacts = CodeExploreAssociatedArtifactsMode.Enabled,
            },
            context,
            TestContext.Current.CancellationToken);

        var artifact = Assert.Single(RequireArtifacts(execution.Value));
        Assert.Null(artifact.Content);
        Assert.Contains(artifact.Omissions, omission => omission.Contains("model request budget", StringComparison.OrdinalIgnoreCase));
        var coverage = RequireArtifactCoverage(execution.Value);
        Assert.Equal(1, coverage.ReturnedCount);
        Assert.Equal(0, coverage.SpentCharacters);
        Assert.True(coverage.CharacterLimitReached);
        Assert.False(coverage.Complete);
        var continuation = Assert.Single(coverage.ContinuationTargets);
        Assert.Equal("Prompts/Huge.prompt", continuation.FilePath);
        Assert.Equal(1, continuation.StartLine);
        Assert.Equal(200, continuation.EndLine);
        AssertSha256(continuation.ExpectedFileSha256);
        Assert.Equal(execution.Value.WorkspaceGeneration, continuation.WorkspaceGeneration);
    }

    /// <summary>Associated-artifact controls remain host-owned and absent from the model-facing schema.</summary>
    [Fact]
    public void CodeExploreTool_InputSchema_ExcludesInternalAssociatedArtifactControls()
    {
        var schema = new CodeExploreTool(new EmptyCodeExploreService()).Definition.InputSchema.JsonSchema;

        Assert.DoesNotContain("associatedArtifacts", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("associatedArtifactPathAnchors", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumAssociatedArtifacts", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumAssociatedArtifactCandidates", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumAssociatedArtifactCharacters", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumPerAssociatedArtifactCharacters", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumAssociatedArtifactBytes", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("maximumAssociatedArtifactNameMatches", schema, StringComparison.Ordinal);
        _ = JsonDocument.Parse(schema);
    }

    private static CodeExploreLimits CreateWideArtifactLimits(
        int maximumAssociatedArtifacts = 16,
        int maximumAssociatedArtifactCharacters = 50_000,
        int maximumPerAssociatedArtifactCharacters = 16_384)
    {
        return new CodeExploreLimits
        {
            MaximumFiles = 8,
            MaximumSourceCharacters = 50_000,
            MaximumPerFileSourceCharacters = 16_384,
            MaximumAssociatedArtifacts = maximumAssociatedArtifacts,
            MaximumAssociatedArtifactCandidates = 64,
            MaximumAssociatedArtifactCharacters = maximumAssociatedArtifactCharacters,
            MaximumPerAssociatedArtifactCharacters = maximumPerAssociatedArtifactCharacters,
            MaximumAssociatedArtifactBytes = 128 * 1024,
            MaximumAssociatedArtifactNameMatches = 8,
            TimeoutMilliseconds = 10_000,
        };
    }

    private static IReadOnlyList<CodeExploreAssociatedArtifact> RequireArtifacts(CodeExploreResult result)
    {
        var artifacts = result.AssociatedArtifacts ?? throw new InvalidOperationException("Expected associated artifacts.");
        Assert.NotEmpty(artifacts);
        return artifacts;
    }

    private static CodeExploreArtifactCoverage RequireArtifactCoverage(CodeExploreResult result)
    {
        return result.ArtifactCoverage ?? throw new InvalidOperationException("Expected associated artifact coverage.");
    }

    private static void AssertArtifact(
        IReadOnlyList<CodeExploreAssociatedArtifact> artifacts,
        string path,
        CodeExploreArtifactRelationshipKind relationship,
        CodeExploreArtifactEvidenceLevel evidence,
        CodeExploreArtifactMediaKind mediaKind,
        string expectedContent)
    {
        var artifact = Assert.Single(artifacts, candidate => candidate.FilePath == path
            && candidate.Relationship == relationship
            && candidate.Evidence == evidence);
        Assert.Equal(mediaKind, artifact.MediaKind);
        Assert.NotEmpty(artifact.SelectionReasons);
        var content = artifact.Content ?? throw new InvalidOperationException($"Expected content for {path}.");
        Assert.Equal(CodeExploreSourceCompleteness.Complete, content.Completeness);
        Assert.Contains(content.NumberedLines, line => line.Contains(expectedContent, StringComparison.Ordinal));
        AssertSha256(content.FileSha256);
    }

    private static void AssertSha256(string? value)
    {
        Assert.NotNull(value);
        Assert.Equal(64, value.Length);
        Assert.All(value, character => Assert.True(Uri.IsHexDigit(character)));
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class CodeExploreArtifactFixture : IAsyncDisposable
    {
        private readonly DomainEventStream _events;

        private CodeExploreArtifactFixture(
            string repositoryPath,
            DomainEventStream events,
            SemanticEngineRegistry registry,
            WorkspaceId workspaceId)
        {
            RepositoryPath = repositoryPath;
            _events = events;
            Registry = registry;
            WorkspaceId = workspaceId;
            Service = new AdvancedSemanticQueryService(registry);
        }

        public string RepositoryPath { get; }

        public SemanticEngineRegistry Registry { get; }

        public AdvancedSemanticQueryService Service { get; }

        public WorkspaceId WorkspaceId { get; }

        public static async Task<CodeExploreArtifactFixture> CreateAsync(
            bool includeSensitiveProjectItem = false,
            int extraMissingExactNameLiterals = 0)
        {
            var repositoryPath = Path.Combine(Path.GetTempPath(), $"threadsmith-plan85-{Guid.NewGuid():N}");
            Directory.CreateDirectory(repositoryPath);
            Write(repositoryPath, "Repo.slnx", """
                <Solution>
                  <Project Path="src/App/App.csproj" />
                </Solution>
                """);
            var sensitiveItem = includeSensitiveProjectItem
                ? "    <Content Include=\"secrets/Hidden.prompt\" />\n"
                : string.Empty;
            var projectXml = string.Concat(
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <AdditionalFiles Include="Additional/Guide.md" />
                    <EditorConfigFiles Include=".editorconfig" />
                    <None Include="appsettings.json" />
                    <Content Include="Config/feature-flags.yaml" />
                    <EmbeddedResource Include="Resources/Welcome.resx" />
                """,
                sensitiveItem,
                """
                  </ItemGroup>
                </Project>
                """);
            var extraMissingLiterals = string.Concat(Enumerable.Range(1, extraMissingExactNameLiterals)
                .Select(index => $"        var missingTemplate{index} = \"missing-{index}.prompt\";\n"));
            Write(repositoryPath, "src/App/App.csproj", projectXml);
            var responseBuilderSource = string.Concat(
                """
                namespace ArtifactSample;

                public sealed class ResponseBuilder
                {
                    public string BuildWelcomeResponse(RequestContext context)
                    {
                        var prompt = PromptCatalog.Load("Prompts/Welcome.prompt");
                        var enabled = ConfigurationReader.GetBoolean("Features:WelcomeEnabled");
                        var logicalPrompt = PromptCatalog.Load("WelcomeEmail");
                        var mode = ConfigurationReader.GetString("FeatureFlags:WelcomeMode");
                        var exactTemplate = "SharedTemplate.prompt";
                """,
                extraMissingLiterals,
                """
                        var placeholder = "{{name}}";
                        return enabled && context.Enabled
                            ? ResponseFormatter.Format(prompt, context.Name, logicalPrompt, mode, exactTemplate, placeholder)
                            : "disabled";
                    }
                }

                public static class PromptCatalog
                {
                    public static string Load(string promptReference) => promptReference;
                }

                public static class ConfigurationReader
                {
                    public static bool GetBoolean(string key) => key == "Features:WelcomeEnabled";
                    public static string GetString(string key) => key;
                }

                public static class ResponseFormatter
                {
                    public static string Format(
                        string template,
                        string name,
                        string promptName,
                        string mode,
                        string sharedTemplate,
                        string placeholder)
                    {
                        return template.Replace(placeholder, name, System.StringComparison.Ordinal)
                            + $" [{promptName}:{mode}:{sharedTemplate}]";
                    }
                }

                public sealed record RequestContext(string Name, bool Enabled);
                """);
            Write(repositoryPath, "src/App/ResponseBuilder.cs", responseBuilderSource);
            Write(repositoryPath, "src/App/Prompts/Welcome.prompt", """
                Hello {{name}}
                Use the welcome voice.
                """);
            Write(repositoryPath, "src/App/Additional/Guide.md", """
                # Additional guidance
                Keep answers concise.
                """);
            Write(repositoryPath, "src/App/.editorconfig", """
                root = true
                [*.cs]
                dotnet_diagnostic.CA1822.severity = warning
                """);
            Write(repositoryPath, "src/App/appsettings.json", """
                {
                  "Features": { "WelcomeEnabled": true },
                  "FeatureFlags": { "WelcomeMode": "friendly" }
                }
                """);
            Write(repositoryPath, "src/App/Config/feature-flags.yaml", """
                welcomeMode: friendly
                """);
            Write(repositoryPath, "src/App/Resources/Welcome.resx", """
                <?xml version="1.0" encoding="utf-8"?>
                <root><data name="Greeting"><value>Welcome resource</value></data></root>
                """);
            Write(repositoryPath, "src/App/Templates/SharedTemplate.prompt", """
                Shared template
                """);
            if (includeSensitiveProjectItem)
            {
                Write(repositoryPath, "src/App/secrets/Hidden.prompt", "do not expose");
            }

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

        public TestCodeExploreArtifactReader CreateArtifactReader(params string[] prohibitedPaths)
        {
            return new TestCodeExploreArtifactReader(RepositoryPath, prohibitedPaths);
        }

        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await _events.DisposeAsync();
            Directory.Delete(RepositoryPath, recursive: true);
        }

        private static void Write(string root, string relativePath, string content)
        {
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? root);
            File.WriteAllText(fullPath, content);
        }
    }

    private sealed class TestCodeExploreArtifactReader : ICodeExploreSourceReader, ICodeExploreArtifactReader
    {
        private static readonly HashSet<string> ExcludedDirectories = new(
            [".codegraph", ".git", "bin", "obj", "node_modules"],
            StringComparer.OrdinalIgnoreCase);

        private readonly string[] _prohibitedPaths;

        internal TestCodeExploreArtifactReader(string repositoryPath, IReadOnlyList<string> prohibitedPaths)
        {
            RepositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            _prohibitedPaths = [.. prohibitedPaths];
        }

        private string RepositoryPath { get; }

        public bool IsPathAllowed(string path)
        {
            return TryNormalize(path, out _);
        }

        public bool IsSupportedTextArtifactPath(string path)
        {
            return ProbeArtifactPath(path).IsSupported;
        }

        public int ProbeCalls { get; private set; }

        public CodeExploreArtifactPathProbe ProbeArtifactPath(string path)
        {
            ProbeCalls++;
            if (!TryNormalize(path, out var normalized))
            {
                return new CodeExploreArtifactPathProbe(false, null, "path is outside the approved repository scope or is prohibited by invocation policy.");
            }

            if (GetPolicyRejection(normalized) is { } reason)
            {
                return new CodeExploreArtifactPathProbe(false, null, reason);
            }

            if (!TryClassifyMedia(normalized, out var mediaKind))
            {
                return new CodeExploreArtifactPathProbe(false, null, "media type is not supported for associated artifacts.");
            }

            var info = new FileInfo(normalized);
            if (!info.Exists)
            {
                return new CodeExploreArtifactPathProbe(false, null, "file does not exist.");
            }

            if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                return new CodeExploreArtifactPathProbe(false, null, "path is not a regular non-reparse file.");
            }

            return new CodeExploreArtifactPathProbe(true, mediaKind, null);
        }

        public async Task<CodeExploreSourceText> ReadTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(path);
            var bytes = await ReadBoundedBytesAsync(normalized, maximumBytes, cancellationToken);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return new CodeExploreSourceText(normalized, text, ComputeSha256(bytes));
        }

        public async Task<CodeExploreArtifactText> ReadArtifactTextAsync(
            string path,
            int maximumBytes,
            CancellationToken cancellationToken = default)
        {
            var probe = ProbeArtifactPath(path);
            if (!probe.IsSupported || probe.MediaKind is null)
            {
                throw new UnauthorizedAccessException(probe.RejectionReason ?? "artifact denied by policy");
            }

            var normalized = Normalize(path);
            var bytes = await ReadBoundedBytesAsync(normalized, maximumBytes, cancellationToken);
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return new CodeExploreArtifactText(normalized, text, ComputeSha256(bytes), probe.MediaKind.Value, bytes.Length);
        }

        public int ExactNameSearchCalls { get; private set; }

        public Task<CodeExploreArtifactFileSearchResult> FindArtifactFilesByNameAsync(
            string directoryPath,
            string fileName,
            int maximumMatches,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExactNameSearchCalls++;
            var directory = Normalize(directoryPath);
            var matches = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Where(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                .Where(IsSupportedTextArtifactPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(path => new CodeExploreArtifactFileMatch(path, ProbeArtifactPath(path).MediaKind ?? CodeExploreArtifactMediaKind.Text))
                .ToArray();
            return Task.FromResult(new CodeExploreArtifactFileSearchResult(
                matches.Take(maximumMatches).ToArray(),
                matches.Length,
                matches.Length > maximumMatches));
        }

        private static async Task<byte[]> ReadBoundedBytesAsync(
            string normalized,
            int maximumBytes,
            CancellationToken cancellationToken)
        {
            var bytes = await File.ReadAllBytesAsync(normalized, cancellationToken);
            if (bytes.Length > maximumBytes)
            {
                throw new InvalidOperationException("The file exceeds the code exploration read limit.");
            }

            return bytes;
        }

        private string Normalize(string path)
        {
            if (TryNormalize(path, out var normalized))
            {
                return normalized;
            }

            throw new UnauthorizedAccessException("Path is outside the repository.");
        }

        private bool TryNormalize(string path, out string normalized)
        {
            normalized = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var candidate = Path.GetFullPath(Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(RepositoryPath, path));
                if (!candidate.Equals(RepositoryPath, PathComparison)
                    && !candidate.StartsWith(RepositoryPath + Path.DirectorySeparatorChar, PathComparison))
                {
                    return false;
                }

                var relative = Path.GetRelativePath(RepositoryPath, candidate).Replace('\\', '/');
                if (_prohibitedPaths.Any(pattern => MatchesPattern(pattern, relative)))
                {
                    return false;
                }

                normalized = candidate;
                return true;
            }
            catch (Exception exception) when (exception is ArgumentException
                or IOException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
            {
                return false;
            }
        }

        private string? GetPolicyRejection(string normalized)
        {
            var relative = Path.GetRelativePath(RepositoryPath, normalized).Replace('\\', '/');
            var parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Any(ExcludedDirectories.Contains))
            {
                return "path is in a Git, generated, dependency, or build-output directory.";
            }

            if (parts.Any(IsSensitiveName))
            {
                return "path is excluded by secret or credential sensitivity policy.";
            }

            return null;
        }

        private static bool TryClassifyMedia(string path, out CodeExploreArtifactMediaKind mediaKind)
        {
            var fileName = Path.GetFileName(path);
            var extension = Path.GetExtension(path);
            if (fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".config", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.Configuration;
                return true;
            }

            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = fileName.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                    ? CodeExploreArtifactMediaKind.Configuration
                    : CodeExploreArtifactMediaKind.Json;
                return true;
            }

            if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.ProjectMetadata;
                return true;
            }

            if (extension.Equals(".resx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".xml", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.Xml;
                return true;
            }

            if (extension.Equals(".md", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.Markdown;
                return true;
            }

            if (extension.Equals(".prompt", StringComparison.OrdinalIgnoreCase))
            {
                mediaKind = CodeExploreArtifactMediaKind.Prompt;
                return true;
            }

            mediaKind = CodeExploreArtifactMediaKind.Text;
            return false;
        }

        private static bool MatchesPattern(string pattern, string relativePath)
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

        private static bool IsSensitiveName(string value)
        {
            return value.Equals(".env", StringComparison.OrdinalIgnoreCase)
                || value.Contains("secret", StringComparison.OrdinalIgnoreCase)
                || value.Contains("credential", StringComparison.OrdinalIgnoreCase)
                || value.Contains("token", StringComparison.OrdinalIgnoreCase);
        }

        private static StringComparison PathComparison => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
    }

    private sealed class ArtifactReaderInspectingService : ICodeExploreService
    {
        private readonly string _repositoryPath;

        internal ArtifactReaderInspectingService(string repositoryPath)
        {
            _repositoryPath = repositoryPath;
        }

        public CodeExploreArtifactPathProbe? ValidProbe { get; private set; }

        public CodeExploreArtifactPathProbe? MissingProbe { get; private set; }

        public CodeExploreArtifactPathProbe? GeneratedProbe { get; private set; }

        public CodeExploreArtifactPathProbe? SecretProbe { get; private set; }

        public Exception? ControlArtifactException { get; private set; }

        public Exception? MalformedArtifactException { get; private set; }

        public CodeExploreArtifactFileSearchResult? ExactNameSearch { get; private set; }

        public CodeExploreArtifactFileSearchResult? MissingExactNameSearch { get; private set; }

        public async Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifactReader = Assert.IsAssignableFrom<ICodeExploreArtifactReader>(sourceReader);
            ValidProbe = artifactReader.ProbeArtifactPath("Prompts/Valid.prompt");
            MissingProbe = artifactReader.ProbeArtifactPath("Prompts/Missing.prompt");
            GeneratedProbe = artifactReader.ProbeArtifactPath("bin/Generated.prompt");
            SecretProbe = artifactReader.ProbeArtifactPath(".env");
            ControlArtifactException = await CaptureExceptionAsync(() => artifactReader.ReadArtifactTextAsync(
                "Prompts/Control.prompt",
                1024,
                cancellationToken));
            MalformedArtifactException = await CaptureExceptionAsync(() => artifactReader.ReadArtifactTextAsync(
                "Prompts/Malformed.prompt",
                1024,
                cancellationToken));
            var templatesPath = Path.Combine(_repositoryPath, "Templates");
            ExactNameSearch = await artifactReader.FindArtifactFilesByNameAsync(
                templatesPath,
                "SharedTemplate.prompt",
                1,
                cancellationToken);
            MissingExactNameSearch = await artifactReader.FindArtifactFilesByNameAsync(
                templatesPath,
                "Missing.prompt",
                1,
                cancellationToken);
            return new CodeExploreResult(
                1,
                SemanticConfidenceLevel.FullSemantic,
                [],
                [],
                new CodeExploreCoverage(true, true, true, true, []),
                [],
                []);
        }

        private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
        {
            try
            {
                await action();
                return null;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                or DecoderFallbackException
                or UnauthorizedAccessException
                or IOException)
            {
                return exception;
            }
        }
    }

    private sealed class EmptyCodeExploreService : ICodeExploreService
    {
        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CodeExploreResult(
                1,
                SemanticConfidenceLevel.FullSemantic,
                [],
                [],
                new CodeExploreCoverage(true, true, true, true, []),
                [],
                []));
        }
    }

    private sealed class LiteralPathspecInspectingService : ICodeExploreService
    {
        private readonly string _repositoryPath;

        internal LiteralPathspecInspectingService(string repositoryPath)
        {
            _repositoryPath = repositoryPath;
        }

        public CodeExploreArtifactFileSearchResult? SearchResult { get; private set; }

        public async Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var artifactReader = Assert.IsAssignableFrom<ICodeExploreArtifactReader>(sourceReader);
            SearchResult = await artifactReader.FindArtifactFilesByNameAsync(
                Path.Combine(_repositoryPath, "Meta[Dir]"),
                "Literal.prompt",
                1,
                cancellationToken);
            return new CodeExploreResult(
                1,
                SemanticConfidenceLevel.FullSemantic,
                [],
                [],
                new CodeExploreCoverage(true, true, true, true, []),
                [],
                []);
        }
    }

    private sealed class OversizedArtifactResultService : ICodeExploreService
    {
        public Task<CodeExploreResult> QueryCodeExploreAsync(
            WorkspaceId workspaceId,
            CodeExploreRequest request,
            ICodeExploreSourceReader sourceReader,
            CancellationToken cancellationToken = default,
            ModelVisibleSourceFrontier? visibleSourceFrontier = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var numberedLines = Enumerable.Range(1, 200)
                .Select(index => $"{index}: {new string('x', 80)}")
                .ToArray();
            var returnedCharacters = string.Join(Environment.NewLine, numberedLines).Length;
            var fileSha256 = ComputeSha256(Encoding.UTF8.GetBytes("huge-artifact"));
            var content = new CodeExploreArtifactContent(
                new SourceRange(1, 1, 200, 81),
                numberedLines,
                fileSha256,
                ComputeSha256(Encoding.UTF8.GetBytes(string.Join('\n', numberedLines))),
                CodeExploreSourceCompleteness.Complete,
                [],
                null,
                returnedCharacters);
            CodeExploreAssociatedArtifact[] artifacts =
            [
                new(
                    "Prompts/Huge.prompt",
                    CodeExploreArtifactMediaKind.Prompt,
                    "App",
                    "M:ArtifactSample.ResponseBuilder.BuildWelcomeResponse",
                    "src/App/ResponseBuilder.cs",
                    new SourceRange(1, 1, 10, 6),
                    CodeExploreArtifactRelationshipKind.PromptReference,
                    CodeExploreArtifactEvidenceLevel.SourceLiteral,
                    ["Selected C# source literal references 'Prompts/Huge.prompt'."],
                    content,
                    []),
            ];
            var coverage = new CodeExploreArtifactCoverage(
                1,
                1,
                0,
                1,
                1,
                0,
                returnedCharacters,
                true,
                false,
                false,
                false,
                false,
                [],
                []);
            return Task.FromResult(new CodeExploreResult(
                1,
                SemanticConfidenceLevel.FullSemantic,
                [],
                [],
                new CodeExploreCoverage(true, true, true, true, []),
                [],
                [],
                AssociatedArtifacts: artifacts,
                ArtifactCoverage: coverage));
        }
    }

    private sealed class RecordingProcessManager : IProcessManager
    {
        public ProcessExecutionResult Result { get; init; } = new(
            0,
            0,
            string.Empty,
            string.Empty,
            false,
            false,
            false,
            TimeSpan.Zero);

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Result);
        }
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"threadsmith-plan85-tool-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }

        public void Write(string relativePath, string content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath) ?? Path);
            File.WriteAllText(fullPath, content);
        }

        public void WriteBytes(string relativePath, byte[] content)
        {
            var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath) ?? Path);
            File.WriteAllBytes(fullPath, content);
        }
    }
}
