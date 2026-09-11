namespace Threadsmith.Context;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;

/// <summary>Conservative provider-neutral token estimator for governed context budgeting.</summary>
public sealed class TokenEstimator
{
    /// <summary>Estimates tokens using a conservative four-characters-per-token heuristic.</summary>
    public static int Estimate(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length == 0 ? 0 : Math.Max(1, (value.Length + 3) / 4);
    }
}

/// <summary>Phase-specific evidence and instruction policy.</summary>
public sealed class ContextPolicy
{
    /// <summary>Gets evidence categories permitted for a phase.</summary>
    public static IReadOnlySet<EvidenceKind> GetAllowedKinds(RunPhase phase)
    {
        HashSet<EvidenceKind> allowedKinds = phase switch
        {
            RunPhase.EvidenceCollection =>
            [
                EvidenceKind.RepositoryMap,
                EvidenceKind.SourceExcerpt,
                EvidenceKind.SemanticFact,
                EvidenceKind.ToolResult,
                EvidenceKind.UserConstraint,
                EvidenceKind.Decision,
                EvidenceKind.Failure,
            ],
            RunPhase.ChangePlanning or RunPhase.AwaitingPlanApproval =>
            [
                EvidenceKind.RepositoryMap,
                EvidenceKind.SourceExcerpt,
                EvidenceKind.SemanticFact,
                EvidenceKind.ToolResult,
                EvidenceKind.UserConstraint,
                EvidenceKind.Decision,
                EvidenceKind.Diagnostic,
                EvidenceKind.Failure,
            ],
            RunPhase.MutationPreparation
                or RunPhase.ImplementationPreparing
                or RunPhase.ImplementationModelTurn
                or RunPhase.MutationProposed
                or RunPhase.MutationStaged
                or RunPhase.AwaitingMutationApproval
                or RunPhase.CorrectionPending
                or RunPhase.CorrectionModelTurn =>
            [
                EvidenceKind.RepositoryMap,
                EvidenceKind.SourceExcerpt,
                EvidenceKind.SemanticFact,
                EvidenceKind.ToolResult,
                EvidenceKind.UserConstraint,
                EvidenceKind.Decision,
                EvidenceKind.Diagnostic,
                EvidenceKind.Failure,
            ],
            RunPhase.Compilation =>
            [
                EvidenceKind.SourceExcerpt,
                EvidenceKind.Decision,
                EvidenceKind.Diagnostic,
                EvidenceKind.Failure,
            ],
            RunPhase.Testing or RunPhase.Verification =>
            [
                EvidenceKind.SourceExcerpt,
                EvidenceKind.Decision,
                EvidenceKind.Diagnostic,
                EvidenceKind.Failure,
            ],
            _ =>
            [
                EvidenceKind.UserConstraint,
                EvidenceKind.Decision,
            ],
        };
        return allowedKinds;
    }
}

/// <summary>Validated conversation context budgets and pressure policy.</summary>
public sealed record ConversationContextPolicy
{
    /// <summary>Configured default mode before a session override.</summary>
    public ConversationContextMode Mode { get; init; } = ConversationContextMode.ConversationAware;

    /// <summary>Maximum tokens used by recent complete turns.</summary>
    public int RecentTurnTokens { get; init; } = 8_000;

    /// <summary>Maximum complete prior user/assistant turns considered.</summary>
    public int RecentTurnCount { get; init; } = 12;

    /// <summary>Maximum age of raw messages eligible for the hot recent-turn window.</summary>
    public TimeSpan RecentTurnMaximumAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Context utilization that recommends next-boundary compaction.</summary>
    public int CompactionPressurePercent { get; init; } = 75;

    /// <summary>Validates hard bounds before request assembly.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RecentTurnTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(RecentTurnCount);
        if (RecentTurnMaximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RecentTurnMaximumAge));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(CompactionPressurePercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(CompactionPressurePercent, 100);
    }
}

/// <summary>Configures stable policy, prompt append assets, and context bounds.</summary>
public sealed record ContextAssemblerOptions
{
    /// <summary>Maximum estimated tokens in one assembled request.</summary>
    public int MaximumTokens { get; init; } = 32_000;

    /// <summary>Maximum retained inspection records across completed runs.</summary>
    public int MaximumInspectionRecords { get; init; } = 256;

    /// <summary>Ordered project prompt append paths from repository configuration.</summary>
    public IReadOnlyList<string> PromptAppendFiles { get; init; } = [];

    /// <summary>Conversation mode, selection, retrieval, and pressure budgets.</summary>
    public ConversationContextPolicy Conversation { get; init; } = new();
}

/// <summary>Default governed context assembler with reduction, telemetry, and execution records.</summary>
public sealed class ContextAssembler : IContextAssembler
{
    private static readonly ActivitySource _activitySource = new("Threadsmith.Context");
    private static readonly Meter _meter = new("Threadsmith.Context");
    private static readonly Histogram<long> _estimatedTokens = _meter.CreateHistogram<long>(
        "threadsmith.context.estimated_tokens",
        "tokens");

    private static readonly Histogram<long> _evidenceCount = _meter.CreateHistogram<long>(
        "threadsmith.context.evidence_count",
        "items");

    private static readonly Counter<long> _reductions = _meter.CreateCounter<long>(
        "threadsmith.context.reductions");

    private readonly IConversationStore? _conversationStore;
    private readonly IEvidenceStore _evidence;
    private readonly IDomainEventStream _events;
    private readonly Lock _gate = new();
    private readonly Dictionary<RunId, ContextInspectionProjection> _inspections = [];
    private readonly Dictionary<RunId, LinkedListNode<RunId>> _inspectionNodes = [];
    private readonly LinkedList<RunId> _inspectionOrder = [];
    private readonly IRepositoryInstructionResolver? _instructionResolver;
    private readonly IHybridRepositoryMemoryRetriever? _repositoryMemoryRetriever;
    private readonly IModelProviderInstructionResolver? _providerInstructionResolver;
    private readonly IModelRequestPreparationResolver? _requestPreparationResolver;
    private readonly IModelResolver? _modelResolver;
    private readonly ContextAssemblerOptions _options;
    private readonly ContextPolicy _policy;
    private readonly IPromptAppendLoader _promptAppendLoader;
    private readonly IPromptLoader _prompts;
    private readonly IOutputSanitizer _sanitizer;
    private readonly string _stableSystemPolicy;
    private readonly TokenEstimator _tokenEstimator;

