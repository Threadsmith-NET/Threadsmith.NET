namespace Threadsmith.Architecture.Tests;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Threadsmith.App;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Execution;
using Threadsmith.Models;
using Threadsmith.Telemetry;
using Threadsmith.Tools;
using Threadsmith.Workspaces;
using Xunit;

/// <summary>Opt-in real-provider coverage for incremental approved-plan mutation execution.</summary>
public sealed class IncrementalMutationLiveTests
{
    private static readonly JsonSerializerOptions ReportJson = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Exercises focused batches, step boundaries, validation, and exact-diff auto-approval with a real model.</summary>
    [Fact]
    [Trait("Category", "LiveIntegration")]
    public async Task ApprovedPlan_Terra_ProposesAndAppliesIncrementalBatches()
    {
        await RunLiveScenarioAsync(incrementalPlanning: false);
    }

    /// <summary>Exercises real planning, mutations, receipts and objective completion on one run.</summary>
    [Fact]
    [Trait("Category", "LiveIntegration")]
    public async Task Objective_Terra_PlansAndAppliesMultipleTranches()
    {
        await RunLiveScenarioAsync(incrementalPlanning: true);
    }

    private static async Task RunLiveScenarioAsync(bool incrementalPlanning)
    {
        var environmentPrefix = incrementalPlanning ? "THREADSMITH_LIVE_INCREMENTAL_PLAN" : "THREADSMITH_LIVE_MUTATION";
        if (Environment.GetEnvironmentVariable(environmentPrefix + "_TESTS") != "1")
        {
            Assert.Skip($"Set {environmentPrefix}_TESTS=1 and {environmentPrefix}_PROFILE to run this live test.");
        }

        var configuredProfile = Environment.GetEnvironmentVariable(environmentPrefix + "_PROFILE");
        Assert.False(string.IsNullOrWhiteSpace(configuredProfile), $"{environmentPrefix}_PROFILE is required.");
        var profileId = new ModelProfileId(Guid.Parse(configuredProfile));
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var cancellationToken = runCancellation.Token;
        Task<bool>? sessionCompletion = null;
        var root = Path.Combine(Path.GetTempPath(), "threadsmith-mutation-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        try
        {
            var expectedFiles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["src/alpha.txt"] = "status=modern-alpha\n",
                ["src/beta.txt"] = "status=modern-beta\n",
                ["src/gamma.txt"] = "status=modern-gamma\n",
                ["src/delta.txt"] = "status=modern-delta\n",
            };
            foreach (var path in expectedFiles.Keys)
            {
                var name = Path.GetFileNameWithoutExtension(path);
                await File.WriteAllTextAsync(
                    Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)),
                    $"status=legacy-{name}\n",
                    cancellationToken);
            }

            var paths = ConfigurationBootstrap.ResolvePaths(root);
            var configuration = ConfigurationBootstrap.BuildTrusted(paths);
            using var ownedConfiguration = configuration as IDisposable;
            var userDirectory = Path.GetDirectoryName(paths.UserConfiguration)
                ?? throw new InvalidOperationException("The user configuration has no directory.");
            var secrets = new SecretResolver(
                [new EnvironmentSecretProvider(), new UserFileSecretProvider(Path.Combine(userDirectory, "secrets", "config.json"))]);
            using var models = await ModelComposition.CreateAsync(
                configuration,
                paths,
                secrets,
                NullLoggerFactory.Instance,
                trustedConfiguration: configuration);
            var profile = models.TrustedCatalog.Get(profileId);
            Assert.Contains("terra", profile.ModelId, StringComparison.OrdinalIgnoreCase);

