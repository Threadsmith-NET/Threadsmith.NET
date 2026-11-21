namespace Threadsmith.Validation.Tests;

using System.Diagnostics;
using System.Globalization;
using System.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Interaction.Coordination;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
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

    /// <summary>Final validation without a pre-edit build reports unknown origins and cannot accept remaining compiler errors.</summary>
    [Fact]
    public void DiagnosticClassifier_NoBaseline_ReportsUnknownOriginAndFailsRemainingErrors()
    {
        var diagnostic = CreateDiagnostic("current", "CS0246", "Type could not be found");
        var classified = DiagnosticClassifier.Classify(null, [diagnostic], SemanticConfidenceLevel.FullSemantic);
        Assert.Equal(DiagnosticClassification.OriginUnknown, Assert.Single(classified).Classification);
        Assert.False(classified[0].IsBaselineDiagnostic);
        var gate = AcceptanceGate.Evaluate(new AcceptanceGateRequest
        {
            Diagnostics = classified,
            RequiredStagesCompleted = true,
            FinalDiffAvailable = true,
            RequiredApprovalsPresent = true,
        });
        Assert.Equal(AcceptanceGateStatus.Failed, gate.Status);
        Assert.Contains(gate.Reasons, reason => reason.Contains("origins were not established", StringComparison.Ordinal));
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

    /// <summary>Retained and restored baselines cannot authorize build or test execution after a policy transition.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validation_StaleBaseline_RejectsBeforeExecution(bool restored)
    {
        await using var events = new DomainEventStream();
        var binding = new SessionPolicyBinding();
        var baseline = CreateBaseline(RepositoryTrustLevel.TrustedBuild);
        var policy = new EffectiveSessionPolicy
        {
            BindingId = Guid.NewGuid(),
            Generation = 1,
            RepositoryPath = baseline.RepositoryPath,
            Fingerprint = "captured",
        };
        binding.Publish(policy);
        baseline = baseline with { SecurityPolicy = restored ? null : binding.Current };
        binding.Publish(policy with { Generation = 2, MaximumTrust = RepositoryTrustLevel.TrustedRead });
        var executor = new BuildExecutor(
            events,
            new DiagnosticNormalizer(),
            NullLogger<BuildExecutor>.Instance,
            policyBinding: binding);
        var request = new BuildValidationRequest
        {
            SessionId = SessionId.New(),
            RunId = RunId.New(),
            Baseline = baseline,
            Confidence = SemanticConfidenceLevel.FullSemantic,
        };
        var ct = TestContext.Current.CancellationToken;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => executor.ExecuteAsync(request, ct));
        var discoverer = new TestDiscoverer(new UnusedProcessManager(), policyBinding: binding);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => discoverer.DiscoverCasesAsync(
            baseline,
            request.RunId,
            [],
            TimeSpan.FromSeconds(30),
            ct));
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

    /// <summary>The semantic stage keeps affected-project errors from unchanged dependent files.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidationPipeline_DefaultSemanticStage_KeepsUnchangedDependentErrors(bool withoutBaseline)
    {
        await using var events = new DomainEventStream();
        var observed = new List<IDomainEvent>();
        await using var subscription = events.Subscribe((item, _) =>
        {
            observed.Add(item);
            return Task.CompletedTask;
        });
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
            withoutBaseline ? null : capture,
            mutationSet,
            requiredApprovalsPresent: true,
            finalDiffAvailable: true);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("CS1061", diagnostic.Code);
        Assert.Equal(withoutBaseline ? DiagnosticClassification.OriginUnknown : DiagnosticClassification.Introduced, diagnostic.Classification);
        Assert.Contains(observed.OfType<DiagnosticObserved>(), item => item.Code == "CS1061");
        var completed = Assert.Single(observed.OfType<SemanticCheckCompleted>());
        Assert.Equal(SemanticCheckOutcome.Failed, completed.Outcome);
        Assert.Contains("1 blocking", completed.Detail, StringComparison.Ordinal);
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

    /// <summary>A failed restore preserves diagnostics and removes the partially constructed fixture.</summary>
    [Fact]
    public async Task BuildFixture_FailedRestore_RemovesOwnedRoot()
    {
        var processes = new ScriptedProcessManager(new ProcessExecutionResult(1, 7, "restore-output", "restore-error", false, false, false, TimeSpan.Zero));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateBuildableBaselineAsync(false, processes));

        Assert.Contains("restore-output", error.Message, StringComparison.Ordinal);
        Assert.Contains("restore-error", error.Message, StringComparison.Ordinal);
        var root = Assert.Single(processes.Requests).WorkingDirectory;
        try
        {
            Assert.False(Directory.Exists(root), $"Partial fixture survived failed restore: {root}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                await DeleteBuildFixtureAsync(root);
            }
        }
    }

    /// <summary>Cancellation during restore remains cancellation after the partial fixture is removed.</summary>
    [Fact]
    public async Task BuildFixture_CancelledRestore_RemovesOwnedRoot()
    {
        var processes = new BlockingProcessManager();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var setup = CreateBuildableBaselineAsync(false, processes, cancellation.Token);
        Exception? completionFailure = null;
        try
        {
            await Task.WhenAny(processes.Entered.Task, setup).WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(processes.Entered.Task.IsCompletedSuccessfully, "Fixture setup ended before restore started.");
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await setup.WaitAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                completionFailure = exception;
            }
        }

        Assert.IsAssignableFrom<OperationCanceledException>(completionFailure);

        Assert.NotNull(processes.Request);
        var root = processes.Request.WorkingDirectory;
        try
        {
            Assert.False(Directory.Exists(root), $"Partial fixture survived cancelled restore: {root}");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                await DeleteBuildFixtureAsync(root);
            }
        }
    }

    /// <summary>Test discovery rejects project paths that traverse a junction or symbolic link.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestDiscoverer_LinkedProject_RejectsTraversal(bool linkedAuthorityRoot)
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
                () => TestDiscoverer.DiscoverProjects(linkedAuthorityRoot ? link.LinkPath : root, [project]));

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
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = runner.RunAsync(
            new SessionId(Guid.NewGuid()),
            new RunId(Guid.NewGuid()),
            baseline,
            selection,
            TimeSpan.FromMinutes(1),
            cancellation.Token);
        Exception? completionFailure = null;
        try
        {
            await Task.WhenAny(processes.Entered.Task, run).WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(processes.Entered.Task.IsCompletedSuccessfully, "Test run ended before process invocation.");
        }
        finally
        {
            await cancellation.CancelAsync();
            try
            {
                await run.WaitAsync(TestContext.Current.CancellationToken);
            }
            catch (Exception exception)
            {
                completionFailure = exception;
            }
        }

        Assert.IsAssignableFrom<OperationCanceledException>(completionFailure);

        Assert.True(processes.WasInvoked);
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

        public async Task PublishCommittedBatchAsync(
            IReadOnlyList<IDomainEvent> domainEvents,
            Func<bool> tryCommit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!tryCommit())
            {
                return;
            }

            foreach (var domainEvent in domainEvents)
            {
                await PublishAsync(domainEvent, CancellationToken.None);
            }
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

    private static async Task DeleteBuildFixtureAsync(string path)
    {
        var root = Path.GetFullPath(path);
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (Path.GetDirectoryName(root) != expectedParent
            || !Path.GetFileName(root).StartsWith("threadsmith-build-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to delete an unowned build fixture.");
        }

        // Windows can report the terminated process as exited before its directory
        // handle is released. Retry only fixture cleanup; lifecycle assertions run first.
        while (true)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
    }

    private static async Task<WorkspaceBaseline> CreateBuildableBaselineAsync(
        bool includeDelay,
        IProcessManager? processManager = null,
        CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken, cancellationToken);
        var ct = cancellation.Token;
        var root = Path.Combine(Path.GetTempPath(), $"threadsmith-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var delayCommand = !includeDelay ? string.Empty
                : OperatingSystem.IsWindows() ? CreateWindowsDelayCommand()
                : """sh -c 'set -eu; echo $$ > "$(MSBuildProjectDirectory)/child.pid.tmp"; mv "$(MSBuildProjectDirectory)/child.pid.tmp" "$(MSBuildProjectDirectory)/child.pid"; mkfifo "$(MSBuildProjectDirectory)/child.gate"; read gate < "$(MSBuildProjectDirectory)/child.gate"'""";
            var delayProperties = includeDelay
                ? """
                      <PropertyGroup>
                        <DelayCommand>{{DelayCommand}}</DelayCommand>
                      </PropertyGroup>
                      <Target Name="DelayBuild" BeforeTargets="BeforeBuild">
                        <Exec Command="$(DelayCommand)" />
                      </Target>
                  """
                    .Replace("{{DelayCommand}}", SecurityElement.Escape(delayCommand), StringComparison.Ordinal)
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
                """,
                ct);
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), "System.Console.WriteLine(\"ok\");", ct);
            processManager ??= new ProcessManager(new SecretOutputSanitizer(), NullLogger<ProcessManager>.Instance, timeProvider: new FakeTimeProvider(DateTimeOffset.UtcNow));
            var restore = await processManager.RunAsync(
                new ProcessExecutionRequest
                {
                    ToolInvocationId = ToolInvocationId.New(),
                    RunId = RunId.New(),
                    FileName = "dotnet",
                    Arguments = ["restore", projectPath, "--nologo"],
                    WorkingDirectory = root,
                    Origin = ProcessRequestOrigin.Host,
                    Timeout = TimeSpan.FromMinutes(5),
                },
                ct);
            if (restore.TimedOut)
            {
                throw new TimeoutException($"Fixture restore timed out: {restore.StandardOutput}{Environment.NewLine}{restore.StandardError}");
            }

            if (restore.ExitCode != 0)
            {
                throw new InvalidOperationException($"Fixture restore failed: {restore.StandardOutput}{Environment.NewLine}{restore.StandardError}");
            }

            return new WorkspaceBaseline(
                new WorkspaceId(Guid.NewGuid()),
                root,
                DateTimeOffset.UtcNow,
                [],
                SelectedSolutionPath: projectPath,
                TrustLevel: RepositoryTrustLevel.TrustedBuild);
        }
        catch (Exception setupFailure)
        {
            try
            {
                await DeleteBuildFixtureAsync(root);
            }
            catch (Exception cleanupFailure)
            {
                setupFailure.Data["FixtureCleanupFailure"] = cleanupFailure;
            }

            throw;
        }
    }

    private static string CreateWindowsDelayCommand()
    {
        // Bind a known executable instead of depending on optional installations or PATH activation aliases.
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(shell))
        {
            throw new InvalidOperationException("Windows PowerShell is required for the delayed build fixture.");
        }

        return $"\"{shell}\" -NoProfile -NonInteractive -Command \"[System.IO.File]::WriteAllText('$(MSBuildProjectDirectory)\\child.pid.tmp', [string]$PID); [System.IO.File]::Move('$(MSBuildProjectDirectory)\\child.pid.tmp', '$(MSBuildProjectDirectory)\\child.pid'); [System.Threading.Thread]::Sleep(-1)\"";
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

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ProcessExecutionRequest? Request { get; private set; }

        public async Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            Request = request;
            Entered.TrySetResult();
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
}
