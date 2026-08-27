namespace Threadsmith.Validation.Tests;

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Tools;
using Threadsmith.Tui;
using Threadsmith.Validation;
using Xunit;

/// <summary>Verifies M6 build diagnostics, classification, correlation, gates, and bounded correction.</summary>
public sealed class Milestone6Tests
{
    /// <summary>Full-semantic classification distinguishes existing and introduced diagnostics.</summary>
    [Fact]
    public void DiagnosticClassifier_FullSemantic_DistinguishesBaselineAndIntroduced()
    {
        var baselineDiagnostic = CreateDiagnostic("baseline", "CS1001", "Expected identifier");
        var introducedDiagnostic = CreateDiagnostic("introduced", "CS1503", "Cannot convert argument");
        var capture = new BaselineCapture(
            new WorkspaceId(Guid.NewGuid()),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            [baselineDiagnostic]);

        var result = DiagnosticClassifier.Classify(
            capture,
            [baselineDiagnostic, introducedDiagnostic],
            SemanticConfidenceLevel.FullSemantic);

        Assert.Equal(DiagnosticClassification.Baseline, result[0].Classification);
        Assert.True(result[0].IsBaselineDiagnostic);
        Assert.Equal(DiagnosticClassification.Introduced, result[1].Classification);
        Assert.False(result[1].IsBaselineDiagnostic);
    }

    /// <summary>Degraded confidence prevents authoritative classification and requires human confirmation.</summary>
    [Fact]
    public void DiagnosticClassifier_PartialCompilation_RequiresHumanConfirmation()
    {
        var diagnostic = CreateDiagnostic("introduced", "CS1503", "Cannot convert argument");
        var capture = new BaselineCapture(
            new WorkspaceId(Guid.NewGuid()),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            []);
        var classified = DiagnosticClassifier.Classify(
            capture,
            [diagnostic],
            SemanticConfidenceLevel.PartialCompilation);

        var gate = AcceptanceGate.Evaluate(new AcceptanceGateRequest
        {
            Diagnostics = classified,
            RequiredStagesCompleted = true,
            FinalDiffAvailable = true,
            RequiredApprovalsPresent = true,
        });

        Assert.Equal(DiagnosticClassification.ConfidenceDegraded, classified[0].Classification);
        Assert.Equal(SemanticConfidenceLevel.PartialCompilation, classified[0].Confidence);
        Assert.Equal(AcceptanceGateStatus.HumanConfirmationRequired, gate.Status);
    }

    /// <summary>Authoritative introduced errors fail the acceptance gate.</summary>
    [Fact]
    public void AcceptanceGate_IntroducedError_Fails()
    {
        var diagnostic = CreateDiagnostic("introduced", "CS1503", "Cannot convert argument") with
        {
            Classification = DiagnosticClassification.Introduced,
        };

        var result = AcceptanceGate.Evaluate(new AcceptanceGateRequest
        {
            Diagnostics = [diagnostic],
            RequiredStagesCompleted = true,
            FinalDiffAvailable = true,
            RequiredApprovalsPresent = true,
        });

        Assert.Equal(AcceptanceGateStatus.Failed, result.Status);
        Assert.Contains("introduced", result.Reasons[0], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Diagnostic correlation carries mutation and confidence-eligible symbol identities.</summary>
    [Fact]
    public void DiagnosticCorrelator_MatchingFile_CarriesMutationAndSymbol()
    {
        var mutationId = new MutationId(Guid.NewGuid());
        var diagnostic = CreateDiagnostic("introduced", "CS1503", "Cannot convert argument") with
        {
            Classification = DiagnosticClassification.Introduced,
        };
        var mutationSet = CreateMutationSet(mutationId, "symbol-id");

        var result = DiagnosticCorrelator.Correlate([diagnostic], mutationSet);

        Assert.Equal(mutationId, result[0].RelatedMutationId);
        Assert.Equal("symbol-id", result[0].RelatedSymbolId);
    }

    /// <summary>Symbol correlation is omitted below partial-compilation confidence.</summary>
    [Fact]
    public void DiagnosticCorrelator_TextOnly_OmitsSymbol()
    {
        var mutationId = new MutationId(Guid.NewGuid());
        var diagnostic = CreateDiagnostic("degraded", "CS1503", "Cannot convert argument") with
        {
            Confidence = SemanticConfidenceLevel.TextOnly,
        };

        var result = DiagnosticCorrelator.Correlate(
            [diagnostic],
            CreateMutationSet(mutationId, "symbol-id"));

        Assert.Equal(mutationId, result[0].RelatedMutationId);
        Assert.Null(result[0].RelatedSymbolId);
    }

    /// <summary>Affected-graph traversal includes transitive project dependents.</summary>
    [Fact]
    public void AffectedProjectCalculator_ChangedLibrary_IncludesDependentProjects()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-affected-{Guid.NewGuid():N}");
        var corePath = Path.Combine(root, "src", "Core", "Core.csproj");
        var appPath = Path.Combine(root, "src", "App", "App.csproj");
        var testPath = Path.Combine(root, "tests", "Core.Tests", "Core.Tests.csproj");
        SemanticProjectInfo[] projects =
        [
            new SemanticProjectInfo(
                "Core",
                corePath,
                ["net10.0"],
                SemanticConfidenceLevel.FullSemantic,
                [],
                []),
            new SemanticProjectInfo(
                "App",
                appPath,
                ["net10.0"],
                SemanticConfidenceLevel.FullSemantic,
                ["../Core/Core.csproj"],
                []),
            new SemanticProjectInfo(
                "Core.Tests",
                testPath,
                ["net10.0"],
                SemanticConfidenceLevel.FullSemantic,
                ["../../src/Core/Core.csproj"],
                []),
        ];

        var result = AffectedProjectCalculator.Calculate(
            root,
            ["src/Core/Service.cs"],
            projects);

        Assert.Equal(3, result.Projects.Count);
        Assert.True(result.Projects.Single(project => project.Name == "Core").IsDirectlyChanged);
        Assert.False(result.Projects.Single(project => project.Name == "App").IsDirectlyChanged);
        Assert.Empty(result.UnmappedFiles);
    }

    /// <summary>Compiler output is normalized into repository-relative structured diagnostics.</summary>
    [Fact]
    public void DiagnosticNormalizer_CompilerLine_ProducesStableDto()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-normalizer-{Guid.NewGuid():N}");
        var file = Path.Combine(root, "Services", "ExampleService.cs");
        var project = Path.Combine(root, "Example.Core.csproj");
        var output = $"{file}(47,23,47,29): error CS1503: Cannot convert argument [{project}]";

        var first = DiagnosticNormalizer.Normalize(
            output,
            root,
            "Fallback",
            "net10.0",
            SemanticConfidenceLevel.FullSemantic);
        var second = DiagnosticNormalizer.Normalize(
            output,
            root,
            "Fallback",
            "net10.0",
            SemanticConfidenceLevel.FullSemantic);

        var diagnostic = Assert.Single(first);
        Assert.Equal("CS1503", diagnostic.Code);
        Assert.Equal("Example.Core", diagnostic.Project);
        Assert.Equal("Services/ExampleService.cs", diagnostic.File);
        Assert.Equal(47, diagnostic.Range?.StartLine);
        Assert.Equal(diagnostic.Id, Assert.Single(second).Id);
    }