    /// <summary>Initializes a new instance of the <see cref="ContextAssembler"/> class.</summary>
    public ContextAssembler(
        IEvidenceStore evidence,
        TokenEstimator tokenEstimator,
        ContextPolicy policy,
        IPromptAppendLoader promptAppendLoader,
        IOutputSanitizer sanitizer,
        IDomainEventStream events,
        IPromptLoader prompts,
        ContextAssemblerOptions? options = null,
        IModelResolver? modelResolver = null,
        IConversationStore? conversationStore = null,
        IRepositoryInstructionResolver? instructionResolver = null,
        IModelProviderInstructionResolver? providerInstructionResolver = null,
        IHybridRepositoryMemoryRetriever? repositoryMemoryRetriever = null,
        IModelRequestPreparationResolver? requestPreparationResolver = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(tokenEstimator);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(promptAppendLoader);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(prompts);
        _options = options ?? new ContextAssemblerOptions();
        _options.Conversation.Validate();
        if (_options.MaximumTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (_options.MaximumInspectionRecords <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _evidence = evidence;
        _tokenEstimator = tokenEstimator;
        _policy = policy;
        _promptAppendLoader = promptAppendLoader;
        _prompts = prompts;
        _stableSystemPolicy = prompts.Get(PromptFileNames.SystemSystemPrompt);
        _sanitizer = sanitizer;
        _events = events;
        _modelResolver = modelResolver;
        _conversationStore = conversationStore;
        _instructionResolver = instructionResolver;
        _repositoryMemoryRetriever = repositoryMemoryRetriever;
        _providerInstructionResolver = providerInstructionResolver;
        _requestPreparationResolver = requestPreparationResolver;
    }

    /// <inheritdoc />
    public async Task<ContextAssemblyResult> AssembleAsync(
        ContextAssemblyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Task);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Task.Intent);
        using var activity = _activitySource.StartActivity("context.assemble");
        activity?.SetTag("threadsmith.session.id", request.SessionId.Value.ToString("D"));
        activity?.SetTag("threadsmith.run.id", request.RunId.Value.ToString("D"));
        activity?.SetTag("threadsmith.context.phase", request.Phase.ToString());
        var invalidated = await _evidence.ApplyInvalidationsAsync(
            request.SessionId,
            cancellationToken);
        activity?.SetTag("threadsmith.context.evidence.invalidated", invalidated);
        var appendSegments = await _promptAppendLoader.LoadAsync(
            new PromptAppendLoadRequest(
                request.RepositoryPath,
                _options.PromptAppendFiles,
                request.ProhibitedPaths),
            cancellationToken);
        var instructionBundle = _instructionResolver is null
            ? CreatePromptAppendBundle(request.RepositoryPath, request.WorkingScope, appendSegments)
            : await _instructionResolver.ResolveAsync(
                request.RepositoryPath,
                request.WorkingScope,
                appendSegments,
                request.ProhibitedPaths,
                request.TrustGeneration,
                cancellationToken);
        var phasePromptFileName = GetPhasePromptFileName(request.Phase);
        var phaseInstructions = _prompts.Get(phasePromptFileName);
        var sanitizedTask = request.Task with
        {
            Intent = _sanitizer.Sanitize(request.Task.Intent),
            AcceptanceCriteria = request.Task.AcceptanceCriteria
                .Select(criterion => criterion with
                {
                    Description = _sanitizer.Sanitize(criterion.Description),
                })
                .ToArray(),
            UserConstraints = request.Task.UserConstraints?
                .Select(_sanitizer.Sanitize)
                .ToArray(),
        };
        var taskJson = Escape(JsonSerializer.Serialize(sanitizedTask));
        string[] currentTurnHostContext =
        [
            .. request.CurrentTurnHostContext.Select(_sanitizer.Sanitize),
        ];
        var additionalMessages = SanitizeAdditionalMessages(request.AdditionalMessages);
        var additionalMessageContent = RenderAdditionalMessages(additionalMessages);
        var structuredTaskStateJson = Escape(JsonSerializer.Serialize(new
        {
            sanitizedTask.AcceptanceCriteria,
            sanitizedTask.UserConstraints,
        }));
        var conversation = await CreateConversationStateAsync(
            request,
            sanitizedTask,
            cancellationToken);
        var repositoryMemory = await CreateRepositoryMemoryStateAsync(
            request,
            sanitizedTask,
            conversation,
            cancellationToken);
        var affectedPaths = request.ApprovedPlan?.Steps
            .SelectMany(step => step.GetAffectedPaths())
            .Select(path => path.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var mutationBaseline = request.MutationBaseline is null
            ? null
            : new
            {
                request.MutationBaseline.WorkspaceId,
                request.MutationBaseline.CapturedAt,
                request.MutationBaseline.GitRevision,
                Files = request.MutationBaseline.Files
                    .Where(file => affectedPaths.Contains(file.RelativePath))
                    .ToArray(),
                PlannedFiles = affectedPaths.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            };
        var governedState = JsonSerializer.Serialize(new
        {
            Phase = request.Phase.ToString(),
            ConversationHistoryIncluded = conversation.Mode == ConversationContextMode.ConversationAware,
            ConversationMode = conversation.Mode.ToString(),
            request.PlanUnderRevision,
            request.ApprovedPlan,
            CurrentTurnHostContext = currentTurnHostContext,
            MutationBaseline = mutationBaseline,
        });
        var canonicalTools = ModelToolCanonicalizer.Canonicalize(
            request.ToolSchemas.Select(schema => new ModelToolDefinition
            {
                Name = schema.Id,
                Description = schema.Description,
                ArgumentsJsonSchema = schema.JsonSchema,
                PreferStrictArguments = schema.PreferStrictArguments,
            }));
        var toolInventoryDigest = ModelToolCanonicalizer.ComputeDigest(canonicalTools);
        var toolSchemas = request.ToolTransportMode == ToolTransportMode.Text
            ? ModelToolCanonicalizer.RenderText(canonicalTools, _prompts)
            : string.Empty;
        var outputSchema = GetRequiredOutput(request.Phase);
        var appendContent = string.Join(
            '\n',
            instructionBundle.Sources.Select(source => source.Kind == RepositoryInstructionSourceKind.PromptAppend
                ? $"<project_context id=\"{Escape(source.Id)}\" version=\"{Escape(source.Version)}\">\n"
                    + Escape(source.Content)
                    + "\n</project_context>"
                : $"<repository_instruction kind=\"{source.Kind}\" id=\"{Escape(source.Id)}\" "
                    + $"path=\"{Escape(source.RelativePath)}\" version=\"{Escape(source.Version)}\" untrusted=\"true\">\n"
                    + Escape(source.Content)
                    + "\n</repository_instruction>"));

        var tokensByCategory = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["systemPolicy"] = TokenEstimator.Estimate(_stableSystemPolicy),
            ["promptAppend"] = TokenEstimator.Estimate(appendContent),
            ["phaseInstructions"] = TokenEstimator.Estimate(phaseInstructions),
            ["task"] = TokenEstimator.Estimate(taskJson),
            ["currentTurn"] = TokenEstimator.Estimate(conversation.CurrentTurnContent),
            ["recentTurns"] = TokenEstimator.Estimate(conversation.RecentTurnsContent),
            ["conversationSummary"] = TokenEstimator.Estimate(string.Empty),
            ["retrievedMemory"] = TokenEstimator.Estimate(string.Empty),
            ["repositoryMemory"] = TokenEstimator.Estimate(repositoryMemory.Content),
            ["governedState"] = TokenEstimator.Estimate(governedState),
            ["additionalMessages"] = TokenEstimator.Estimate(additionalMessageContent),
            ["toolSchemas"] = TokenEstimator.Estimate(toolSchemas),
            ["nativeToolSchemas"] = request.ToolTransportMode == ToolTransportMode.Native
                ? TokenEstimator.Estimate(JsonSerializer.Serialize(canonicalTools))
                : 0,
            ["providerInstructions"] = 0,
            ["wireFraming"] = 0,
            ["outputSchema"] = TokenEstimator.Estimate(outputSchema),
        };
        var fixedTokens = tokensByCategory.Values.Sum();
        var optionalConversationTokens = tokensByCategory["recentTurns"]
            + tokensByCategory["conversationSummary"]
            + tokensByCategory["retrievedMemory"]
            + tokensByCategory["repositoryMemory"] - repositoryMemory.RequiredTokens;
        var allowedKinds = ContextPolicy.GetAllowedKinds(request.Phase);
        Evidence[] candidates = [.. _evidence.Snapshot(request.SessionId)
            .Where(item => item.RunId is null || item.RunId == request.RunId)
            .OrderByDescending(item => item.Kind == EvidenceKind.Decision)
            .ThenByDescending(item => item.Relevance)
            .ThenBy(item => item.CollectedAt)
            .ThenBy(item => item.EvidenceId.Value)];
        var workloadClass = ResolveWorkloadClass(request.Phase);
        var tokenBudget = _modelResolver?.MaximumInputTokenBudget ?? _options.MaximumTokens;
        var requiredFixedTokens = fixedTokens - optionalConversationTokens;
        if (requiredFixedTokens > tokenBudget)
        {
            throw new InvalidOperationException(
                $"Required governed framing and current input need {requiredFixedTokens} tokens but the budget is "
                + $"{tokenBudget}.");
        }

        var evidenceProjections = new List<ContextEvidenceProjection>();
        var reductions = new List<string>();
        var selected = new List<Evidence>();
        var selectedTokens = 0;
        var contentHashes = new HashSet<string>(StringComparer.Ordinal);
        var instructionEvidence = new InstructionEvidenceMatcher(instructionBundle);
        var instructionEvidenceIsSensitive = false;
        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = item.EstimatedTokens > 0
                ? item.EstimatedTokens
                : TokenEstimator.Estimate(item.Content);
            string? omissionReason = null;
            if (item.IsStale)
            {
                omissionReason = item.StaleReason ?? "Evidence is stale.";
            }
            else if (!allowedKinds.Contains(item.Kind))
            {
                omissionReason = $"{item.Kind} is excluded by {request.Phase} context policy.";
            }
            else if (instructionEvidence.IsAlreadyIncluded(item))
            {
                omissionReason = "Exact file-read content is already present in the current repository instruction bundle.";
                instructionEvidenceIsSensitive |= item.Sensitivity == EvidenceSensitivity.Sensitive;
            }
            else
            {
                var hash = Convert.ToHexStringLower(
                    SHA256.HashData(Encoding.UTF8.GetBytes(item.Content)));
                if (!contentHashes.Add(hash))
                {
                    omissionReason = "Duplicate content was reduced to its strongest evidence item.";
                }
                else if (fixedTokens + selectedTokens + tokens > tokenBudget)
                {
                    omissionReason = "Omitted to fit the selected model input-token budget.";
                }
            }

            if (omissionReason is not null)
            {
                evidenceProjections.Add(new ContextEvidenceProjection(
                    item.EvidenceId,
                    item.Kind.ToString(),
                    Included: false,
                    omissionReason,
                    tokens,
                    item.IsStale));
                reductions.Add($"{item.EvidenceId.Value:D}: {omissionReason}");
                continue;
            }

            selected.Add(item);
            selectedTokens += tokens;
            evidenceProjections.Add(new ContextEvidenceProjection(
                item.EvidenceId,
                item.Kind.ToString(),
                Included: true,
                $"Included by {request.Phase} policy with relevance {item.Relevance:F2}.",
                tokens,
                IsStale: false));
        }