            var timeoutSeconds = ReadPositiveDouble(environmentPrefix + "_TIMEOUT_SECONDS", 600);
            var targetMutations = ReadPositiveInt("THREADSMITH_LIVE_MUTATION_TARGET_MUTATIONS", 1);
            var targetFiles = ReadPositiveInt("THREADSMITH_LIVE_MUTATION_TARGET_FILES", 1);
            var targetCharacters = ReadPositiveLong("THREADSMITH_LIVE_MUTATION_TARGET_CHARACTERS", 4_000);
            var maximumCalls = ReadPositiveInt("THREADSMITH_LIVE_MUTATION_MAX_CALLS", 16);
            var maximumTokens = ReadPositiveLong("THREADSMITH_LIVE_MUTATION_TOKEN_BUDGET", 250_000);
            var limits = ExecutionLimits.Default with
            {
                MutationBatching = new MutationBatchingOptions
                {
                    TargetMutations = targetMutations,
                    TargetFiles = targetFiles,
                    TargetMutationCharacters = targetCharacters,
                },
                IncrementalPlanning = new IncrementalPlanningOptions
                {
                    Enabled = incrementalPlanning,
                    TargetSteps = ReadPositiveInt(environmentPrefix + "_TARGET_STEPS", 1),
                    TargetFiles = targetFiles,
                    MaximumPlansPerObjective = ReadPositiveInt(environmentPrefix + "_MAX_PLANS", 6),
                },
            };
            var budgetLimit = new BudgetDimensions(
                maximumTokens,
                maximumCalls,
                TimeSpan.FromSeconds(timeoutSeconds),
                25m);