    /// <summary>Build execution rejects repositories that have not granted build trust.</summary>
    [Fact]
    public async Task BuildExecutor_TrustedRead_RejectsBeforeExecution()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedRead);

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(
            new BuildValidationRequest
            {
                SessionId = new SessionId(Guid.NewGuid()),
                RunId = new RunId(Guid.NewGuid()),
                Baseline = baseline,
                Confidence = SemanticConfidenceLevel.FullSemantic,
            }));

        Assert.Contains("TrustedBuild", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Build execution rejects targets excluded by repository path policy.</summary>
    [Fact]
    public async Task BuildExecutor_ProhibitedTarget_RejectsBeforeExecution()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild) with
        {
            ProhibitedPaths = ["src/**"],
        };

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(
            new BuildValidationRequest
            {
                SessionId = new SessionId(Guid.NewGuid()),
                RunId = new RunId(Guid.NewGuid()),
                Baseline = baseline,
                Confidence = SemanticConfidenceLevel.FullSemantic,
            }));

        Assert.Contains("prohibited", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A trusted baseline build captures diagnostics and confidence against the immutable baseline.</summary>
    [Fact]
    public async Task BaselineBuildCapture_TrustedBuild_RecordsCaptureTimeConfidence()
    {
        await using var events = new DomainEventStream();
        var capture = new BaselineBuildCapture(new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance));
        var baseline = await CreateBuildableBaselineAsync(includeDelay: false);
        try
        {
            var result = await capture.CaptureAsync(new BuildValidationRequest
            {
                SessionId = new SessionId(Guid.NewGuid()),
                RunId = new RunId(Guid.NewGuid()),
                Baseline = baseline,
                Confidence = SemanticConfidenceLevel.FullSemantic,
            });

            Assert.Equal(baseline.WorkspaceId, result.WorkspaceId);
            Assert.Equal(baseline.CapturedAt, result.BaselineCapturedAt);
            Assert.Equal(SemanticConfidenceLevel.FullSemantic, result.Confidence);
            Assert.Empty(result.Diagnostics);
        }
        finally
        {
            Directory.Delete(baseline.RepositoryPath, recursive: true);
        }
    }

    /// <summary>The validation pipeline returns a passing gate for a clean affected-project build.</summary>
    [Fact]
    public async Task ValidationPipeline_CleanBuild_ReturnsPassingEvidence()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = await CreateBuildableBaselineAsync(includeDelay: false);
        try
        {
            var request = new BuildValidationRequest
            {
                SessionId = new SessionId(Guid.NewGuid()),
                RunId = new RunId(Guid.NewGuid()),
                Baseline = baseline,
                Confidence = SemanticConfidenceLevel.FullSemantic,
                Stages =
                [
                    MutationValidationStage.Compile,
                    MutationValidationStage.Diagnostics,
                ],
            };
            var capture = await new BaselineBuildCapture(executor).CaptureAsync(request);
            var mutationSet = new MutationSet
            {
                MutationSetId = new MutationSetId(Guid.NewGuid()),
                SessionId = request.SessionId,
                RunId = request.RunId,
                WorkspaceId = baseline.WorkspaceId,
                BaselineCapturedAt = baseline.CapturedAt,
                Rationale = "Validate a clean build.",
                Mutations =
                [
                    new Mutation
                    {
                        MutationId = new MutationId(Guid.NewGuid()),
                        Type = MutationType.ReplaceText,
                        RelativePath = "Program.cs",
                        BaselineSha256 = new string('0', 64),
                        ReplacementText = "unchanged",
                    },
                ],
            };
            var pipeline = new ValidationPipeline(
                executor,
                new DiagnosticClassifier(),
                new DiagnosticCorrelator(),
                new AcceptanceGate(),
                CreateTestPipeline(events),
                events);

            var result = await pipeline.ValidateAsync(
                request,
                capture,
                mutationSet,
                requiredApprovalsPresent: true,
                finalDiffAvailable: true);

            Assert.True(result.Build.Succeeded);
            Assert.True(result.Tests.Completed);
            Assert.Contains(
                result.Tests.Selection.Rationale,
                reason => reason.Contains(
                    "tests validation stage is not configured",
                    StringComparison.Ordinal));
            Assert.Empty(result.Diagnostics);
            Assert.Equal(AcceptanceGateStatus.Passed, result.Gate.Status);
        }
        finally
        {
            Directory.Delete(baseline.RepositoryPath, recursive: true);
        }
    }

    /// <summary>The default semantic validation stage uses Roslyn diagnostics without build targets.</summary>
    [Fact]
    public async Task ValidationPipeline_DefaultSemanticStage_UsesSemanticDiagnosticsWithoutBuild()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var request = new BuildValidationRequest
        {
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            Baseline = baseline,
            Confidence = SemanticConfidenceLevel.FullSemantic,
            Stages = [MutationValidationStage.Semantic],
            Projects =
            [
                new AffectedProject(
                    "Example",
                    Path.Combine(baseline.RepositoryPath, "Example.csproj"),
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    true),
            ],
        };
        var semanticDiagnostic = CreateDiagnostic("semantic", "CS1002", "; expected");
        var resolver = new FixedSemanticResolver([semanticDiagnostic]);
        var pipeline = new ValidationPipeline(
            executor,
            new DiagnosticClassifier(),
            new DiagnosticCorrelator(),
            new AcceptanceGate(),
            CreateTestPipeline(events),
            events,
            resolver);
        var capture = new BaselineCapture(
            baseline.WorkspaceId,
            baseline.CapturedAt,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            []);

        var mutationSet = CreateMutationSet(new MutationId(Guid.NewGuid()), "symbol") with
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
        };

        var result = await pipeline.ValidateAsync(
            request,
            capture,
            mutationSet,
            requiredApprovalsPresent: true,
            finalDiffAvailable: true);

        Assert.True(result.Build.Succeeded);
        Assert.Empty(result.Build.Targets);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CS1002", diagnostic.Code);
        Assert.Equal(DiagnosticClassification.Introduced, diagnostic.Classification);
        Assert.Contains(
            Assert.Single(resolver.ChangedFileRequests),
            path => path.Equals("Services/ExampleService.cs", StringComparison.OrdinalIgnoreCase));
        var started = Assert.IsType<SemanticCheckStarted>(
            Assert.Single(observed, domainEvent => domainEvent is SemanticCheckStarted));
        var completed = Assert.IsType<SemanticCheckCompleted>(
            Assert.Single(observed, domainEvent => domainEvent is SemanticCheckCompleted));
        Assert.Equal(started.SemanticCheckId, completed.SemanticCheckId);
        Assert.Equal(SemanticCheckPhase.PostMutation, completed.Phase);
        Assert.Equal("semantic post-mutation diagnostics", completed.CheckName);
        Assert.Equal(SemanticCheckOutcome.Failed, completed.Outcome);
        Assert.Contains("1 diagnostics", completed.Detail, StringComparison.Ordinal);
        Assert.Contains("1 blocking", completed.Detail, StringComparison.Ordinal);
    }

    /// <summary>Semantic completion publication failures are not treated as semantic diagnostic-service failures.</summary>
    [Fact]
    public async Task ValidationPipeline_SemanticCompletionPublicationFailure_DoesNotPublishSecondCompletion()
    {
        await using var events = new FailingSemanticCompletionEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var request = new BuildValidationRequest
        {
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            Baseline = baseline,
            Confidence = SemanticConfidenceLevel.FullSemantic,
            Stages = [MutationValidationStage.Semantic],
            Projects =
            [
                new AffectedProject(
                    "Example",
                    Path.Combine(baseline.RepositoryPath, "Example.csproj"),
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    true),
            ],
        };
        var pipeline = new ValidationPipeline(
            executor,
            new DiagnosticClassifier(),
            new DiagnosticCorrelator(),
            new AcceptanceGate(),
            CreateTestPipeline(events),
            events,
            new FixedSemanticResolver([]));
        var capture = new BaselineCapture(
            baseline.WorkspaceId,
            baseline.CapturedAt,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            []);
        var mutationSet = CreateMutationSet(new MutationId(Guid.NewGuid()), "symbol") with
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ValidateAsync(
                request,
                capture,
                mutationSet,
                requiredApprovalsPresent: true,
                finalDiffAvailable: true));

        Assert.Contains("completion subscriber failed", exception.Message, StringComparison.Ordinal);
        _ = Assert.Single(events.Published.OfType<SemanticCheckStarted>());
        _ = Assert.Single(events.Published.OfType<SemanticCheckCompleted>());
    }

    /// <summary>Semantic-check activity keeps unchanged baseline errors non-blocking.</summary>
    [Fact]
    public async Task ValidationPipeline_SemanticCheckOutcome_LeavesBaselineDiagnosticsCompleted()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var request = new BuildValidationRequest
        {
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            Baseline = baseline,
            Confidence = SemanticConfidenceLevel.FullSemantic,
            Stages = [MutationValidationStage.Semantic],
            Projects =
            [
                new AffectedProject(
                    "Example",
                    Path.Combine(baseline.RepositoryPath, "Example.csproj"),
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    true),
            ],
        };
        var semanticDiagnostic = CreateDiagnostic("semantic-baseline", "CS1002", "; expected");
        var resolver = new FixedSemanticResolver([semanticDiagnostic]);
        var pipeline = new ValidationPipeline(
            executor,
            new DiagnosticClassifier(),
            new DiagnosticCorrelator(),
            new AcceptanceGate(),
            CreateTestPipeline(events),
            events,
            resolver);
        var capture = new BaselineCapture(
            baseline.WorkspaceId,
            baseline.CapturedAt,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            [semanticDiagnostic]);

        var mutationSet = CreateMutationSet(new MutationId(Guid.NewGuid()), "symbol") with
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
        };

        var result = await pipeline.ValidateAsync(
            request,
            capture,
            mutationSet,
            requiredApprovalsPresent: true,
            finalDiffAvailable: true);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticClassification.Baseline, diagnostic.Classification);
        Assert.Equal(AcceptanceGateStatus.Passed, result.Gate.Status);
        var completed = Assert.IsType<SemanticCheckCompleted>(
            Assert.Single(observed, domainEvent => domainEvent is SemanticCheckCompleted));
        Assert.Equal(SemanticCheckOutcome.Completed, completed.Outcome);
        Assert.Contains("1 diagnostics", completed.Detail, StringComparison.Ordinal);
        Assert.Contains("0 blocking", completed.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(observed, domainEvent => domainEvent is DiagnosticObserved);
    }

    /// <summary>Semantic-only validation ignores no-file project diagnostics such as executable Main errors.</summary>
    [Fact]
    public async Task ValidationPipeline_DefaultSemanticStage_IgnoresNoFileProjectDiagnostics()
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            observed.Add(domainEvent);
            return Task.CompletedTask;
        });
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var request = new BuildValidationRequest
        {
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            Baseline = baseline,
            Confidence = SemanticConfidenceLevel.FullSemantic,
            Stages = [MutationValidationStage.Semantic],
            Projects =
            [
                new AffectedProject(
                    "Example",
                    Path.Combine(baseline.RepositoryPath, "Example.csproj"),
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    true),
            ],
        };
        var resolver = new FixedSemanticResolver(
        [
            CreateDiagnostic("semantic-project", "CS5001", "Program does not contain a static 'Main' method") with
            {
                File = null,
            },
        ]);
        var pipeline = new ValidationPipeline(
            executor,
            new DiagnosticClassifier(),
            new DiagnosticCorrelator(),
            new AcceptanceGate(),
            CreateTestPipeline(events),
            events,
            resolver);
        var capture = new BaselineCapture(
            baseline.WorkspaceId,
            baseline.CapturedAt,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            []);
        var mutationSet = CreateMutationSet(new MutationId(Guid.NewGuid()), "symbol") with
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
        };

        var result = await pipeline.ValidateAsync(
            request,
            capture,
            mutationSet,
            requiredApprovalsPresent: true,
            finalDiffAvailable: true);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(AcceptanceGateStatus.Passed, result.Gate.Status);
        Assert.DoesNotContain(observed, domainEvent => domainEvent is DiagnosticObserved);
    }

    /// <summary>Required semantic validation fails closed when no semantic service is available.</summary>
    [Fact]
    public async Task ValidationPipeline_RequiredSemanticStageUnavailable_FailsGate()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var request = new BuildValidationRequest
        {
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            Baseline = baseline,
            Confidence = SemanticConfidenceLevel.FullSemantic,
            Stages = [MutationValidationStage.Semantic],
            Projects =
            [
                new AffectedProject(
                    "Example",
                    Path.Combine(baseline.RepositoryPath, "Example.csproj"),
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    true),
            ],
        };
        var pipeline = new ValidationPipeline(
            executor,
            new DiagnosticClassifier(),
            new DiagnosticCorrelator(),
            new AcceptanceGate(),
            CreateTestPipeline(events),
            events);
        var capture = new BaselineCapture(
            baseline.WorkspaceId,
            baseline.CapturedAt,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            []);
        var mutationSet = CreateMutationSet(new MutationId(Guid.NewGuid()), "symbol") with
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
        };

        var result = await pipeline.ValidateAsync(
            request,
            capture,
            mutationSet,
            requiredApprovalsPresent: true,
            finalDiffAvailable: true);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("SEMANTIC_VALIDATION_UNAVAILABLE", diagnostic.Code);
        Assert.Equal(AcceptanceGateStatus.Failed, result.Gate.Status);
        Assert.Contains(
            result.Gate.Reasons,
            reason => reason.Contains("did not complete", StringComparison.Ordinal));
    }

    /// <summary>Required semantic validation failure prevents later trusted test execution.</summary>
    [Fact]
    public async Task ValidationPipeline_RequiredSemanticUnavailableWithTests_SkipsTests()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-semantic-tests-{Guid.NewGuid():N}");
        var sourceProject = Path.Combine(root, "src", "Example", "Example.csproj");
        var testProject = Path.Combine(root, "tests", "Example.Tests", "Example.Tests.csproj");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(sourceProject) ?? root);
            Directory.CreateDirectory(Path.GetDirectoryName(testProject) ?? root);
            await File.WriteAllTextAsync(sourceProject, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            await File.WriteAllTextAsync(
                testProject,
                $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <PackageReference Include="xunit.v3" />
                    <ProjectReference Include="{{Path.GetRelativePath(Path.GetDirectoryName(testProject) ?? root, sourceProject)}}" />
                  </ItemGroup>
                </Project>
                """);
            WorkspaceBaseline baseline = new(
                new WorkspaceId(Guid.NewGuid()),
                root,
                DateTimeOffset.UtcNow,
                [],
                SelectedSolutionPath: sourceProject,
                TrustLevel: RepositoryTrustLevel.TrustedBuild);
            var request = new BuildValidationRequest
            {
                SessionId = new SessionId(Guid.NewGuid()),
                RunId = new RunId(Guid.NewGuid()),
                Baseline = baseline,
                Confidence = SemanticConfidenceLevel.FullSemantic,
                Stages =
                [
                    MutationValidationStage.Semantic,
                    MutationValidationStage.Tests,
                ],
                Projects =
                [
                    new AffectedProject(
                        "Example",
                        sourceProject,
                        ["net10.0"],
                        SemanticConfidenceLevel.FullSemantic,
                        true),
                ],
                ProjectInventory =
                [
                    new SemanticProjectInfo(
                        "Example",
                        sourceProject,
                        ["net10.0"],
                        SemanticConfidenceLevel.FullSemantic,
                        [],
                        []),
                    new SemanticProjectInfo(
                        "Example.Tests",
                        testProject,
                        ["net10.0"],
                        SemanticConfidenceLevel.FullSemantic,
                        [Path.GetRelativePath(Path.GetDirectoryName(testProject) ?? root, sourceProject)],
                        ["xunit.v3"]),
                ],
            };
            var pipeline = new ValidationPipeline(
                executor,
                new DiagnosticClassifier(),
                new DiagnosticCorrelator(),
                new AcceptanceGate(),
                CreateTestPipeline(events),
                events);
            var capture = new BaselineCapture(
                baseline.WorkspaceId,
                baseline.CapturedAt,
                DateTimeOffset.UtcNow,
                SemanticConfidenceLevel.FullSemantic,
                []);
            var mutationSet = CreateMutationSet(new MutationId(Guid.NewGuid()), "symbol") with
            {
                SessionId = request.SessionId,
                RunId = request.RunId,
                WorkspaceId = baseline.WorkspaceId,
                BaselineCapturedAt = baseline.CapturedAt,
            };

            var result = await pipeline.ValidateAsync(
                request,
                capture,
                mutationSet,
                requiredApprovalsPresent: true,
                finalDiffAvailable: true);

            Assert.False(result.Tests.Completed);
            Assert.Contains(
                result.Tests.Selection.Rationale,
                reason => reason.Contains("semantic validation did not complete", StringComparison.Ordinal));
            Assert.Equal(AcceptanceGateStatus.Failed, result.Gate.Status);
            Assert.Contains(
                result.Gate.Reasons,
                reason => reason.Contains("did not complete", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    /// <summary>The semantic stage keeps affected-project errors from unchanged dependent files.</summary>
    [Fact]
    public async Task ValidationPipeline_DefaultSemanticStage_KeepsUnchangedDependentErrors()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var directProject = Path.Combine(baseline.RepositoryPath, "Example.csproj");
        var dependentProject = Path.Combine(baseline.RepositoryPath, "Example.App.csproj");
        var request = new BuildValidationRequest
        {
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            Baseline = baseline,
            Confidence = SemanticConfidenceLevel.FullSemantic,
            Stages = [MutationValidationStage.Semantic],
            Projects =
            [
                new AffectedProject(
                    "Example",
                    directProject,
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    true),
                new AffectedProject(
                    "Example.App",
                    dependentProject,
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    false),
            ],
        };
        var dependentDiagnostic = CreateDiagnostic(
            "dependent",
            "CS1061",
            "ExampleService does not contain a definition for RemovedMember") with
        {
            Project = "Example.App",
            File = "Callers/ExampleCaller.cs",
        };
        var resolver = new FixedSemanticResolver([dependentDiagnostic]);
        var pipeline = new ValidationPipeline(
            executor,
            new DiagnosticClassifier(),
            new DiagnosticCorrelator(),
            new AcceptanceGate(),
            CreateTestPipeline(events),
            events,
            resolver);
        var capture = new BaselineCapture(
            baseline.WorkspaceId,
            baseline.CapturedAt,
            DateTimeOffset.UtcNow,
            SemanticConfidenceLevel.FullSemantic,
            []);
        var mutationSet = CreateMutationSet(new MutationId(Guid.NewGuid()), "symbol") with
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
        };

        var result = await pipeline.ValidateAsync(
            request,
            capture,
            mutationSet,
            requiredApprovalsPresent: true,
            finalDiffAvailable: true);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CS1061", diagnostic.Code);
        Assert.Equal(DiagnosticClassification.Introduced, diagnostic.Classification);
        var requestedProjects = Assert.Single(resolver.ProjectPathRequests);
        Assert.Contains(directProject, requestedProjects);
        Assert.Contains(dependentProject, requestedProjects);
        Assert.Equal(AcceptanceGateStatus.Failed, result.Gate.Status);
    }

    /// <summary>Semantic-only baseline capture records pre-existing affected-project errors as baseline diagnostics.</summary>
    [Fact]
    public async Task ValidationPipeline_SemanticBaselineCapture_ClassifiesPreExistingTouchedFileErrors()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var request = new BuildValidationRequest
        {
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            Baseline = baseline,
            Confidence = SemanticConfidenceLevel.FullSemantic,
            Projects =
            [
                new AffectedProject(
                    "Example",
                    Path.Combine(baseline.RepositoryPath, "Example.csproj"),
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    true),
            ],
        };
        var resolver = new FixedSemanticResolver(
        [
            CreateDiagnostic("baseline", "CS0103", "The name 'Missing' does not exist in the current context"),
        ]);
        var pipeline = new ValidationPipeline(
            executor,
            new DiagnosticClassifier(),
            new DiagnosticCorrelator(),
            new AcceptanceGate(),
            CreateTestPipeline(events),
            events,
            resolver);
        var mutationSet = CreateMutationSet(new MutationId(Guid.NewGuid()), "symbol") with
        {
            SessionId = request.SessionId,
            RunId = request.RunId,
            WorkspaceId = baseline.WorkspaceId,
            BaselineCapturedAt = baseline.CapturedAt,
        };

        var capture = await pipeline.CaptureSemanticBaselineAsync(
            request,
            mutationSet);

        var diagnostic = Assert.Single(capture.Diagnostics);
        Assert.True(diagnostic.IsBaselineDiagnostic);
        Assert.Equal(DiagnosticClassification.Baseline, diagnostic.Classification);
        Assert.Contains(
            Assert.Single(resolver.ChangedFileRequests),
            path => path.Equals("Services/ExampleService.cs", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A failed build without normalized diagnostics cannot pass the acceptance gate.</summary>
    [Fact]
    public async Task ValidationPipeline_UnnormalizedBuildFailure_RejectsRequiredStages()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance);
        var baseline = await CreateBuildableBaselineAsync(includeDelay: false);
        try
        {
            var request = new BuildValidationRequest
            {
                SessionId = new SessionId(Guid.NewGuid()),
                RunId = new RunId(Guid.NewGuid()),
                Baseline = baseline,
                ProjectInventory =
                [
                    new SemanticProjectInfo(
                        "Stale.Tests",
                        Path.Combine(baseline.RepositoryPath, "missing", "Stale.Tests.csproj"),
                        ["net10.0"],
                        SemanticConfidenceLevel.FullSemantic,
                        [],
                        ["xunit.v3.mtp-v2"]),
                ],
                Confidence = SemanticConfidenceLevel.FullSemantic,
                Stages =
                [
                    MutationValidationStage.Compile,
                    MutationValidationStage.Diagnostics,
                ],
            };
            var capture = await new BaselineBuildCapture(executor).CaptureAsync(request);
            var projectPath = baseline.SelectedSolutionPath
                ?? throw new InvalidOperationException("The build fixture must select its project.");
            var project = await File.ReadAllTextAsync(projectPath);
            await File.WriteAllTextAsync(
                projectPath,
                project.Replace(
                    "</Project>",
                    "  <Target Name=\"FailWithoutDiagnostic\" BeforeTargets=\"BeforeBuild\"><Error Text=\"Build failed without a source diagnostic.\" /></Target>\n</Project>",
                    StringComparison.Ordinal));
            var mutationSet = new MutationSet
            {
                MutationSetId = new MutationSetId(Guid.NewGuid()),
                SessionId = request.SessionId,
                RunId = request.RunId,
                WorkspaceId = baseline.WorkspaceId,
                BaselineCapturedAt = baseline.CapturedAt,
                Rationale = "Validate a build failure without a compiler diagnostic.",
                Mutations = [],
            };
            var pipeline = new ValidationPipeline(
                executor,
                new DiagnosticClassifier(),
                new DiagnosticCorrelator(),
                new AcceptanceGate(),
                CreateTestPipeline(events),
                events);

            var result = await pipeline.ValidateAsync(
                request,
                capture,
                mutationSet,
                requiredApprovalsPresent: true,
                finalDiffAvailable: true);

            Assert.False(result.Build.Succeeded);
            Assert.Empty(result.Diagnostics);
            Assert.Equal(AcceptanceGateStatus.Failed, result.Gate.Status);
            Assert.Contains("Required validation stages did not complete.", result.Gate.Reasons);
            Assert.Contains(
                result.Tests.Selection.Rationale,
                reason => reason.Contains("skipped because the affected build failed", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(baseline.RepositoryPath, recursive: true);
        }
    }

    /// <summary>Cancelling a non-cooperative build abandons its result and terminates its process tree.</summary>
    [Fact]
    public async Task BuildExecutor_CancelledBuild_ThrowsCancellation()
    {
        await using var events = new DomainEventStream();
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance,
            TimeSpan.FromSeconds(5));
        var baseline = await CreateBuildableBaselineAsync(includeDelay: true);
        try
        {
            using var cancellation = new CancellationTokenSource();
            var stopwatch = Stopwatch.StartNew();
            var childPidPath = Path.Combine(baseline.RepositoryPath, "child.pid");
            var childPid = 0;
            var cancellationCoordinator = Task.Run(async () =>
            {
                for (var attempt = 0; attempt < 200 && !File.Exists(childPidPath); attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }

                Assert.True(File.Exists(childPidPath), "The delayed child process did not publish its process id.");
                var childPidText = await File.ReadAllTextAsync(childPidPath);
                Assert.True(int.TryParse(
                    childPidText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out childPid));
                await cancellation.CancelAsync();
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecuteAsync(
                new BuildValidationRequest
                {
                    SessionId = new SessionId(Guid.NewGuid()),
                    RunId = new RunId(Guid.NewGuid()),
                    Baseline = baseline,
                    Confidence = SemanticConfidenceLevel.FullSemantic,
                },
                cancellation.Token));
            await cancellationCoordinator;

            var childExited = false;
            for (var attempt = 0; attempt < 200 && !childExited; attempt++)
            {
                try
                {
                    using var child = Process.GetProcessById(childPid);
                    childExited = child.HasExited;
                }
                catch (ArgumentException)
                {
                    childExited = true;
                }

                if (!childExited)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(25));
                }
            }

            Assert.True(childExited, $"Build child process {childPid} survived cancellation.");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        }
        finally
        {
            Directory.Delete(baseline.RepositoryPath, recursive: true);
        }
    }

    /// <summary>Structured diagnostic events flow into the host projection and diagnostics TUI view.</summary>
    [Fact]
    public async Task DiagnosticObserved_StructuredPayload_RendersInTui()
    {
        var sessionId = new SessionId(Guid.NewGuid());
        var projections = new InMemoryProjectionStore();
        await projections.ApplyAsync(new SessionCreated(sessionId, DateTimeOffset.UtcNow, "validation"));
        var diagnostic = CreateDiagnostic("introduced", "CS1503", "Cannot convert argument") with
        {
            Classification = DiagnosticClassification.Introduced,
        };
        await projections.ApplyAsync(new DiagnosticObserved(
            sessionId,
            DateTimeOffset.UtcNow,
            diagnostic.Code,
            diagnostic.Message,
            diagnostic));
        var presenter = new TuiPresenter(new RejectingDispatcher(), projections);

        var snapshot = await presenter.RenderAsync(sessionId);

        Assert.Contains("Diagnostics (1)", snapshot.Workspace, StringComparison.Ordinal);
        Assert.Contains("CS1503", snapshot.Workspace, StringComparison.Ordinal);
        Assert.Contains("Introduced", snapshot.Workspace, StringComparison.Ordinal);
    }

    /// <summary>Project discovery recognizes the repository's xUnit Microsoft.Testing.Platform project.</summary>
    [Fact]
    public void TestDiscoverer_MicrosoftTestingPlatformProject_NormalizesFrameworkAndReferences()
    {
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var projectPath = Path.Combine(
            baseline.RepositoryPath,
            "tests",
            "Threadsmith.Validation.Tests",
            "Threadsmith.Validation.Tests.csproj");
        var project = new SemanticProjectInfo(
            "Threadsmith.Validation.Tests",
            projectPath,
            ["net10.0"],
            SemanticConfidenceLevel.FullSemantic,
            [],
            ["xunit.v3.mtp-v2", "Microsoft.Testing.Platform.MSBuild"]);

        var result = TestDiscoverer.DiscoverProjects(
            baseline.RepositoryPath,
            [project]);

        var discovered = Assert.Single(result);
        Assert.Equal(TestFramework.MicrosoftTestingPlatform, discovered.Framework);
        Assert.Contains(
            discovered.ProjectReferences,
            reference => reference.EndsWith("Threadsmith.Core.csproj", StringComparison.Ordinal));
    }

    /// <summary>An IsTestProject-only project without a supported runner is excluded.</summary>
    [Fact]
    public async Task TestDiscoverer_UnsupportedTestProject_SkipsProject()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-unsupported-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var projectPath = Path.Combine(root, "Unsupported.Tests.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup></Project>");
        try
        {
            var project = new SemanticProjectInfo(
                "Unsupported.Tests",
                projectPath,
                ["net10.0"],
                SemanticConfidenceLevel.FullSemantic,
                [],
                []);

            var result = TestDiscoverer.DiscoverProjects(root, [project]);

            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Test discovery rejects project paths that traverse a junction or symbolic link.</summary>
    [Fact]
    public async Task TestDiscoverer_LinkedProject_RejectsTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-linked-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await using (var link = await TemporaryDirectoryLink.CreateAsync(root))
        {
            var projectPath = Path.Combine(link.TargetPath, "Linked.Tests.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit.v3.mtp-v2\" /></ItemGroup></Project>");
            var project = new SemanticProjectInfo(
                "Linked.Tests",
                Path.Combine(link.LinkPath, "Linked.Tests.csproj"),
                ["net10.0"],
                SemanticConfidenceLevel.FullSemantic,
                [],
                ["xunit.v3.mtp-v2"]);

            var exception = Assert.Throws<InvalidOperationException>(
                () => TestDiscoverer.DiscoverProjects(root, [project]));

            Assert.Contains("symbolic link or junction", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        Directory.Delete(root, recursive: true);
    }

    /// <summary>Selection includes a referencing test project and explains project and symbol drivers.</summary>
    [Fact]
    public void TestSelector_AffectedProject_SelectsReferencingTestsWithRationale()
    {
        var root = CreateBaseline(RepositoryTrustLevel.TrustedBuild).RepositoryPath;
        var core = Path.Combine(root, "src", "Threadsmith.Core", "Threadsmith.Core.csproj");
        var testProject = new TestProject
        {
            Name = "Threadsmith.Validation.Tests",
            FilePath = Path.Combine(root, "tests", "Threadsmith.Validation.Tests", "Threadsmith.Validation.Tests.csproj"),
            Framework = TestFramework.MicrosoftTestingPlatform,
            ProjectReferences = [core],
        };
        var affected = new AffectedProject(
            "Threadsmith.Core",
            core,
            ["net10.0"],
            SemanticConfidenceLevel.FullSemantic,
            IsDirectlyChanged: true);
        var mutationId = new MutationId(Guid.NewGuid());

        var result = TestSelector.Select(
            [testProject],
            [affected],
            CreateMutationSet(mutationId, "Threadsmith.Core:Example"));

        Assert.Single(result.Projects);
        Assert.Equal(mutationId, Assert.Single(result.RelatedMutationIds));
        Assert.Contains(result.Rationale, reason => reason.Contains("references affected", StringComparison.Ordinal));
        Assert.Contains(result.Rationale, reason => reason.Contains("Threadsmith.Core:Example", StringComparison.Ordinal));
    }

    /// <summary>Both Microsoft.Testing.Platform and VSTest summaries normalize to host-owned counts.</summary>
    [Theory]
    [InlineData("Test run summary: Passed!\n  total: 4\n  failed: 0\n  succeeded: 3\n  skipped: 1", 3, 0, 1)]
    [InlineData("Passed! - Failed: 1, Passed: 2, Skipped: 1, Total: 4", 2, 1, 1)]
    public void TestResultNormalizer_FrameworkSummaries_NormalizesCounts(
        string output,
        int passed,
        int failed,
        int skipped)
    {
        var project = new TestProject
        {
            Name = "Tests",
            FilePath = "Tests.csproj",
            Framework = TestFramework.XUnit,
        };
        var process = new ProcessExecutionResult(
            42,
            failed == 0 ? 0 : 1,
            output,
            string.Empty,
            StandardOutputTruncated: false,
            StandardErrorTruncated: false,
            TimedOut: false,
            TimeSpan.FromSeconds(1));

        var result = TestResultNormalizer.Normalize(project, process, []);

        Assert.Equal(passed, result.Passed);
        Assert.Equal(failed, result.Failed);
        Assert.Equal(skipped, result.Skipped);
        Assert.Equal(failed > 0 ? TestOutcome.Failed : TestOutcome.Skipped, result.Outcome);
    }

    /// <summary>The test pipeline discovers, explains, runs, normalizes, and publishes selected evidence.</summary>
    [Fact]
    public async Task TestValidationPipeline_AffectedProject_RunsSelectedProjectAndPublishesEvidence()
    {
        await using var events = new DomainEventStream();
        var publishedEvents = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((domainEvent, _) =>
        {
            publishedEvents.Add(domainEvent);
            return Task.CompletedTask;
        });
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var core = Path.Combine(baseline.RepositoryPath, "src", "Threadsmith.Core", "Threadsmith.Core.csproj");
        var tests = Path.Combine(
            baseline.RepositoryPath,
            "tests",
            "Threadsmith.Validation.Tests",
            "Threadsmith.Validation.Tests.csproj");
        var processes = new ScriptedProcessManager(
            new ProcessExecutionResult(
                1,
                0,
                "xUnit.net v3 Microsoft.Testing.Platform v2 Runner v3.2.2 (64-bit .NET 10.0)\n\n  Example.Tests.Case\n\nTest discovery summary: found 1 test(s) - Tests.dll\n  duration: 10ms",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(10)),
            new ProcessExecutionResult(
                2,
                0,
                "Test run summary: Passed!\n  total: 1\n  failed: 0\n  succeeded: 1\n  skipped: 0",
                string.Empty,
                false,
                false,
                false,
                TimeSpan.FromMilliseconds(20)));
        var pipeline = new TestValidationPipeline(
            new TestDiscoverer(processes),
            new TestRunner(processes, events),
            events);
        var request = new BuildValidationRequest
        {
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            Baseline = baseline,
            Projects =
            [
                new AffectedProject(
                    "Threadsmith.Core",
                    core,
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    IsDirectlyChanged: true),
            ],
            ProjectInventory =
            [
                new SemanticProjectInfo(
                    "Threadsmith.Validation.Tests",
                    tests,
                    ["net10.0"],
                    SemanticConfidenceLevel.FullSemantic,
                    [core],
                    ["xunit.v3.mtp-v2", "Microsoft.Testing.Platform.MSBuild"]),
            ],
            Confidence = SemanticConfidenceLevel.FullSemantic,
        };

        var result = await pipeline.ValidateAsync(
            request,
            CreateMutationSet(new MutationId(Guid.NewGuid()), "Threadsmith.Core:Example"),
            TimeSpan.FromSeconds(5));

        Assert.True(result.Completed);
        Assert.Equal(1, result.Passed);
        Assert.Equal("Example.Tests.Case", Assert.Single(result.Selection.TestCases).FullyQualifiedName);
        Assert.Equal(2, processes.Requests.Count);
        Assert.Equal(["run", "--project"], processes.Requests[0].Arguments.Take(2));
        Assert.Equal(["test", "--project"], processes.Requests[1].Arguments.Take(2));
        Assert.Contains("--configuration", processes.Requests[1].Arguments);
        Assert.DoesNotContain(
            processes.Requests[1].Arguments,
            argument => argument.StartsWith("-property:", StringComparison.Ordinal));
        var published = Assert.Single(publishedEvents, item => item is TestRunCompleted);
        var completed = Assert.IsType<TestRunCompleted>(published);
        Assert.Equal(2, completed.SchemaVersion);
    }

    /// <summary>Selected test failures block final acceptance.</summary>
    [Fact]
    public void AcceptanceGate_SelectedTestFailure_Fails()
    {
        var project = new TestProject
        {
            Name = "Tests",
            FilePath = "Tests.csproj",
            Framework = TestFramework.XUnit,
        };
        var tests = new TestValidationResult
        {
            Selection = new TestSelection { Projects = [project] },
            Results =
            [
                new Threadsmith.Core.TestResult
                {
                    Project = project,
                    Outcome = TestOutcome.Failed,
                    ProcessCompleted = true,
                    Failed = 1,
                    Output = "failed",
                },
            ],
            Completed = true,
        };

        var result = AcceptanceGate.Evaluate(new AcceptanceGateRequest
        {
            Diagnostics = [],
            Tests = tests,
            RequiredStagesCompleted = true,
            FinalDiffAvailable = true,
            RequiredApprovalsPresent = true,
        });

        Assert.Equal(AcceptanceGateStatus.Failed, result.Status);
        Assert.Contains(result.Reasons, reason => reason.Contains("selected test", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Test execution rejects read-only trust before any repository process starts.</summary>
    [Fact]
    public async Task TestRunner_TrustedRead_RejectsBeforeExecution()
    {
        await using var events = new DomainEventStream();
        var processes = new UnusedProcessManager();
        var runner = new TestRunner(processes, events);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedRead);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => runner.RunAsync(
            new SessionId(Guid.NewGuid()),
            new RunId(Guid.NewGuid()),
            baseline,
            new TestSelection(),
            TimeSpan.FromSeconds(5)));
    }

    /// <summary>Cancellation is propagated through the test runner to the tracked process manager.</summary>
    [Fact]
    public async Task TestRunner_CancelledRun_PropagatesCancellation()
    {
        await using var events = new DomainEventStream();
        var processes = new BlockingProcessManager();
        var runner = new TestRunner(processes, events);
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var projectPath = Path.Combine(
            baseline.RepositoryPath,
            "tests",
            "Threadsmith.Validation.Tests",
            "Threadsmith.Validation.Tests.csproj");
        var selection = new TestSelection
        {
            Projects =
            [
                new TestProject
                {
                    Name = "Threadsmith.Validation.Tests",
                    FilePath = projectPath,
                    Framework = TestFramework.MicrosoftTestingPlatform,
                },
            ],
        };
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new SessionId(Guid.NewGuid()),
            new RunId(Guid.NewGuid()),
            baseline,
            selection,
            TimeSpan.FromMinutes(1),
            cancellation.Token));

        Assert.True(processes.WasInvoked);
    }

    /// <summary>Structured test evidence and selection rationale render through the TUI projection.</summary>
    [Fact]
    public async Task TestRunCompleted_StructuredPayload_RendersInTui()
    {
        var projections = new InMemoryProjectionStore();
        var sessionId = new SessionId(Guid.NewGuid());
        await projections.ApplyAsync(new SessionCreated(sessionId, DateTimeOffset.UtcNow, "tests"));
        var project = new TestProject
        {
            Name = "Example.Tests",
            FilePath = "Example.Tests.csproj",
            Framework = TestFramework.XUnit,
        };
        var validation = new TestValidationResult
        {
            Selection = new TestSelection
            {
                Projects = [project],
                Rationale = ["Selected Example.Tests because it references affected project Example.Core."],
            },
            Results =
            [
                new Threadsmith.Core.TestResult
                {
                    Project = project,
                    Outcome = TestOutcome.Passed,
                    ProcessCompleted = true,
                    Passed = 2,
                    Output = "passed",
                    Duration = TimeSpan.FromMilliseconds(50),
                },
            ],
            Completed = true,
        };
        await projections.ApplyAsync(new TestRunCompleted(
            sessionId,
            DateTimeOffset.UtcNow,
            Passed: 2,
            Failed: 0,
            Skipped: 0,
            StructuredResult: validation));
        var presenter = new TuiPresenter(new RejectingDispatcher(), projections);

        var snapshot = await presenter.RenderAsync(sessionId);

        Assert.Contains("Tests (2 passed, 0 failed, 0 skipped)", snapshot.Workspace, StringComparison.Ordinal);
        Assert.Contains("references affected", snapshot.Workspace, StringComparison.Ordinal);
    }

    private static TestValidationResult CreateTestValidation(TestOutcome outcome)
    {
        var project = new TestProject
        {
            Name = "Tests",
            FilePath = "Tests.csproj",
            Framework = TestFramework.XUnit,
        };
        var failed = outcome == TestOutcome.Failed;
        return new TestValidationResult
        {
            Selection = new TestSelection { Projects = [project] },
            Results =
            [
                new Threadsmith.Core.TestResult
                {
                    Project = project,
                    Outcome = outcome,
                    ProcessCompleted = true,
                    Passed = failed ? 0 : 1,
                    Failed = failed ? 1 : 0,
                    Output = failed ? "failed" : "passed",
                },
            ],
            Completed = !failed,
        };
    }

    private sealed class FailingSemanticCompletionEventStream : IDomainEventStream
    {
        public List<IDomainEvent> Published { get; } = [];

        public IDomainEventSubscription Subscribe(
            Func<IDomainEvent, CancellationToken, Task> handler,
            int capacity = 256)
        {
            throw new NotSupportedException("The test stream does not support subscriptions.");
        }

        public Task PublishAsync(
            IDomainEvent domainEvent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Published.Add(domainEvent);
            if (domainEvent is SemanticCheckCompleted)
            {
                throw new InvalidOperationException("semantic completion subscriber failed");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedSemanticResolver : ISemanticEngineResolver
    {
        private readonly IReadOnlyList<Diagnostic> _diagnostics;

        public FixedSemanticResolver(IReadOnlyList<Diagnostic> diagnostics)
        {
            _diagnostics = diagnostics;
        }

        public SemanticConfidenceLevel GetConfidence(WorkspaceId workspaceId)
        {
            return SemanticConfidenceLevel.FullSemantic;
        }

        public Task<IReadOnlyList<SymbolResult>> FindSymbolsAsync(
            WorkspaceId workspaceId,
            string query,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<SymbolResult>>([]);
        }

        public Task<IReadOnlyList<ReferenceResult>> FindReferencesAsync(
            WorkspaceId workspaceId,
            string symbolId,
            bool allowTextFallback = false,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ReferenceResult>>([]);
        }

        public Task<IReadOnlyList<ImplementationResult>> FindImplementationsAsync(
            WorkspaceId workspaceId,
            string symbolId,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<ImplementationResult>>([]);
        }

        public List<IReadOnlyList<string>> ChangedFileRequests { get; } = [];

        public List<IReadOnlyList<string>> ProjectPathRequests { get; } = [];

        public Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
            WorkspaceId workspaceId,
            IReadOnlyList<string> projectPaths,
            IReadOnlyList<string> changedFiles,
            CancellationToken cancellationToken = default)
        {
            ProjectPathRequests.Add(projectPaths.ToArray());
            ChangedFileRequests.Add(changedFiles.ToArray());
            return Task.FromResult(_diagnostics);
        }
    }

    private static TestValidationPipeline CreateTestPipeline(IDomainEventStream events)
    {
        var processes = new UnusedProcessManager();
        return new TestValidationPipeline(
            new TestDiscoverer(processes),
            new TestRunner(processes, events),
            events);
    }

    private static Diagnostic CreateDiagnostic(string id, string code, string message)
    {
        return new()
        {
            Id = id,
            Code = code,
            Severity = DiagnosticSeverity.Error,
            Project = "Example.Core",
            TargetFramework = "net10.0",
            File = "Services/ExampleService.cs",
            Range = new SourceRange(47, 23, 47, 29),
            Message = message,
            Confidence = SemanticConfidenceLevel.FullSemantic,
        };
    }

    private static MutationSet CreateMutationSet(MutationId mutationId, string relatedSymbolId)
    {
        return new()
        {
            MutationSetId = new MutationSetId(Guid.NewGuid()),
            SessionId = new SessionId(Guid.NewGuid()),
            RunId = new RunId(Guid.NewGuid()),
            WorkspaceId = new WorkspaceId(Guid.NewGuid()),
            BaselineCapturedAt = DateTimeOffset.UtcNow,
            Rationale = "Test correlation.",
            Mutations =
        [
            new Mutation
            {
                MutationId = mutationId,
                Type = MutationType.ReplaceText,
                RelativePath = "Services/ExampleService.cs",
                BaselineSha256 = new string('0', 64),
                ReplacementText = "replacement",
                RelatedSymbolId = relatedSymbolId,
            },
        ],
        };
    }

    private static WorkspaceBaseline CreateBaseline(RepositoryTrustLevel trustLevel)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return new WorkspaceBaseline(
            new WorkspaceId(Guid.NewGuid()),
            root,
            DateTimeOffset.UtcNow,
            [],
            SelectedSolutionPath: Path.Combine(root, "src", "Threadsmith.Validation", "Threadsmith.Validation.csproj"),
            TrustLevel: trustLevel);
    }

    private static async Task<WorkspaceBaseline> CreateBuildableBaselineAsync(bool includeDelay)
    {
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var delayProperties = includeDelay
            ? """
                  <PropertyGroup>
                    <DelayCommand Condition="'$(OS)' == 'Windows_NT'">powershell -NoProfile -Command "$PID | Set-Content -NoNewline '&quot;$(MSBuildProjectDirectory)\child.pid&quot;'; Start-Sleep -Seconds 30"</DelayCommand>
                    <DelayCommand Condition="'$(OS)' != 'Windows_NT'">sh -c 'echo $$ > "$(MSBuildProjectDirectory)/child.pid"; sleep 30'</DelayCommand>
                  </PropertyGroup>
                  <Target Name="DelayBuild" BeforeTargets="BeforeBuild">
                    <Exec Command="$(DelayCommand)" />
                  </Target>
              """
            : string.Empty;
        var projectPath = Path.Combine(root, "ValidationTarget.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            {delayProperties}
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), "System.Console.WriteLine(\"ok\");");
        using var restore = Process.Start(new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "restore", projectPath, "--nologo" },
        }) ?? throw new InvalidOperationException("Could not start fixture restore.");
        var output = await restore.StandardOutput.ReadToEndAsync();
        var error = await restore.StandardError.ReadToEndAsync();
        await restore.WaitForExitAsync();
        if (restore.ExitCode != 0)
        {
            throw new InvalidOperationException($"Fixture restore failed: {output}{Environment.NewLine}{error}");
        }

        return new WorkspaceBaseline(
            new WorkspaceId(Guid.NewGuid()),
            root,
            DateTimeOffset.UtcNow,
            [],
            SelectedSolutionPath: projectPath,
            TrustLevel: RepositoryTrustLevel.TrustedBuild);
    }

    private sealed class TemporaryDirectoryLink : IAsyncDisposable
    {
        private TemporaryDirectoryLink(string linkPath, string targetPath)
        {
            LinkPath = linkPath;
            TargetPath = targetPath;
        }

        public string LinkPath { get; }

        public string TargetPath { get; }

        public static async Task<TemporaryDirectoryLink> CreateAsync(
            string repositoryPath,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
            var linkPath = Path.Combine(repositoryPath, $"linked-{Guid.NewGuid():N}");
            var targetPath = Path.Combine(Path.GetTempPath(), $"threadsmith-m6-external-{Guid.NewGuid():N}");
            Directory.CreateDirectory(targetPath);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.System),
                                "cmd.exe"),
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        },
                    };
                    process.StartInfo.ArgumentList.Add("/d");
                    process.StartInfo.ArgumentList.Add("/c");
                    process.StartInfo.ArgumentList.Add("mklink");
                    process.StartInfo.ArgumentList.Add("/J");
                    process.StartInfo.ArgumentList.Add(linkPath);
                    process.StartInfo.ArgumentList.Add(targetPath);
                    if (!process.Start())
                    {
                        throw new InvalidOperationException("Unable to start the Windows junction command.");
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
                    var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
                    await process.WaitForExitAsync(cancellationToken);
                    var output = await outputTask;
                    var error = await errorTask;
                    if (process.ExitCode != 0)
                    {
                        throw new IOException(
                            $"Unable to create the Windows test junction. {error}{output}".Trim());
                    }
                }
                else
                {
                    Directory.CreateSymbolicLink(linkPath, targetPath);
                }

                return new TemporaryDirectoryLink(linkPath, targetPath);
            }
            catch
            {
                if (Directory.Exists(linkPath))
                {
                    Directory.Delete(linkPath);
                }

                if (Directory.Exists(targetPath))
                {
                    Directory.Delete(targetPath, recursive: true);
                }

                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(LinkPath))
            {
                Directory.Delete(LinkPath);
            }

            if (Directory.Exists(TargetPath))
            {
                Directory.Delete(TargetPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public bool WasInvoked { get; private set; }

        public async Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled process should not complete.");
        }
    }

    private sealed class ScriptedProcessManager : IProcessManager
    {
        private readonly Queue<ProcessExecutionResult> _results;

        public ScriptedProcessManager(params ProcessExecutionResult[] results)
        {
            _results = new Queue<ProcessExecutionResult>(results);
        }

        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class UnusedProcessManager : IProcessManager
    {
        public IReadOnlyList<ActiveProcessInfo> ActiveProcesses => [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<ProcessExecutionResult>(
                new InvalidOperationException("No process execution expected."));
        }
    }

    private sealed class RejectingDispatcher : ICommandDispatcher
    {
        public Task<TResponse> DispatchAsync<TResponse>(
            ICommand<TResponse> command,
            CancellationToken cancellationToken = default)
        {
            return Task.FromException<TResponse>(new InvalidOperationException("No command expected."));
        }
    }
}