        var constraints = request.ModelConstraints with
        {
            ContainsSensitiveData = request.ModelConstraints.ContainsSensitiveData
                || selected.Any(item => item.Sensitivity == EvidenceSensitivity.Sensitive)
                || instructionEvidenceIsSensitive
                || conversation.ContainsSensitiveData
                || repositoryMemory.ContainsSensitiveData,
        };
        var modelResolution = _modelResolver?.Resolve(
            workloadClass,
            request.RequiredCapabilities,
            constraints,
            request.DefaultModelProfileId);
        var providerInstructions = modelResolution is null
            ? null
            : _providerInstructionResolver?.Resolve(modelResolution.ProfileId);
        tokensByCategory["providerInstructions"] = providerInstructions is null
            ? 0
            : TokenEstimator.Estimate(providerInstructions.Content);
        tokenBudget = ResolveInputTokenBudget(modelResolution, _options.MaximumTokens);

        var evidenceContent = BuildEvidenceContent(selected);
        var modelInput = BuildModelInput(
            appendContent,
            phaseInstructions,
            taskJson,
            conversation,
            repositoryMemory,
            governedState,
            evidenceContent,
            toolSchemas,
            outputSchema,
            additionalMessageContent);
        var totalTokens = EstimateCompleteInputTokens(modelInput, evidenceContent);
        while (totalTokens > tokenBudget
            && (conversation.CanReduce || repositoryMemory.CanReduce || selected.Count > 0))
        {
            if (conversation.TryReduce() || repositoryMemory.TryReduce())
            {
                modelInput = BuildModelInput(
                    appendContent,
                    phaseInstructions,
                    taskJson,
                    conversation,
                    repositoryMemory,
                    governedState,
                    evidenceContent,
                    toolSchemas,
                    outputSchema,
                    additionalMessageContent);
                totalTokens = EstimateCompleteInputTokens(modelInput, evidenceContent);
                continue;
            }

            var removed = selected[^1];
            selected.RemoveAt(selected.Count - 1);
            var projectionIndex = evidenceProjections.FindIndex(
                item => item.EvidenceId == removed.EvidenceId);
            const string reason = "Omitted during final reduction to include request framing within the token budget.";
            if (projectionIndex >= 0)
            {
                evidenceProjections[projectionIndex] = evidenceProjections[projectionIndex] with
                {
                    Included = false,
                    Rationale = reason,
                };
            }

            reductions.Add($"{removed.EvidenceId.Value:D}: {reason}");
            evidenceContent = BuildEvidenceContent(selected);
            modelInput = BuildModelInput(
                appendContent,
                phaseInstructions,
                taskJson,
                conversation,
                repositoryMemory,
                governedState,
                evidenceContent,
                toolSchemas,
                outputSchema,
                additionalMessageContent);
            totalTokens = EstimateCompleteInputTokens(modelInput, evidenceContent);
        }

        int EstimateCompleteInputTokens(string currentModelInput, string currentEvidenceContent)
        {
            var legacyTokens = EstimateWireInputTokens(
                currentModelInput,
                tokensByCategory["nativeToolSchemas"],
                tokensByCategory["wireFraming"]);
            var currentMessages = BuildStructuredMessages(
                appendContent,
                phaseInstructions,
                structuredTaskStateJson,
                conversation,
                repositoryMemory,
                governedState,
                currentEvidenceContent,
                toolSchemas,
                outputSchema,
                additionalMessages);
            var stablePrefixCount = Math.Min(3, currentMessages.Count);
            var currentEstimate = ModelWireEstimator.Estimate(
                currentMessages,
                canonicalTools,
                request.ToolTransportMode,
                stablePrefixCount,
                modelResolution?.EffectiveRequestOutputTokenReserve ?? 0,
                providerInstructions,
                _prompts);
            var prepared = PrepareWire(currentModelInput, currentMessages, currentEstimate, null);
            return Math.Max(legacyTokens, prepared.WireInputTokens);
        }

        ModelWireEstimate PrepareWire(
            string input,
            IReadOnlyList<ModelMessage> requestMessages,
            ModelWireEstimate estimate,
            ModelRequestLayout? requestLayout)
        {
            if (_requestPreparationResolver is null || modelResolution is null)
            {
                return estimate;
            }

            var candidate = _requestPreparationResolver.Prepare(new ModelStreamRequest
            {
                RunId = request.RunId,
                Input = input,
                ResolvedProfileId = modelResolution.ProfileId,
                MaximumOutputTokens = modelResolution.EffectiveRequestOutputTokenReserve,
                ReasoningLevel = modelResolution.DefaultReasoningLevel,
                IncludeReasoningText = false,
                Messages = requestMessages,
                Tools = canonicalTools,
                ToolTransportMode = request.ToolTransportMode,
                ProviderInstructions = providerInstructions,
                Layout = requestLayout,
                WireEstimate = estimate,
            });
            return candidate.WireEstimate ?? estimate;
        }

        if (totalTokens > tokenBudget)
        {
            throw new InvalidOperationException(
                $"Governed request framing requires {totalTokens} tokens but the budget is "
                + $"{tokenBudget}.");
        }