            await using var events = new DomainEventStream();
            await using var workspaces = new TransactionalWorkspaceCoordinator(events);
            var sanitizer = new SecretOutputSanitizer();
            var evidence = new EvidenceStore(events, sanitizer);
            var baseline = await CreateBaselineAsync(root, expectedFiles.Keys, cancellationToken);
            await workspaces.RegisterBaselineAsync(baseline, cancellationToken: cancellationToken);
            var prompts = TestPromptLoader.Instance;
            var innerContext = new ContextAssembler(
                evidence,
                new TokenEstimator(),
                new ContextPolicy(),
                new PromptAppendLoader(sanitizer),
                sanitizer,
                events,
                prompts,
                new ContextAssemblerOptions(),
                new ModelResolver(models.TrustedCatalog, new InMemoryModelPreferenceSnapshotProvider()),
                providerInstructionResolver: new ModelProviderInstructionResolver(models.TrustedCatalog, prompts),
                requestPreparationResolver: models.TrustedProvider as IModelRequestPreparationResolver);
            var context = new RefreshingContextAssembler(innerContext, evidence, expectedFiles.Keys.ToArray());
            var recorder = new RecordingProvider(models.TrustedProvider);
            var corrections = new ConcurrentQueue<CorrectionObservation>();
            var pendingApprovals = Channel.CreateUnbounded<RunId>();
            var plans = new ConcurrentDictionary<int, ImplementationPlan>();
            await using var correctionSubscription = events.Subscribe((domainEvent, _) =>
            {
                if (domainEvent is ExecutionCheckpointWritten { Phase: ExecutionCheckpointPhase.MutationApprovalPending } pending)
                {
                    pendingApprovals.Writer.TryWrite(pending.RunId);
                }

                if (domainEvent is ModelCorrectionAttempted correction)
                {
                    corrections.Enqueue(new CorrectionObservation(
                        correction.Category,
                        correction.AttemptNumber,
                        correction.MaximumAttempts,
                        correction.SafeReason));
                }

                return Task.CompletedTask;
            });
            var proposals = new MutationProposalApplication(
                recorder,
                context,
                workspaces,
                new ExecutionBudget(budgetLimit),
                sanitizer,
                events,
                profileId,
                limits,
                budgetFactory: () => new ExecutionBudget(budgetLimit),
                correctiveMessages: new CorrectiveMessageFactory(prompts),
                prompts: prompts);
            var checkpoints = new MemoryCheckpointStore();
            var artifacts = new MemoryArtifactPublisher();
            var orchestrator = new ExecutionOrchestrator(
                proposals,
                workspaces,
                new PassingBaselineHandler(),
                new PassingValidationHandler(),
                workspaces,
                checkpoints,
                artifacts,
                events,
                sanitizer,
                NullLogger<ExecutionOrchestrator>.Instance,
                new CorrectiveMessageFactory(prompts),
                limits);
            var request = CreateStartRequest(baseline);
            var batches = new List<BatchObservation>();
            var stopwatch = Stopwatch.StartNew();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            Task executionFinished;
            if (incrementalPlanning)
            {
                var template = request;
                var registry = new ToolRegistry([]);
                var pipeline = new ToolInvocationPipeline(
                    registry,
                    new DefaultPolicyEngine(),
                    new DenyApprovalPolicy(),
                    events,
                    sanitizer,
                    NullLogger<ToolInvocationPipeline>.Instance);
                var approvalConfiguration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["planning:approvalPolicy"] = "AutoApproveAllValid" }).Build();
                using var ownedApprovalConfiguration = approvalConfiguration as IDisposable;
                var application = new SessionApplication(
                    events,
                    recorder,
                    new ExecutionBudget(budgetLimit),
                    sanitizer,
                    NullLogger<SessionApplication>.Instance,
                    pipeline,
                    (_, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult(new ToolInvocationContext
                        {
                            RepositoryPath = root,
                            WorkspaceId = baseline.WorkspaceId,
                            TrustLevel = RepositoryTrustLevel.TrustedMutation,
                            ApprovedRoots = ["src"],
                            RequestedBy = "live incremental planning test",
                        });
                    },
                    contextAssembler: context,
                    evidenceStore: evidence,
                    toolRegistry: registry,
                    defaultModelProfileId: profileId,
                    limits: limits,
                    executionOrchestrator: orchestrator,
                    executionRequestFactory: (session, run, task, plan, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        plans[plan.Revision] = plan;
                        return Task.FromResult<ExecutionStartRequest?>(template with
                        {
                            SessionId = session,
                            RunId = run,
                            Task = task,
                            ApprovedPlan = plan,
                            AllowPlanContinuation = true,
                            ValidationRequest = template.ValidationRequest with { SessionId = session, RunId = run },
                        });
                    },
                    budgetFactory: () => new ExecutionBudget(budgetLimit),
                    planSanityChecker: new PlanSanityChecker(prompts, limits),
                    planApprovalPolicy: new PlanApprovalPolicyService(approvalConfiguration),
                    planSanityRequestFactory: (_, plan, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult<PlanSanityCheckRequest?>(new PlanSanityCheckRequest
                        {
                            Plan = plan,
                            RepositoryRoot = root,
                            Baseline = baseline,
                            TrustLevel = RepositoryTrustLevel.TrustedMutation,
                        });
                    },
                    correctiveMessages: new CorrectiveMessageFactory(prompts),
                    prompts: prompts);
                var dispatcher = new CommandDispatcher([application]);
                var sessionId = await dispatcher.DispatchAsync(new CreateSessionCommand("live incremental objective"), timeout.Token);
                var runId = await dispatcher.DispatchAsync(
                    new SubmitRequestCommand(
                        sessionId,
                        "Modernize src/alpha.txt, src/beta.txt, src/gamma.txt and src/delta.txt so each contains "
                        + "status=modern-<name> followed by one newline. Complete these independent changes serially "
                        + "using multiple small plans, validating each before planning the next. Finish only after all four are updated."),
                    timeout.Token);
                request = request with { SessionId = sessionId, RunId = runId };
                sessionCompletion = dispatcher.DispatchAsync(new WaitForRunCommand(runId), timeout.Token);
                executionFinished = sessionCompletion;
            }
            else
            {
                plans[request.ApprovedPlan.Revision] = request.ApprovedPlan;
                await orchestrator.StartAsync(request, timeout.Token);
                executionFinished = orchestrator.WaitForOutcomeAsync(request.RunId, timeout.Token);
            }

            double firstPreviewSeconds = 0;
            while (!executionFinished.IsCompleted)
            {
                var pending = pendingApprovals.Reader.ReadAsync(timeout.Token).AsTask();
                if (await Task.WhenAny(pending, executionFinished) == executionFinished)
                {
                    break;
                }

                Assert.Equal(request.RunId, await pending);
                var checkpoint = await checkpoints.GetCheckpointAsync(request.RunId, timeout.Token)
                    ?? throw new InvalidOperationException("The mutation checkpoint was not persisted.");
                if (batches.Count == 0)
                {
                    firstPreviewSeconds = stopwatch.Elapsed.TotalSeconds;
                }

                var staged = await orchestrator.HandleAsync(
                    new GetExecutionMutationCommand(request.SessionId, request.RunId),
                    timeout.Token);
                Assert.NotNull(staged);
                var activeStep = plans[checkpoint.PlanRevision].Steps[checkpoint.CurrentPlanStepOrdinal!.Value - 1];
                var allowedPaths = activeStep.GetAffectedPaths().ToHashSet(StringComparer.OrdinalIgnoreCase);
                var mutationPaths = staged.MutationSet.Mutations
                    .SelectMany(mutation => string.IsNullOrWhiteSpace(mutation.DestinationRelativePath)
                        ? [mutation.RelativePath]
                        : new[] { mutation.RelativePath, mutation.DestinationRelativePath! })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                Assert.All(mutationPaths, path => Assert.Contains(path, allowedPaths));
                batches.Add(new BatchObservation(
                    checkpoint.BatchOrdinal,
                    checkpoint.CurrentPlanStepOrdinal.Value,
                    activeStep.Title,
                    staged.MutationSet.Mutations.Count,
                    mutationPaths,
                    staged.StepComplete,
                    staged.Preview.UnifiedDiff.Length,
                    stopwatch.Elapsed.TotalSeconds,
                    recorder.RequestCount));
                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"Approving batch {checkpoint.BatchOrdinal} for step {checkpoint.CurrentPlanStepOrdinal}: "
                    + $"{staged.MutationSet.Mutations.Count} mutations across {mutationPaths.Length} files; "
                    + $"stepComplete={staged.StepComplete}.");
                var progress = await orchestrator.ContinueAsync(
                    new ContinueExecutionRequest
                    {
                        SessionId = request.SessionId,
                        RunId = request.RunId,
                        Approval = new MutationApproval
                        {
                            Level = MutationApprovalLevel.EntireSet,
                            ApprovalId = staged.ApprovalId,
                        },
                        ApprovalProvenance = "opt-in live test auto-approval",
                    },
                    timeout.Token);
                Assert.NotEqual(ExecutionCheckpointPhase.Failed, progress.Status);
            }

            await executionFinished;
            if (sessionCompletion is not null)
            {
                Assert.True(await sessionCompletion);
                Assert.InRange(plans.Count, 2, limits.IncrementalPlanning.MaximumPlansPerObjective);
                Assert.Contains(recorder.Requests, observation => observation.ReceivedExecutionReceipt);
                Assert.Contains(recorder.Requests, observation => observation.ConfirmedObjective);
            }

            var outcome = await checkpoints.GetOutcomeAsync(request.RunId, timeout.Token);
            Assert.NotNull(outcome);
            Assert.Equal(ExecutionCheckpointPhase.Completed, outcome.Status);
            Assert.Equal(plans.Values.Sum(plan => plan.Steps.Count), outcome.CompletedStepIds.Count);
            foreach (var expected in expectedFiles)
            {
                var actual = await File.ReadAllTextAsync(
                    Path.Combine(root, expected.Key.Replace('/', Path.DirectorySeparatorChar)),
                    timeout.Token);
                Assert.Equal(expected.Value, actual);
            }

            Assert.All(recorder.Requests, item => Assert.Equal(profileId, item.ResolvedProfileId));
            if (!incrementalPlanning)
            {
                Assert.Contains(batches, batch => batch.PlanStepOrdinal == 2);
            }

            var finalDiff = outcome.FinalDiff is null
                ? null
                : await artifacts.ReadAsync(outcome.FinalDiff, timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(finalDiff));
            var report = new LiveMutationReport(
                profile.Name,
                profile.ModelId,
                profile.Id.Value,
                profile.DefaultReasoningLevel,
                timeoutSeconds,
                targetMutations,
                targetFiles,
                targetCharacters,
                firstPreviewSeconds,
                stopwatch.Elapsed.TotalSeconds,
                recorder.Requests,
                batches,
                corrections.ToArray(),
                outcome.Status,
                outcome.CompletedStepIds.Count,
                outcome.ChangedFiles,
                finalDiff?.Length ?? 0,
                plans.Count);
            var reportDirectory = Path.GetFullPath(
                Environment.GetEnvironmentVariable("THREADSMITH_LIVE_MUTATION_REPORT_DIRECTORY")
                ?? Path.Combine(Path.GetTempPath(), "threadsmith-mutation-live-reports", DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss")));
            Directory.CreateDirectory(reportDirectory);
            var reportPath = Path.Combine(reportDirectory, "incremental-mutation-terra.json");
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, ReportJson), timeout.Token);
            TestContext.Current.TestOutputHelper?.WriteLine($"Live report: {reportPath}");
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Completed in {stopwatch.Elapsed.TotalSeconds:F1}s; first preview in {firstPreviewSeconds:F1}s; "
                + $"{batches.Count} batches, {recorder.RequestCount} model requests, {corrections.Count} corrections.");
        }
        finally
        {
            await runCancellation.CancelAsync();
            if (sessionCompletion is not null)
            {
                await ((Task)sessionCompletion).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            var normalized = Path.GetFullPath(root);
            var parent = Path.GetFullPath(Path.GetTempPath());
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(normalized)) != Path.TrimEndingDirectorySeparator(parent)
                || !Path.GetFileName(normalized).StartsWith("threadsmith-mutation-live-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to clean an unowned live fixture directory.");
            }

            Directory.Delete(normalized, recursive: true);
        }
    }

    private static ExecutionStartRequest CreateStartRequest(WorkspaceBaseline baseline)
    {
        var firstStep = new ImplementationPlanStep
        {
            StepId = StepId.New(),
            Title = "Modernize the first status group",
            Description = "Change alpha, beta, and gamma independently. The exact final lines are status=modern-alpha, status=modern-beta, and status=modern-gamma. Prefer one focused file per proposal and report stepComplete only after all three files have those values.",
            FileIntents =
            [
                new PlanFileIntent { Kind = PlanFileChangeKind.Modify, Path = "src/alpha.txt" },
                new PlanFileIntent { Kind = PlanFileChangeKind.Modify, Path = "src/beta.txt" },
                new PlanFileIntent { Kind = PlanFileChangeKind.Modify, Path = "src/gamma.txt" },
            ],
            ExpectedOutcome = "Alpha, beta, and gamma contain their exact modern status values.",
            Validation = ["Read all three files and compare their complete text with the requested values."],
        };
        var secondStep = new ImplementationPlanStep
        {
            StepId = StepId.New(),
            Title = "Modernize the final status",
            Description = "Change delta.txt so its complete content is exactly status=modern-delta followed by one newline.",
            FileIntents = [new PlanFileIntent { Kind = PlanFileChangeKind.Modify, Path = "src/delta.txt" }],
            ExpectedOutcome = "Delta contains its exact modern status value.",
            Validation = ["Read delta.txt and compare its complete text with the requested value."],
        };
        var plan = new ImplementationPlan
        {
            Summary = "Modernize four independent status fixtures in two ordered steps.",
            Steps = [firstStep, secondStep],
        };
        var sessionId = SessionId.New();
        var runId = RunId.New();
        return new ExecutionStartRequest
        {
            SessionId = sessionId,
            RunId = runId,
            Baseline = baseline,
            Task = new TaskSpecification(
                "Apply the approved status modernization plan exactly.",
                [new AcceptanceCriterion("Every declared file contains its exact requested final line.")],
                ["Keep each proposal focused and do not edit files outside the active approved step."]),
            ApprovedPlan = plan,
            ValidationRequest = new BuildValidationRequest
            {
                SessionId = sessionId,
                RunId = runId,
                Baseline = baseline,
                Confidence = SemanticConfidenceLevel.FullSemantic,
                Stages = [MutationValidationStage.Semantic],
            },
        };
    }

    private static async Task<WorkspaceBaseline> CreateBaselineAsync(
        string root,
        IEnumerable<string> relativePaths,
        CancellationToken cancellationToken)
    {
        var files = new List<WorkspaceFileHash>();
        foreach (var relativePath in relativePaths.OrderBy(path => path, StringComparer.Ordinal))
        {
            var bytes = await File.ReadAllBytesAsync(
                Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                cancellationToken);
            files.Add(new WorkspaceFileHash(relativePath, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength));
        }

        return new WorkspaceBaseline(
            WorkspaceId.New(),
            root,
            DateTimeOffset.UtcNow,
            files,
            ApprovedRoots: ["src"],
            TrustLevel: RepositoryTrustLevel.TrustedMutation);
    }

    private static int ReadPositiveInt(string name, int fallback)
    {
        return int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    }

    private static long ReadPositiveLong(string name, long fallback)
    {
        return long.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    }

    private static double ReadPositiveDouble(string name, double fallback)
    {
        return double.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;
    }

    private sealed record BatchObservation(
        int BatchOrdinal,
        int PlanStepOrdinal,
        string PlanStepTitle,
        int MutationCount,
        IReadOnlyList<string> Paths,
        bool? StepComplete,
        int DiffCharacters,
        double ElapsedSeconds,
        int ModelRequestsSoFar);

    private sealed record CorrectionObservation(
        ModelCorrectionCategory Category,
        int AttemptNumber,
        int MaximumAttempts,
        string SafeReason);

    private sealed record ProviderRequestObservation(
        int Ordinal,
        double Seconds,
        ModelProfileId? ResolvedProfileId,
        long EstimatedInputTokens,
        long? InputTokens,
        long? OutputTokens,
        IReadOnlyList<ToolProposalObservation> ToolProposals,
        bool ReceivedExecutionReceipt,
        bool ConfirmedObjective);

    private sealed record ToolProposalObservation(
        int MutationCount,
        int DistinctPathCount,
        bool? StepComplete,
        int ArgumentCharacters);

    private sealed record LiveMutationReport(
        string ProfileName,
        string ModelId,
        Guid ProfileId,
        ReasoningLevel ReasoningLevel,
        double TimeoutSeconds,
        int TargetMutations,
        int TargetFiles,
        long TargetMutationCharacters,
        double FirstPreviewSeconds,
        double TotalSeconds,
        IReadOnlyList<ProviderRequestObservation> Requests,
        IReadOnlyList<BatchObservation> Batches,
        IReadOnlyList<CorrectionObservation> Corrections,
        ExecutionCheckpointPhase FinalStatus,
        int CompletedSteps,
        IReadOnlyList<string> ChangedFiles,
        int FinalDiffCharacters,
        int PlanCount);

    private sealed class RefreshingContextAssembler : IContextAssembler
    {
        private readonly Dictionary<string, EvidenceId> _evidenceIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly EvidenceStore _evidence;
        private readonly IContextAssembler _inner;
        private readonly IReadOnlyList<string> _planningPaths;

        public RefreshingContextAssembler(IContextAssembler inner, EvidenceStore evidence, IReadOnlyList<string> planningPaths)
        {
            _inner = inner;
            _evidence = evidence;
            _planningPaths = planningPaths;
        }

        public async Task<ContextAssemblyResult> AssembleAsync(
            ContextAssemblyRequest request,
            CancellationToken cancellationToken = default)
        {
            var paths = request.MutationExecutionScope?.ActiveStep.GetAffectedPaths()
                ?? request.ApprovedPlan?.Steps.SelectMany(step => step.GetAffectedPaths()).ToArray()
                ?? _planningPaths;
            var refreshed = new List<Evidence>();
            foreach (var relativePath in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var normalized = relativePath.Replace('\\', '/');
                var absolute = Path.Combine(request.RepositoryPath, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolute))
                {
                    continue;
                }

                var content = await File.ReadAllTextAsync(absolute, cancellationToken);
                if (!_evidenceIds.TryGetValue(normalized, out var evidenceId))
                {
                    evidenceId = EvidenceId.New();
                    _evidenceIds.Add(normalized, evidenceId);
                }

                refreshed.Add(new Evidence
                {
                    EvidenceId = evidenceId,
                    SessionId = request.SessionId,
                    RunId = request.RunId,
                    Kind = EvidenceKind.SourceExcerpt,
                    Content = content,
                    Provenance = new EvidenceProvenance
                    {
                        SourcePath = normalized,
                        Source = "live-test:workspace-refresh",
                        SemanticConfidence = SemanticConfidenceLevel.FullSemantic,
                    },
                    CollectedAt = DateTimeOffset.UtcNow,
                    Relevance = 1,
                    EstimatedTokens = TokenEstimator.Estimate(content),
                    InvalidationKeys = [normalized],
                });
            }

            if (refreshed.Count > 0)
            {
                await _evidence.AddBatchAsync(refreshed, cancellationToken);
            }

            return await _inner.AssembleAsync(request, cancellationToken);
        }

        public ContextInspectionProjection? GetInspection(RunId runId)
        {
            return _inner.GetInspection(runId);
        }

        public void InvalidateInspections()
        {
            _inner.InvalidateInspections();
        }
    }

    private sealed class RecordingProvider : IModelProvider
    {
        private readonly IModelProvider _inner;
        private readonly Lock _gate = new();
        private readonly List<ProviderRequestObservation> _requests = [];
        private int _requestCount;

        public RecordingProvider(IModelProvider inner)
        {
            _inner = inner;
        }

        public int RequestCount => Volatile.Read(ref _requestCount);

        public IReadOnlyList<ProviderRequestObservation> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.OrderBy(item => item.Ordinal).ToArray();
                }
            }
        }

        public async IAsyncEnumerable<ModelChunk> StreamAsync(
            ModelStreamRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var ordinal = Interlocked.Increment(ref _requestCount);
            var stopwatch = Stopwatch.StartNew();
            ModelUsage? usage = null;
            var toolProposals = new List<ToolProposalObservation>();
            var confirmedObjective = false;
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Mutation model request {ordinal}; round {request.ToolContinuationRound}; profile {request.ResolvedProfileId?.Value:D}.");
            try
            {
                await foreach (var chunk in _inner.StreamAsync(request, cancellationToken))
                {
                    usage = chunk.Usage ?? usage;
                    confirmedObjective |= chunk.Output is ToolRequestModelOutput { ToolName: "complete_objective" };
                    if (chunk.Output is ToolRequestModelOutput tool
                        && string.Equals(tool.ToolName, "propose_mutations", StringComparison.Ordinal))
                    {
                        toolProposals.Add(ParseToolProposal(tool.ArgumentsJson));
                    }

                    yield return chunk;
                }
            }
            finally
            {
                var observation = new ProviderRequestObservation(
                    ordinal,
                    stopwatch.Elapsed.TotalSeconds,
                    request.ResolvedProfileId,
                    request.WireEstimate?.WireInputTokens ?? 0,
                    usage?.InputTokens,
                    usage?.OutputTokens,
                    toolProposals,
                    request.Tools.Any(tool => tool.Name == "complete_objective")
                        && request.Messages.Any(message => message.GetModelVisibleContent().Contains("PlanContinuationPending", StringComparison.Ordinal)),
                    confirmedObjective);
                lock (_gate)
                {
                    _requests.Add(observation);
                }
            }
        }

        private static ToolProposalObservation ParseToolProposal(string argumentsJson)
        {
            try
            {
                using var document = JsonDocument.Parse(argumentsJson);
                var root = document.RootElement;
                var mutationSet = root.TryGetProperty("mutationSet", out var nested) ? nested : root;
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var mutationCount = 0;
                if (mutationSet.TryGetProperty("mutations", out var mutations)
                    && mutations.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mutation in mutations.EnumerateArray())
                    {
                        mutationCount++;
                        if (mutation.TryGetProperty("relativePath", out var path) && path.ValueKind == JsonValueKind.String)
                        {
                            paths.Add(path.GetString() ?? string.Empty);
                        }

                        if (mutation.TryGetProperty("destinationRelativePath", out var destination)
                            && destination.ValueKind == JsonValueKind.String)
                        {
                            paths.Add(destination.GetString() ?? string.Empty);
                        }
                    }
                }

                bool? stepComplete = mutationSet.TryGetProperty("stepComplete", out var completion)
                    && completion.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? completion.GetBoolean()
                        : null;
                return new ToolProposalObservation(mutationCount, paths.Count, stepComplete, argumentsJson.Length);
            }
            catch (JsonException)
            {
                return new ToolProposalObservation(0, 0, null, argumentsJson.Length);
            }
        }
    }

    private sealed class PassingBaselineHandler : ICommandHandler<CaptureBaselineBuildCommand, BaselineCapture>
    {
        public Task<BaselineCapture> HandleAsync(
            CaptureBaselineBuildCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new BaselineCapture(
                command.Request.Baseline.WorkspaceId,
                command.Request.Baseline.CapturedAt,
                DateTimeOffset.UtcNow,
                SemanticConfidenceLevel.FullSemantic,
                []));
        }
    }

    private sealed class PassingValidationHandler : ICommandHandler<ValidateMutationCommand, MutationValidationResult>
    {
        public Task<MutationValidationResult> HandleAsync(
            ValidateMutationCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new MutationValidationResult(
                new BuildValidationResult(true, [], [], TimeSpan.Zero),
                [],
                new TestValidationResult
                {
                    Selection = new TestSelection { Rationale = ["Synthetic file assertions run after orchestration."] },
                    Completed = true,
                },
                new AcceptanceGateResult(AcceptanceGateStatus.Passed, [])));
        }
    }

    private sealed class MemoryCheckpointStore : IExecutionCheckpointStore
    {
        private readonly ConcurrentDictionary<RunId, ExecutionContinuation> _checkpoints = new();
        private readonly ConcurrentDictionary<RunId, ExecutionOutcomeProjection> _outcomes = new();

        public Task<ExecutionContinuation?> GetCheckpointAsync(
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _checkpoints.TryGetValue(runId, out var checkpoint);
            return Task.FromResult(checkpoint);
        }

        public Task<ExecutionOutcomeProjection?> GetOutcomeAsync(
            RunId runId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _outcomes.TryGetValue(runId, out var outcome);
            return Task.FromResult(outcome);
        }

        public Task SaveCheckpointAsync(
            ExecutionContinuation checkpoint,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _checkpoints[checkpoint.RunId] = checkpoint;
            return Task.CompletedTask;
        }

        public Task SaveOutcomeAsync(
            ExecutionOutcomeProjection outcome,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _outcomes[outcome.RunId] = outcome;
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryArtifactPublisher : IExecutionArtifactPublisher
    {
        private readonly Dictionary<string, string> _content = new(StringComparer.Ordinal);

        public Task<ExecutionArtifactReference> PublishAsync(
            SessionId sessionId,
            string kind,
            string content,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            _content[hash] = content;
            return Task.FromResult(new ExecutionArtifactReference(hash, kind, Encoding.UTF8.GetByteCount(content)));
        }

        public Task<string?> ReadAsync(
            ExecutionArtifactReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _content.TryGetValue(reference.ContentHash, out var content);
            return Task.FromResult(content);
        }
    }
}