        tokensByCategory["currentTurn"] = TokenEstimator.Estimate(conversation.CurrentTurnContent);
        tokensByCategory["recentTurns"] = TokenEstimator.Estimate(conversation.RecentTurnsContent);
        tokensByCategory["conversationSummary"] = TokenEstimator.Estimate(string.Empty);
        tokensByCategory["retrievedMemory"] = TokenEstimator.Estimate(string.Empty);
        tokensByCategory["repositoryMemory"] = TokenEstimator.Estimate(repositoryMemory.Content);
        tokensByCategory["evidence"] = TokenEstimator.Estimate(evidenceContent);
        tokensByCategory["assemblyOverhead"] = Math.Max(
            0,
            totalTokens - tokensByCategory.Values.Sum());
        string[] modelRationale = modelResolution is null
            ? []
            :
            [
                .. modelResolution.Rationale,
                .. modelResolution.AppliedHints.Select(hint =>
                            $"Applied hint {hint.Source}: {hint.Reason}"),
                .. modelResolution.IgnoredHints.Select(hint =>
                            $"Ignored hint {hint.Source}: {hint.Reason}"),
            ];
        var promptAssets = new List<PromptAssetReference>
        {
            CreateAssetReference("host:stable-policy", PromptFileNames.SystemSystemPrompt, 0, _stableSystemPolicy),
        };
        promptAssets.AddRange(instructionBundle.Sources.Select(source => new PromptAssetReference(
            source.Id,
            source.Version,
            source.RelativePath,
            source.Position + 1,
            source.Content.Length)));
        promptAssets.Add(CreateAssetReference(
            $"host:phase:{request.Phase}",
            phasePromptFileName,
            promptAssets.Count,
            phaseInstructions));
        var messages = BuildStructuredMessages(
            appendContent,
            phaseInstructions,
            structuredTaskStateJson,
            conversation,
            repositoryMemory,
            governedState,
            evidenceContent,
            toolSchemas,
            outputSchema,
            additionalMessages);
        var stablePrefixMessageCount = Math.Min(3, messages.Count);
        var stablePrefixDigest = ComputeMessageDigest(
            messages.Take(stablePrefixMessageCount),
            providerInstructions);
        var cacheFamily = $"layout-v{ModelRequestLayout.CurrentVersion}:{request.Phase}:"
            + $"{stablePrefixDigest}:{instructionBundle.Digest}:{toolInventoryDigest}";
        var layout = new ModelRequestLayout
        {
            CacheFamily = cacheFamily,
            StablePrefixDigest = stablePrefixDigest,
            StablePrefixMessageCount = stablePrefixMessageCount,
            Segments = CreateCanonicalSegments(messages, providerInstructions),
        };
        var wireEstimate = ModelWireEstimator.Estimate(
            messages,
            canonicalTools,
            request.ToolTransportMode,
            stablePrefixMessageCount,
            modelResolution?.EffectiveRequestOutputTokenReserve ?? 0,
            providerInstructions,
            _prompts);
        wireEstimate = PrepareWire(modelInput, messages, wireEstimate, layout);
        if (wireEstimate.WireInputTokens > tokenBudget)
        {
            throw new InvalidOperationException(
                $"Structured provider wire input requires {wireEstimate.WireInputTokens} tokens but the budget is "
                + $"{tokenBudget}.");
        }

        totalTokens = wireEstimate.WireInputTokens;
        var effectiveContextWindow = modelResolution?.ContextWindow ?? _options.MaximumTokens;
        var contextPressurePercent = totalTokens * 100d / effectiveContextWindow;
        var compactionRecommended = contextPressurePercent
            >= _options.Conversation.CompactionPressurePercent;
        var inspection = new ContextInspectionProjection
        {
            RunId = request.RunId,
            Phase = request.Phase,
            EstimatedTokens = totalTokens,
            TokenBudget = tokenBudget,
            TokensByCategory = new ReadOnlyDictionary<string, int>(
                tokensByCategory.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal)),
            Evidence = evidenceProjections,
            PromptAssets = promptAssets,
            ModelProfileId = modelResolution?.ProfileId,
            ModelRationale = modelRationale,
            Reductions = [.. conversation.Reductions, .. repositoryMemory.Reductions, .. reductions],
            ConversationMode = conversation.Mode,
            ConversationModeSource = conversation.ModeSource,
            CurrentMessageId = request.CurrentMessageId,
            ConversationSummaryVersion = null,
            CompactedThroughMessageSequence = null,
            ConversationItems = conversation.CreateProjections(),
            RepositoryMemoryItems = repositoryMemory.CreateProjections(),
            ContextPressurePercent = contextPressurePercent,
            CompactionRecommended = compactionRecommended,
            CompactionRationale = compactionRecommended
                ? $"Context pressure reached {_options.Conversation.CompactionPressurePercent}% of the selected model window."
                : "No compaction pressure threshold was reached.",
            RequestLayoutVersion = layout.Version,
            CacheFamily = layout.CacheFamily,
            StablePrefixDigest = layout.StablePrefixDigest,
            ToolInventoryDigest = toolInventoryDigest,
            InstructionBundleDigest = instructionBundle.Digest,
            LogicalTokens = wireEstimate.LogicalTokens,
            WireInputTokens = wireEstimate.WireInputTokens,
            StablePrefixTokens = wireEstimate.StablePrefixTokens,
            NativeToolTokens = wireEstimate.NativeToolTokens,
            TextToolTokens = wireEstimate.TextToolTokens,
            FramingTokens = wireEstimate.FramingTokens,
            ProviderInstructionTokens = wireEstimate.ProviderInstructionTokens,
            ToolTransportMode = request.ToolTransportMode.ToString(),
        };
        lock (_gate)
        {
            _inspections[request.RunId] = inspection;
            if (_inspectionNodes.Remove(request.RunId, out var existingNode))
            {
                _inspectionOrder.Remove(existingNode);
            }

            _inspectionNodes[request.RunId] = _inspectionOrder.AddLast(request.RunId);
            while (_inspections.Count > _options.MaximumInspectionRecords)
            {
                var oldest = _inspectionOrder.First
                    ?? throw new InvalidOperationException("Inspection retention state is inconsistent.");
                _inspectionOrder.RemoveFirst();
                _inspectionNodes.Remove(oldest.Value);
                _inspections.Remove(oldest.Value);
            }
        }

        _estimatedTokens.Record(totalTokens, new KeyValuePair<string, object?>(
            "threadsmith.context.phase",
            request.Phase.ToString()));
        _evidenceCount.Record(selected.Count, new KeyValuePair<string, object?>(
            "threadsmith.context.phase",
            request.Phase.ToString()));
        var reductionCount = reductions.Count + conversation.Reductions.Count + repositoryMemory.Reductions.Count;
        if (reductionCount > 0)
        {
            _reductions.Add(reductionCount, new KeyValuePair<string, object?>(
                "threadsmith.context.phase",
                request.Phase.ToString()));
        }

        activity?.SetTag("threadsmith.context.estimated_tokens", totalTokens);
        activity?.SetTag("threadsmith.context.evidence.included", selected.Count);
        activity?.SetTag("threadsmith.context.evidence.omitted", candidates.Length - selected.Count);
        activity?.SetStatus(ActivityStatusCode.Ok);
        await _events.PublishAsync(
            new ContextAssembled(request.SessionId, DateTimeOffset.UtcNow, inspection),
            cancellationToken);
        return new ContextAssemblyResult(
            modelInput,
            workloadClass,
            request.RequiredCapabilities,
            constraints,
            modelResolution,
            inspection,
            messages,
            layout,
            wireEstimate,
            toolInventoryDigest,
            instructionBundle.Digest,
            providerInstructions,
            repositoryMemory.CreateInclusions());
    }

    /// <inheritdoc />
    public ContextInspectionProjection? GetInspection(RunId runId)
    {
        lock (_gate)
        {
            if (_inspectionNodes.TryGetValue(runId, out var node))
            {
                _inspectionOrder.Remove(node);
                _inspectionNodes[runId] = _inspectionOrder.AddLast(runId);
            }

            return _inspections.TryGetValue(runId, out var inspection)
                ? inspection with
                {
                    TokensByCategory = new ReadOnlyDictionary<string, int>(
                        inspection.TokensByCategory.ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.Ordinal)),
                    Evidence = inspection.Evidence.ToArray(),
                    PromptAssets = inspection.PromptAssets.ToArray(),
                    ModelRationale = inspection.ModelRationale.ToArray(),
                    Reductions = inspection.Reductions.ToArray(),
                    ConversationItems = inspection.ConversationItems.Select(item => item with
                    {
                        SourceMessageIds = item.SourceMessageIds.ToArray(),
                        SourceRunIds = item.SourceRunIds.ToArray(),
                        SourceEvidenceIds = item.SourceEvidenceIds.ToArray(),
                    }).ToArray(),
                    RepositoryMemoryItems = inspection.RepositoryMemoryItems.ToArray(),
                }
                : null;
        }
    }

    /// <inheritdoc />
    public Task UpdateActiveTurnInspectionAsync(
        SessionId sessionId,
        RunId runId,
        ActiveTurnCompactionInspectionProjection activeTurn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activeTurn);
        return UpdateInspectionAsync(
            sessionId,
            runId,
            inspection => inspection with { ActiveTurnCompaction = activeTurn },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task UpdateVisibleSourceFrontierInspectionAsync(
        SessionId sessionId,
        RunId runId,
        VisibleSourceFrontierInspectionProjection visibleSourceFrontier,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(visibleSourceFrontier);
        return UpdateInspectionAsync(
            sessionId,
            runId,
            inspection => inspection with { VisibleSourceFrontier = visibleSourceFrontier },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task UpdateRepositoryMemoryDispatchInspectionAsync(
        SessionId sessionId,
        RunId runId,
        RepositoryMemoryDispatchInspection dispatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        return UpdateInspectionAsync(
            sessionId,
            runId,
            inspection => inspection with { RepositoryMemoryDispatch = dispatch with { Inclusions = dispatch.Inclusions.ToArray() } },
            cancellationToken);
    }

    /// <inheritdoc />
    public void InvalidateInspections()
    {
        lock (_gate)
        {
            _inspections.Clear();
            _inspectionOrder.Clear();
            _inspectionNodes.Clear();
        }
    }

    private async Task UpdateInspectionAsync(
        SessionId sessionId,
        RunId runId,
        Func<ContextInspectionProjection, ContextInspectionProjection> update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        ContextInspectionProjection? updated = null;
        lock (_gate)
        {
            if (_inspections.TryGetValue(runId, out var inspection))
            {
                updated = update(inspection);
                _inspections[runId] = updated;
                if (_inspectionNodes.TryGetValue(runId, out var node))
                {
                    _inspectionOrder.Remove(node);
                    _inspectionNodes[runId] = _inspectionOrder.AddLast(runId);
                }
            }
        }

        if (updated is not null)
        {
            await _events.PublishAsync(
                new ContextAssembled(sessionId, DateTimeOffset.UtcNow, updated),
                cancellationToken);
        }
    }

    private async Task<ConversationAssemblyState> CreateConversationStateAsync(
        ContextAssemblyRequest request,
        TaskSpecification task,
        CancellationToken cancellationToken)
    {
        var state = _conversationStore is null
            ? new ConversationStateSnapshot { SessionId = request.SessionId }
            : await _conversationStore.GetSnapshotAsync(
                request.SessionId,
                includeBodies: true,
                cancellationToken);
        var mode = request.ConversationModeOverride
            ?? (_conversationStore is null ? _options.Conversation.Mode : state.Mode);
        var modeSource = request.ConversationModeOverride is not null
            ? request.ConversationModeSource ?? "session-override"
            : _conversationStore is null ? "configuration" : "session-state";
        var current = request.CurrentMessageId is { } currentId
            ? state.Messages.FirstOrDefault(message => message.Id == currentId)
            : null;
        var currentContent = _sanitizer.Sanitize(current?.Content ?? task.Intent);
        var assembly = new ConversationAssemblyState(
            mode,
            modeSource,
            currentContent,
            current?.Sensitivity == ConversationSensitivity.Sensitive);
        var recentCutoff = DateTimeOffset.UtcNow - _options.Conversation.RecentTurnMaximumAge;
        ConversationMessage[] allPriorMessages =
        [
            .. state.Messages.Where(message =>
                request.CurrentMessageId is null || message.Id != request.CurrentMessageId),
        ];
        var priorMessages = allPriorMessages.Where(message =>
            message.OccurredAt >= recentCutoff);
        if (mode == ConversationContextMode.ConversationAware)
        {
            foreach (var message in allPriorMessages.Where(message =>
                message.OccurredAt < recentCutoff))
            {
                assembly.AddExcludedMessage(message, "Message is outside the hot recent-turn age window.");
            }

            var turns = CreateCompleteTurns(priorMessages, state.Messages);
            foreach (var turn in turns
                .TakeLast(_options.Conversation.RecentTurnCount))
            {
                assembly.AddRecentTurn(turn);
            }

            while (TokenEstimator.Estimate(assembly.RecentTurnsContent)
                > _options.Conversation.RecentTurnTokens
                && assembly.RemoveOldestRecentTurn("Omitted oldest complete turn to fit the recent-turn budget."))
            {
            }
        }
        else
        {
            foreach (var message in allPriorMessages)
            {
                assembly.AddExcludedMessage(
                    message,
                    $"Raw prior messages are excluded by {mode} mode.");
            }
        }

        // Historical automatic snapshots are archive metadata, never a current prompt source.
        // Model-generated active-turn compaction remains owned by ActiveTurnCompaction.
        return assembly;
    }

    private async Task<RepositoryMemoryAssemblyState> CreateRepositoryMemoryStateAsync(
        ContextAssemblyRequest request,
        TaskSpecification task,
        ConversationAssemblyState conversation,
        CancellationToken cancellationToken)
    {
        var assembly = new RepositoryMemoryAssemblyState(
            2_000,
            _prompts.Get(PromptFileNames.SystemRepositoryMemoryGuidance),
            _prompts.Get(PromptFileNames.SystemStandingPreferenceGuidance));
        if (_repositoryMemoryRetriever is null || !request.RepositoryMemoriesEnabled
            || conversation.Mode == ConversationContextMode.Stateless)
        {
            return assembly;
        }

        var repositoryIdentity = string.IsNullOrWhiteSpace(request.RepositoryIdentity)
            ? RepositoryIdentity.Create(request.RepositoryPath) : request.RepositoryIdentity;
        var retrieval = await _repositoryMemoryRetriever.RetrieveAsync(
            new RepositoryMemoryRetrievalRequest
            {
                RepositoryIdentity = repositoryIdentity,
                UserTurnId = request.RunId,
                CurrentInstruction = _sanitizer.Sanitize(request.RepositoryMemoryCurrentInstruction ?? conversation.CurrentTurnContent),
                TaskIntent = task.Intent,
                Options = request.RepositoryMemoryOptions ?? new RepositoryMemoryOptions(),
            },
            cancellationToken);
        foreach (var entry in retrieval.StandingPreferences)
        {
            cancellationToken.ThrowIfCancellationRequested();
            assembly.AddStandingPreference(entry with { Text = _sanitizer.Sanitize(entry.Text) });
        }

        foreach (var candidate in retrieval.Selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sanitized = candidate with { Entry = candidate.Entry with { Text = _sanitizer.Sanitize(candidate.Entry.Text) } };
            var tokens = assembly.EstimateAddition(sanitized);
            if (!assembly.TryAdd(sanitized, tokens))
            {
                assembly.AddExcluded(sanitized, "Omitted to fit the repository-memory token budget.", tokens);
            }
        }

        assembly.Reductions.AddRange(retrieval.Diagnostics);
        assembly.Reductions.Add($"Memory query cache reused: {retrieval.QueryEmbeddingCacheHit}; ranking cache reused: {retrieval.RankingCacheHit}.");
        return assembly;
    }

    private static WorkloadClass ResolveWorkloadClass(RunPhase phase)
    {
        return phase switch
        {
            RunPhase.ChangePlanning or RunPhase.AwaitingPlanApproval => WorkloadClass.Planning,
            RunPhase.MutationPreparation
                or RunPhase.ImplementationPreparing
                or RunPhase.ImplementationModelTurn
                or RunPhase.MutationProposed
                or RunPhase.MutationStaged
                or RunPhase.AwaitingMutationApproval
                or RunPhase.CorrectionPending
                or RunPhase.CorrectionModelTurn => WorkloadClass.CodeEdit,
            _ => WorkloadClass.General,
        };
    }

    private static int ResolveInputTokenBudget(ModelResolution? resolution, int fallbackBudget)
    {
        if (resolution is null)
        {
            return fallbackBudget;
        }

        var requestOutputTokenReserve = resolution.EffectiveRequestOutputTokenReserve;
        if (resolution.ContextWindow > 0
            && resolution.MaximumOutputTokens == 0
            && requestOutputTokenReserve == 0)
        {
            return resolution.ContextWindow;
        }

        if (resolution.ContextWindow <= 0
            || resolution.MaximumOutputTokens <= 0
            || resolution.MaximumOutputTokens > resolution.ContextWindow
            || requestOutputTokenReserve <= 0
            || requestOutputTokenReserve >= resolution.ContextWindow
            || requestOutputTokenReserve > resolution.MaximumOutputTokens)
        {
            throw new InvalidOperationException(
                "The selected model profile has an invalid context or output-token capacity.");
        }

        return resolution.ContextWindow - requestOutputTokenReserve;
    }

    private static List<IReadOnlyList<ConversationMessage>> CreateCompleteTurns(
        IEnumerable<ConversationMessage> messages,
        IReadOnlyList<ConversationMessage> archive)
    {
        ConversationMessage[] ordered = [.. messages.OrderBy(message => message.Sequence)];
        var turns = new List<IReadOnlyList<ConversationMessage>>();
        var pendingUsers = new Dictionary<RunId, ConversationMessage>();
        foreach (var message in ordered)
        {
            if (message.Role == ConversationRole.User)
            {
                pendingUsers[message.RunId] = message;
            }
            else if (message.Role == ConversationRole.Assistant
                && pendingUsers.Remove(message.RunId, out var user))
            {
                // Overlapping runs may finish in a different order from their requests.
                // Pair by identity and retain complete exchanges in completion order.
                turns.Add([user, message]);
            }
        }

        // Older clones assigned a different run id to every copied message. Retain their
        // original adjacent pairs only when neither side has a counterpart anywhere in the
        // archive; filtering the current/expired messages cannot create legacy eligibility.
        HashSet<RunId> userRuns = [.. archive.Where(message => message.Role == ConversationRole.User)
            .Select(message => message.RunId)];
        HashSet<RunId> assistantRuns = [.. archive.Where(message => message.Role == ConversationRole.Assistant)
            .Select(message => message.RunId)];
        for (var index = 0; index + 1 < ordered.Length; index++)
        {
            var user = ordered[index];
            var assistant = ordered[index + 1];
            if (user.Role == ConversationRole.User
                && assistant.Role == ConversationRole.Assistant
                && assistant.Sequence == user.Sequence + 1
                && user.SchemaVersion == 1
                && assistant.SchemaVersion == 1
                && !assistantRuns.Contains(user.RunId)
                && !userRuns.Contains(assistant.RunId))
            {
                turns.Add([user, assistant]);
            }
        }

        return [.. turns.OrderBy(turn => turn[1].Sequence)];
    }

    private static PromptAssetReference CreateAssetReference(
        string id,
        string source,
        int position,
        string content)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        return new PromptAssetReference(id, $"sha256:{hash}", source, position, content.Length);
    }

    private static string BuildEvidenceContent(IReadOnlyList<Evidence> selected)
    {
        return string.Join(
        '\n',
        selected.Select(item =>
        {
            var digest = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(item.Content)));
            var sourcePath = item.Provenance.SourcePath is null
                ? string.Empty
                : $" path=\"{Escape(item.Provenance.SourcePath)}\"";
            var revision = item.Provenance.RepositoryRevision is null
                ? string.Empty
                : $" revision=\"{Escape(item.Provenance.RepositoryRevision)}\"";
            var invocation = item.Provenance.ToolInvocationId is null
                ? string.Empty
                : $" tool_invocation=\"{item.Provenance.ToolInvocationId.Value.Value:D}\"";
            return $"<evidence id=\"sha256:{digest}\" kind=\"{item.Kind}\" "
                + $"source=\"{Escape(item.Provenance.Source)}\" confidence=\"{item.Provenance.SemanticConfidence}\""
                + sourcePath
                + revision
                + invocation
                + " untrusted=\"true\">\n"
                + Escape(FileReadEvidenceRenderer.Render(item))
                + "\n</evidence>";
        }));
    }

    private static string Escape(string value)
    {
        return SecurityElement.Escape(value) ?? string.Empty;
    }

    private static RepositoryInstructionBundle CreatePromptAppendBundle(
        string repositoryPath,
        string? workingScope,
        IReadOnlyList<PromptAppendSegment> appendSegments)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var scope = string.IsNullOrWhiteSpace(workingScope)
            ? string.Empty
            : Path.GetRelativePath(root, Path.GetFullPath(workingScope, root)).Replace('\\', '/');
        RepositoryInstructionSource[] sources = [.. appendSegments
            .OrderBy(segment => segment.Position)
            .Select((segment, position) => new RepositoryInstructionSource(
                RepositoryInstructionSourceKind.PromptAppend,
                segment.Id,
                segment.SourcePath,
                segment.Version,
                segment.Content,
                position))];
        var identity = string.Join('\n', sources.Select(source =>
            $"{source.Id}|{source.Version}|{source.Position}"));
        var digest = "sha256:"
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return new RepositoryInstructionBundle
        {
            RepositoryRoot = root,
            WorkingScope = scope,
            Sources = sources,
            Digest = digest,
        };
    }

    private static int EstimateWireInputTokens(
        string modelInput,
        int nativeToolTokens,
        int framingTokens)
    {
        return checked(TokenEstimator.Estimate(modelInput) + nativeToolTokens + framingTokens);
    }

    private static string ComputeMessageDigest(
        IEnumerable<ModelMessage> messages,
        ModelProviderInstructions? providerInstructions)
    {
        if (providerInstructions is null)
        {
            var messageEncoding = JsonSerializer.Serialize(messages.Select(message => new
            {
                message.Role,
                message.SectionId,
                message.ToolCallId,
                message.ToolName,
                Content = message.GetModelVisibleContent(),
            }));
            return "sha256:"
                + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(messageEncoding)));
        }

        var encoded = JsonSerializer.Serialize(new
        {
            ProviderInstructions = new
            {
                providerInstructions.SectionId,
                providerInstructions.Content,
            },
            Messages = messages.Select(message => new
            {
                message.Role,
                message.SectionId,
                message.ToolCallId,
                message.ToolName,
                Content = message.GetModelVisibleContent(),
            }),
        });
        return "sha256:"
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(encoded)));
    }

    private static IReadOnlyList<CanonicalContextSegment> CreateCanonicalSegments(
        IReadOnlyList<ModelMessage> messages,
        ModelProviderInstructions? providerInstructions)
    {
        var segments = new List<CanonicalContextSegment>();
        if (providerInstructions is not null)
        {
            var contentDigest = "sha256:" + Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(providerInstructions.Content)));
            segments.Add(new CanonicalContextSegment(
                providerInstructions.SectionId,
                ContextVolatilityClass.Process,
                contentDigest,
                TokenEstimator.Estimate(providerInstructions.Content)));
        }

        segments.AddRange(messages.Select(message =>
        {
            var content = message.GetModelVisibleContent();
            var volatility = message.SectionId switch
            {
                "host-policy" => ContextVolatilityClass.Process,
                "repository-instructions" => ContextVolatilityClass.Repository,
                "phase-policy" => ContextVolatilityClass.Phase,
                "conversation-summary" => ContextVolatilityClass.Session,
                "recent-user" or "recent-assistant" => ContextVolatilityClass.Turn,
                "current-user" => ContextVolatilityClass.Request,
                _ => ContextVolatilityClass.Request,
            };
            return new CanonicalContextSegment(
                message.SectionId,
                volatility,
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))),
                TokenEstimator.Estimate(content));
        }));
        return segments;
    }

    private static string GetPhasePromptFileName(RunPhase phase)
    {
        return phase switch
        {
            RunPhase.EvidenceCollection => PromptFileNames.SystemPhaseEvidenceCollection,
            RunPhase.ChangePlanning or RunPhase.AwaitingPlanApproval => PromptFileNames.SystemPhaseChangePlanning,
            RunPhase.MutationPreparation or RunPhase.ImplementationPreparing or RunPhase.ImplementationModelTurn
                or RunPhase.CorrectionPending or RunPhase.CorrectionModelTurn =>
                PromptFileNames.SystemPhaseMutationProposal,
            RunPhase.AwaitingMutationApproval => PromptFileNames.SystemPhaseAwaitingMutationApproval,
            RunPhase.Compilation => PromptFileNames.SystemPhaseCompilation,
            RunPhase.Testing or RunPhase.Verification => PromptFileNames.SystemPhaseValidation,
            _ => PromptFileNames.SystemPhaseDefault,
        };
    }

    private string GetRequiredOutput(RunPhase phase)
    {
        var name = phase switch
        {
            RunPhase.EvidenceCollection => PromptFileNames.SystemRequiredOutputEvidenceCollection,
            RunPhase.MutationPreparation or RunPhase.ImplementationPreparing or RunPhase.ImplementationModelTurn
                or RunPhase.CorrectionPending or RunPhase.CorrectionModelTurn =>
                PromptFileNames.SystemRequiredOutputMutationProposal,
            _ => PromptFileNames.SystemRequiredOutputPlan,
        };
        return _prompts.Get(name);
    }

    private IReadOnlyList<ModelMessage> BuildStructuredMessages(
        string appendContent,
        string phaseInstructions,
        string taskStateJson,
        ConversationAssemblyState conversation,
        RepositoryMemoryAssemblyState repositoryMemory,
        string governedState,
        string evidenceContent,
        string toolSchemas,
        string outputSchema,
        IReadOnlyList<ModelMessage> additionalMessages)
    {
        var repositoryInstructions = string.IsNullOrWhiteSpace(appendContent)
            ? _prompts.Get(PromptFileNames.SystemRepositoryInstructionsNone)
            : appendContent;
        var messages = new List<ModelMessage>
        {
            CreateTextMessage(ModelMessageRole.System, "host-policy", _stableSystemPolicy),
            CreateTextMessage(ModelMessageRole.System, "phase-policy", phaseInstructions),
            CreateTextMessage(
                ModelMessageRole.Developer,
                "repository-instructions",
                repositoryInstructions),
        };
        var additionalPrefix = additionalMessages.TakeWhile(message => message.Role is ModelMessageRole.System or ModelMessageRole.Developer).ToArray();
        messages.AddRange(additionalPrefix);
        messages.AddRange(conversation.CreateRecentMessages());
        if (!string.IsNullOrWhiteSpace(repositoryMemory.Content))
        {
            messages.Add(CreateTextMessage(
                ModelMessageRole.HostContext,
                "repository-memory",
                repositoryMemory.Content));
        }

        messages.Add(CreateTextMessage(
            ModelMessageRole.HostContext,
            "governed-request-state",
            _prompts.Render(
                PromptFileNames.SystemGovernedRequestState,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["TaskState"] = $"<task_state>{taskStateJson}</task_state>",
                    ["GovernedState"] = $"\n<governed_state>{Escape(governedState)}</governed_state>",
                    ["EvidenceSet"] = string.IsNullOrWhiteSpace(evidenceContent)
                        ? string.Empty
                        : $"\n<evidence_set>{evidenceContent}</evidence_set>",
                    ["ToolInventory"] = string.IsNullOrWhiteSpace(toolSchemas)
                        ? "\n" + _prompts.Get(PromptFileNames.SystemToolInventoryNativeSeparate)
                        : $"\n<available_tools>{toolSchemas}</available_tools>",
                    ["RequiredOutput"] = $"\n<required_output>{Escape(outputSchema)}</required_output>",
                })));
        messages.Add(CreateTextMessage(
            ModelMessageRole.User,
            "current-user",
            conversation.CurrentTurnContent));
        messages.AddRange(additionalMessages.Skip(additionalPrefix.Length));
        return messages;
    }

    private IReadOnlyList<ModelMessage> SanitizeAdditionalMessages(
        IReadOnlyList<ModelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return [.. messages.Select(message => message with
        {
            Content = [.. message.Content.Select(part => part with
            {
                Content = _sanitizer.Sanitize(part.Content),
            })],
        })];
    }

    private static string RenderAdditionalMessages(IReadOnlyList<ModelMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return string.Join(
            "\n",
            messages.Select(message =>
                $"<request_local_message role=\"{message.Role}\" section=\"{Escape(message.SectionId)}\">"
                + Escape(message.GetModelVisibleContent())
                + "</request_local_message>"));
    }

    private static ModelMessage CreateTextMessage(
        ModelMessageRole role,
        string sectionId,
        string content)
    {
        return new ModelMessage
        {
            Role = role,
            SectionId = sectionId,
            Content = [new ModelContentPart { Content = content }],
        };
    }

    private string BuildModelInput(
        string appendContent,
        string phaseInstructions,
        string taskJson,
        ConversationAssemblyState conversation,
        RepositoryMemoryAssemblyState repositoryMemory,
        string governedState,
        string evidenceContent,
        string toolSchemas,
        string outputSchema,
        string additionalMessageContent)
    {
        return _prompts.Render(
            PromptFileNames.SystemLegacyRequestEnvelope,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SystemPolicy"] = $"<system_policy>{Escape(_stableSystemPolicy)}</system_policy>",
                ["RepositoryInstructions"] = PrefixLegacySection(appendContent),
                ["PhaseInstructions"] = PrefixLegacySection(
                    $"<phase_instructions>{Escape(phaseInstructions)}</phase_instructions>"),
                ["Task"] = PrefixLegacySection($"<task>{taskJson}</task>"),
                ["CurrentTurn"] = PrefixLegacySection(
                    $"<current_turn untrusted=\"true\">{Escape(conversation.CurrentTurnContent)}</current_turn>"),
                ["AdditionalMessages"] = PrefixLegacySection(additionalMessageContent),
                ["RecentTurns"] = PrefixLegacySection(conversation.RecentTurnsContent),
                ["ConversationSummary"] = PrefixLegacySection(string.Empty),
                ["RetrievedMemory"] = PrefixLegacySection(string.Empty),
                ["RepositoryMemory"] = PrefixLegacySection(repositoryMemory.Content),
                ["GovernedState"] = PrefixLegacySection(
                    $"<governed_state>{Escape(governedState)}</governed_state>"),
                ["EvidenceSet"] = PrefixLegacySection($"<evidence_set>{evidenceContent}</evidence_set>"),
                ["AvailableTools"] = string.IsNullOrWhiteSpace(toolSchemas)
                    ? string.Empty
                    : PrefixLegacySection($"<available_tools>{toolSchemas}</available_tools>"),
                ["RequiredOutput"] = PrefixLegacySection(
                    $"<required_output>{Escape(outputSchema)}</required_output>"),
            });
    }

    private static string PrefixLegacySection(string content)
    {
        return string.IsNullOrWhiteSpace(content) ? string.Empty : "\n\n" + content;
    }

    private sealed class RepositoryMemoryAssemblyState
    {
        private readonly List<(RepositoryMemoryRetrievalCandidate Candidate, int Tokens)> _included = [];
        private readonly List<RepositoryMemoryContextItemProjection> _excluded = [];
        private readonly List<(RepositoryMemoryRetrievalCandidate Candidate, int Tokens)> _standingPreferences = [];
        private readonly int _maximumTokens;
        private readonly string _guidance;
        private readonly string _standingGuidance;
        private int _includedTokens;
        private int _standingTokens;

        public RepositoryMemoryAssemblyState(int maximumTokens, string guidance, string standingGuidance)
        {
            _maximumTokens = maximumTokens;
            _guidance = guidance;
            _standingGuidance = standingGuidance;
        }

        public bool CanReduce => _included.Count > 0;

        public bool ContainsSensitiveData => _included.Concat(_standingPreferences)
            .Any(item => item.Candidate.Entry.Sensitivity == ConversationSensitivity.Sensitive);

        public string Content => _standingPreferences.Count == 0
            ? SituationalContent
            : RenderStanding(_standingPreferences.Select(item => item.Candidate))
                + (_included.Count == 0 ? string.Empty : "\n" + SituationalContent);

        public List<string> Reductions { get; } = [];

        public int RequiredTokens => _standingTokens;

        private string SituationalContent => _included.Count == 0 ? string.Empty : Render(_included.Select(item => item.Candidate));

        public void AddStandingPreference(RepositoryMemoryEntry entry)
        {
            var candidate = new RepositoryMemoryRetrievalCandidate(entry, 0, null, null, null);
            var total = TokenEstimator.Estimate(RenderStanding(_standingPreferences.Select(item => item.Candidate).Append(candidate)));
            _standingPreferences.Add((candidate, total - _standingTokens));
            _standingTokens = total;
        }

        public void AddExcluded(RepositoryMemoryRetrievalCandidate candidate, string reason, int tokens)
        {
            if (!_excluded.Any(item => item.Id == candidate.Entry.Id))
            {
                _excluded.Add(CreateProjection(candidate, false, reason, tokens));
            }
        }

        public IReadOnlyList<RepositoryMemoryInclusion> CreateInclusions() =>
            [.. _standingPreferences.Concat(_included).Select(item => new RepositoryMemoryInclusion(item.Candidate.Entry.Id, item.Candidate.Entry.Revision))];

        public IReadOnlyList<RepositoryMemoryContextItemProjection> CreateProjections() =>
            [.. _standingPreferences.Select(item => CreateProjection(item.Candidate, true, "Standing preference included independently of the current query.", item.Tokens)),
                .. _included.Select(item => CreateProjection(item.Candidate, true, "Included by qualified hybrid retrieval and context budget.", item.Tokens)), .. _excluded];

        public int EstimateAddition(RepositoryMemoryRetrievalCandidate candidate) =>
            TokenEstimator.Estimate(Render(_included.Select(item => item.Candidate).Append(candidate))) - _includedTokens;

        public bool TryAdd(RepositoryMemoryRetrievalCandidate candidate, int tokens)
        {
            if (_includedTokens + tokens > _maximumTokens)
            {
                return false;
            }

            _included.Add((candidate, tokens));
            _includedTokens += tokens;
            return true;
        }

        public bool TryReduce()
        {
            if (_included.Count == 0)
            {
                return false;
            }

            var removed = _included[^1];
            _included.RemoveAt(_included.Count - 1);
            _includedTokens -= removed.Tokens;
            const string reason = "Omitted lowest-ranked repository memory during final context reduction.";
            AddExcluded(removed.Candidate, reason, removed.Tokens);
            Reductions.Add(reason);
            return true;
        }

        private string Render(IEnumerable<RepositoryMemoryRetrievalCandidate> candidates) =>
            _guidance + "\n<repository_memory untrusted=\"true\">\n"
            + string.Join('\n', candidates.Select(candidate =>
                $"<memory id=\"{candidate.Entry.Id.Value:D}\">{Escape(candidate.Entry.Text)}</memory>"))
            + "\n</repository_memory>";

        private string RenderStanding(IEnumerable<RepositoryMemoryRetrievalCandidate> candidates) =>
            _standingGuidance + "\n<standing_preferences untrusted=\"true\">\n"
            + string.Join('\n', candidates.Select(candidate =>
                $"<memory id=\"{candidate.Entry.Id.Value:D}\">{Escape(candidate.Entry.Text)}</memory>"))
            + "\n</standing_preferences>";

        private static RepositoryMemoryContextItemProjection CreateProjection(
            RepositoryMemoryRetrievalCandidate candidate, bool included, string reason, int tokens) => new()
        {
            Id = candidate.Entry.Id,
            Origin = candidate.Entry.Origin,
            MemoryType = candidate.Entry.MemoryType,
            Revision = candidate.Entry.Revision,
            Included = included,
            Rationale = reason,
            EstimatedTokens = tokens,
            Score = candidate.Entry.MemoryType == RepositoryMemoryType.StandingPreference ? null : candidate.Score,
            LexicalRank = candidate.LexicalRank,
            SemanticRank = candidate.SemanticRank,
            CosineSimilarity = candidate.CosineSimilarity,
            CrossEncoderScore = candidate.CrossEncoderScore,
        };
    }

    private sealed class ConversationAssemblyState
    {
        private readonly List<ConversationContextItemProjection> _excluded = [];
        private readonly List<IReadOnlyList<ConversationMessage>> _recentTurns = [];

        public ConversationAssemblyState(
            ConversationContextMode mode,
            string modeSource,
            string currentTurnContent,
            bool currentTurnIsSensitive)
        {
            Mode = mode;
            ModeSource = modeSource;
            CurrentTurnContent = currentTurnContent;
            CurrentTurnIsSensitive = currentTurnIsSensitive;
        }

        public bool CanReduce => _recentTurns.Count > 0;

        public string CurrentTurnContent { get; }

        public bool ContainsSensitiveData => CurrentTurnIsSensitive
            || _recentTurns.SelectMany(turn => turn).Any(message => message.Sensitivity == ConversationSensitivity.Sensitive);

        public ConversationContextMode Mode { get; }

        public string ModeSource { get; }

        public string RecentTurnsContent => string.Join('\n', _recentTurns.SelectMany(turn => turn).Select(message =>
            $"<conversation_message id=\"{message.Id.Value:D}\" role=\"{message.Role}\" untrusted=\"true\">"
            + $"{Escape(message.Content ?? string.Empty)}</conversation_message>")) is { Length: > 0 } content
            ? $"<recent_conversation>\n{content}\n</recent_conversation>" : string.Empty;

        public List<string> Reductions { get; } = [];

        public void AddExcludedMessage(ConversationMessage message, string reason)
        {
            if (!_excluded.Any(projection => projection.Id == message.Id.Value.ToString("D")))
            {
                _excluded.Add(CreateMessageProjection(message, false, reason));
            }
        }

        public void AddRecentTurn(IReadOnlyList<ConversationMessage> turn) => _recentTurns.Add(turn);

        public IReadOnlyList<ModelMessage> CreateRecentMessages() =>
            [.. _recentTurns.SelectMany(turn => turn).Select(message => CreateTextMessage(
                message.Role == ConversationRole.User ? ModelMessageRole.User : ModelMessageRole.Assistant,
                message.Role == ConversationRole.User ? "recent-user" : "recent-assistant",
                message.Content ?? string.Empty))];

        public IReadOnlyList<ConversationContextItemProjection> CreateProjections() =>
            [.. _recentTurns.SelectMany(turn => turn).Select(message => CreateMessageProjection(
                message, true, "Included as a bounded complete recent turn.")), .. _excluded];

        public bool RemoveOldestRecentTurn(string reason)
        {
            if (_recentTurns.Count == 0)
            {
                return false;
            }

            var removed = _recentTurns[0];
            _recentTurns.RemoveAt(0);
            foreach (var message in removed)
            {
                AddExcludedMessage(message, reason);
            }

            Reductions.Add(reason);
            return true;
        }

        public bool TryReduce() => RemoveOldestRecentTurn("Omitted oldest complete turn during final context-pressure reduction.");

        private bool CurrentTurnIsSensitive { get; }

        private static ConversationContextItemProjection CreateMessageProjection(
            ConversationMessage message, bool included, string rationale) => new()
        {
            Id = message.Id.Value.ToString("D"),
            Kind = message.Role.ToString(),
            Included = included,
            Rationale = rationale,
            EstimatedTokens = message.EstimatedTokens,
            SourceMessageIds = [message.Id],
            SourceRunIds = [message.RunId],
        };
    }
}
