namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>One child response plus independently collected usage, evidence, and model metadata.</summary>
internal sealed record ChildAgentModelResult(
    string Response,
    AgentResourceUsage Usage,
    IReadOnlyList<EvidenceId> DeliveredEvidenceIds,
    AgentModelSelection Model);

/// <summary>Runs bounded child model continuations and exact pipeline-fenced tool batches.</summary>
internal sealed class ChildAgentModelLoop
{
    private readonly DelegateAgentsOptions _options;
    private readonly IEvidenceStore _evidence;
    private readonly IModelProvider _models;
    private readonly IModelProvider? _trustedModels;
    private readonly AgentModelSelector? _selection;
    private readonly IReadOnlyList<ToolRegistration> _parentRegistrations;
    private readonly IOutputSanitizer _sanitizer;
    private readonly IPromptLoader _prompts;
    private readonly SessionUsageProjection? _sessionUsage;
    private readonly RunSteeringCoordinator? _steering;
    private readonly IToolInvocationPipeline _tools;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentModelLoop"/> class.</summary>
    public ChildAgentModelLoop(
        IModelProvider models,
        IToolInvocationPipeline tools,
        IEvidenceStore evidence,
        IOutputSanitizer sanitizer,
        DelegateAgentsOptions options,
        IReadOnlyList<ToolRegistration> parentRegistrations,
        IPromptLoader prompts,
        SessionUsageProjection? sessionUsage = null,
        RunSteeringCoordinator? steering = null,
        AgentModelSelector? selection = null,
        IModelProvider? trustedModels = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parentRegistrations);
        ArgumentNullException.ThrowIfNull(prompts);
        _models = models;
        _tools = tools;
        _evidence = evidence;
        _sanitizer = sanitizer;
        _prompts = prompts;
        _parentRegistrations = parentRegistrations.ToArray();
        _sessionUsage = sessionUsage;
        _steering = steering;
        _selection = selection;
        _trustedModels = trustedModels;
        _options = options;
    }

    /// <summary>Runs advertised tools until the model returns its response, without imposing an answer format.</summary>
    public async Task<ChildAgentModelResult> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        AgentContextSnapshot context,
        RepositoryInstructionBundle instructions,
        ToolInvocationContext childToolContext,
        AgentModelSelection model,
        CancellationToken cancellationToken)
    {
        var deliveredEvidenceIds = context.Evidence.Select(item => item.EvidenceId).ToHashSet();
        var registrations = ResolveRegistrations(assignment).ToList();
        if (!childToolContext.DenyAllTools
            && !childToolContext.DeniedToolIds.Contains(ChildAgentEvidenceTool.ToolId, StringComparer.OrdinalIgnoreCase))
        {
            registrations.Add(new ToolRegistration(
                new ChildAgentEvidenceTool(_evidence, plan.Provenance.SessionId, assignment.ChildRunId, deliveredEvidenceIds, _prompts),
                new ToolActivitySource(ToolActivitySourceKind.BuiltIn, "child-evidence")));
        }

        var toolDefinitions = ModelToolCanonicalizer.Canonicalize(
            ChildAgentPrompt.CreateToolDefinitions(registrations));
        var toolWireEstimate = ModelWireEstimator.EstimateTools(
            toolDefinitions,
            ToolTransportMode.Native);
        var registrationById = registrations.ToDictionary(
            registration => registration.Tool.Definition.Id,
            StringComparer.OrdinalIgnoreCase);
        var prompt = new ChildAgentPrompt(_prompts, assignment.Role);
        var messages = prompt.CreateMessages(context, instructions);
        var history = new ChildAgentHistory(messages, _options.Compaction, _prompts);
        var evidenceProgress = new ChildAgentEvidenceProgressTracker(context.Evidence);
        var ledger = new AgentBudgetLedger(assignment.Budget);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var maximumRounds = assignment.Budget.EnforceLimits
                ? Math.Max(1, assignment.Budget.ToolCalls + assignment.Budget.Corrections + 1)
                : int.MaxValue;
            for (var round = 0; round < maximumRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_steering is not null)
                {
                    var steering = await _steering.PauseChildAtBoundaryAsync(
                        plan.Provenance.SessionId,
                        plan.Provenance.ParentRunId,
                        assignment.ChildRunId,
                        cancellationToken);
                    if (steering.Count > 0)
                    {
                        messages.AddRange(steering.Select(prompt.CreateSteeringMessage));
                    }
                }

                var summaryPolicy = _options.Compaction.Summary;
                var provider = model.UsesTrustedCatalog ? _trustedModels ?? _models : _models;
                var compactor = new ActiveTurnCompactor(
                    new ModelActiveTurnCompactionCandidateProvider(provider, summaryPolicy, _prompts),
                    new ActiveTurnCompactionValidator(summaryPolicy, _sanitizer, _prompts),
                    summaryPolicy,
                    _prompts);
                await history.CompactAsync(
                    assignment,
                    model,
                    toolWireEstimate,
                    round,
                    compactor,
                    new ChildCompactionObserver(plan.Provenance.SessionId, _sessionUsage, ledger),
                    cancellationToken);
                var fitted = FitRequest(assignment, model, messages, toolDefinitions, toolWireEstimate, round, history.RewriteGeneration);
                model = fitted.Model;
                childToolContext = childToolContext with
                {
                    ModelContextWindowTokens = model.ContextWindowTokens,
                    ModelRequestOutputReserveTokens = model.OutputReserveTokens,
                    ModelEffectiveInputBudgetTokens = model.ContextWindowTokens - model.OutputReserveTokens,
                };

                var response = await StreamAsync(
                    plan.Provenance.SessionId,
                    assignment,
                    model,
                    fitted.Request,
                    cancellationToken);
                ledger.Charge(new AgentResourceUsage { ModelTokens = response.ModelTokens });
                cancellationToken.ThrowIfCancellationRequested();
                history.MarkDelivered();
                if (response.ToolRequests.Count > 0)
                {
                    if (_steering is not null)
                    {
                        var steering = await _steering.PauseChildAtBoundaryAsync(
                            plan.Provenance.SessionId,
                            plan.Provenance.ParentRunId,
                            assignment.ChildRunId,
                            cancellationToken);
                        if (steering.Count > 0)
                        {
                            messages.AddRange(steering.Select(prompt.CreateSteeringMessage));
                            continue;
                        }
                    }

                    var exchangeStart = messages.Count;
                    IReadOnlyList<EvidenceId> exchangeEvidence = [];
                    try
                    {
                        var continuation = await InvokeToolsAsync(
                            plan,
                            assignment,
                            model.ProfileId,
                            response.ToolRequests,
                            childToolContext,
                            registrationById,
                            ledger,
                            evidenceProgress,
                            round,
                            cancellationToken);
                        messages.AddRange(continuation.Messages);
                        messages.Add(prompt.CreateEvidenceProgressMessage(continuation.Progress));
                        deliveredEvidenceIds.UnionWith(continuation.DeliveredEvidenceIds);
                        exchangeEvidence = continuation.DeliveredEvidenceIds;
                    }
                    catch (Exception exception) when (exception is InvalidDataException
                        or ToolArgumentValidationException
                        or UnauthorizedAccessException)
                    {
                        AddCorrection(
                            messages,
                            ledger,
                            exception.Message,
                            prompt,
                            response.ToolRequests,
                            registrationById,
                            assignment,
                            round);
                    }

                    history.RecordExchange(exchangeStart, round, exchangeEvidence);

                    continue;
                }

                stopwatch.Stop();
                return new ChildAgentModelResult(
                    _sanitizer.Sanitize(response.Text),
                    ledger.Snapshot with { WallTime = stopwatch.Elapsed },
                    deliveredEvidenceIds.OrderBy(item => item.Value).ToArray(),
                    model);
            }

            throw new InvalidOperationException("The child model-turn limit is exhausted.");
        }
        catch (OperationCanceledException exception)
        {
            ChildAgentFailureDetails.Attach(
                exception,
                "child cancellation observed",
                ledger.Snapshot with { WallTime = stopwatch.Elapsed },
                model.ProfileId,
                model.Provenance);
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            ChildAgentFailureDetails.Attach(
                exception,
                ResolveSafeFailureReason(exception),
                ledger.Snapshot with { WallTime = stopwatch.Elapsed },
                model.ProfileId,
                model.Provenance);
            throw;
        }
    }

    private static string ResolveSafeFailureReason(Exception exception)
    {
        return exception switch
        {
            InvalidDataException => exception.Message,
            ToolArgumentValidationException => exception.Message,
            UnauthorizedAccessException => exception.Message,
            InvalidOperationException when exception.Message.StartsWith(
                "The child ",
                StringComparison.Ordinal) => exception.Message,
            _ => $"{exception.GetType().Name}: child execution failed",
        };
    }

    private (AgentModelSelection Model, ModelStreamRequest Request) FitRequest(
        AgentAssignment assignment,
        AgentModelSelection model,
        IReadOnlyList<ModelMessage> messages,
        IReadOnlyList<ModelToolDefinition> tools,
        ModelWireToolEstimate toolEstimate,
        int round,
        long historyRewriteGeneration)
    {
        var attempted = new HashSet<ModelProfileId>();
        while (attempted.Add(model.ProfileId))
        {
            var outputTokens = ResolveDesiredOutputTokens(model);
            var estimate = ModelWireEstimator.Estimate(
                messages,
                toolEstimate,
                stablePrefixMessageCount: 0,
                outputReserveTokens: outputTokens,
                model.ProviderInstructions);
            var request = new ModelStreamRequest
            {
                RunId = assignment.ChildRunId,
                Input = assignment.Objective,
                Seed = HashCode.Combine(assignment.AssignmentId.Value, round),
                ToolContinuationRound = round,
                HistoryRewriteGeneration = historyRewriteGeneration,
                WorkloadClass = assignment.Role switch
                {
                    AgentRole.Explorer => WorkloadClass.General,
                    AgentRole.Implementer => WorkloadClass.CodeEdit,
                    _ => WorkloadClass.Review,
                },
                ContainsSensitiveData = assignment.Policy.Sensitivity == ConversationSensitivity.Sensitive,
                RequiredCapabilities = new ModelCapabilitySet
                {
                    Streaming = true,
                    ToolCalls = tools.Count > 0,
                    StructuredOutput = false,
                },
                SelectionConstraints = new ModelSelectionConstraints
                {
                    ContainsSensitiveData = assignment.Policy.Sensitivity == ConversationSensitivity.Sensitive,
                },
                ResolvedProfileId = model.ProfileId,
                MaximumOutputTokens = outputTokens,
                ReasoningLevel = model.ReasoningLevel,
                Tools = tools,
                AllowMultipleToolCalls = true,
                Messages = messages.ToArray(),
                WireEstimate = estimate,
                ProviderInstructions = model.ProviderInstructions,
            };
            var effectiveAssignment = assignment with
            {
                Policy = assignment.Policy with { ModelSelection = model.Provenance },
            };
            var selected = _selection?.SelectForRequest(effectiveAssignment, request, useProfileOutputReserve: true) ?? model;
            if (selected.ProfileId == model.ProfileId)
            {
                if (estimate.TotalCapacityTokens > selected.ContextWindowTokens)
                {
                    throw new InvalidOperationException("The complete child context exceeds the selected model context window.");
                }

                return (selected, request);
            }

            model = selected;
        }

        throw new InvalidOperationException("The child model fallback repeated without finding a compatible request.");
    }

    private async Task<ModelRoundResponse> StreamAsync(
        SessionId sessionId,
        AgentAssignment assignment,
        AgentModelSelection model,
        ModelStreamRequest request,
        CancellationToken cancellationToken)
    {
        var maximumOutputTokens = model.MaximumOutputTokens;
        var wireEstimate = request.WireEstimate
            ?? throw new InvalidOperationException("The child request has no capacity estimate.");
        var provider = model.UsesTrustedCatalog
            ? _trustedModels ?? throw new InvalidOperationException("The trusted child model provider is unavailable.")
            : _models;
        var text = new StringBuilder();
        var toolRequests = new List<ToolRequestModelOutput>();
        var toolArgumentBytes = 0;
        var reasoningCharacters = 0;
        var reasoningTokens = 0;
        var toolRequestTokens = 0;
        ModelUsage? usage = null;
        var usageRequestId = new ModelRequestUsageId(
            assignment.ChildRunId,
            "delegate-agent",
            request.ToolContinuationRound,
            Guid.NewGuid());
        try
        {
            await foreach (var chunk in provider.StreamAsync(
                request,
                cancellationToken))
            {
                if (chunk.Text is { } delta)
                {
                    text.Append(delta);
                }

                if (chunk.Reasoning is { } reasoning)
                {
                    reasoningCharacters = checked(reasoningCharacters + reasoning.Length);
                    reasoningTokens = EstimateCharacterTokens(reasoningCharacters);
                }

                switch (chunk.Output)
                {
                    case ToolRequestModelOutput toolRequest:
                        if (ExceedsLimit(toolRequests.Count + 1, _options.MaximumToolRequestsPerRound)
                            || string.IsNullOrWhiteSpace(toolRequest.ToolName)
                            || ExceedsLimit(toolRequest.ToolName.Length, _options.MaximumToolNameCharacters))
                        {
                            throw new InvalidDataException(
                                "The child response exceeds its tool-request count or name bound.");
                        }

                        var argumentBytes = Encoding.UTF8.GetByteCount(toolRequest.ArgumentsJson);
                        toolArgumentBytes = checked(toolArgumentBytes + argumentBytes);
                        if (ExceedsLimit(argumentBytes, _options.MaximumToolArgumentBytes)
                            || ExceedsLimit(toolArgumentBytes, _options.MaximumToolArgumentsAggregateBytes))
                        {
                            throw new InvalidDataException(
                                "The child response exceeds its tool-argument payload bound.");
                        }

                        toolRequests.Add(toolRequest);
                        toolRequestTokens = checked(
                            toolRequestTokens
                            + TokenEstimator.Estimate(toolRequest.ToolName)
                            + TokenEstimator.Estimate(toolRequest.ArgumentsJson));
                        break;
                    case TextModelOutput textOutput when chunk.Text is null:
                        text.Append(textOutput.Text);
                        break;
                    case null:
                    case TextModelOutput:
                        break;
                    default:
                        throw new InvalidDataException(
                            "The child returned an unsupported structured output type.");
                }

                if (chunk.Usage is not null)
                {
                    usage = chunk.Usage;
                    _sessionUsage?.Observe(sessionId, usageRequestId, chunk.Usage);
                }

                if (ExceedsLimit(checked(text.Length + reasoningCharacters), _options.MaximumChildOutputCharacters))
                {
                    throw new InvalidDataException("The child response exceeds its output bound.");
                }

                // Estimates support accounting but cannot prove the provider exceeded its output capacity.
                if (usage is { IsEstimate: false } && usage.OutputTokens > maximumOutputTokens)
                {
                    throw new InvalidDataException("The child response exceeds its output token bound.");
                }
            }
        }
        finally
        {
            if (usage is null)
            {
                _sessionUsage?.ObserveMissing(sessionId, usageRequestId);
            }
        }

        var responseText = text.ToString();
        var hostOutputTokens = checked(
            TokenEstimator.Estimate(responseText)
            + reasoningTokens
            + toolRequestTokens);
        var hostModelTokens = checked((long)wireEstimate.WireInputTokens + hostOutputTokens);
        var providerModelTokens = usage is null
            ? 0
            : checked(usage.InputTokens + usage.OutputTokens);
        var modelTokens = Math.Max(hostModelTokens, providerModelTokens);
        if (modelTokens < 0)
        {
            throw new InvalidDataException("The child provider returned invalid usage.");
        }

        return new ModelRoundResponse(responseText, toolRequests, modelTokens);
    }

    private static int ResolveDesiredOutputTokens(AgentModelSelection model)
    {
        return checked((int)Math.Min(
            model.MaximumOutputTokens,
            model.OutputReserveTokens));
    }

    private bool ExceedsLimit(int actual, int configured)
    {
        var limit = _options.EffectiveLimit(configured);
        return limit > 0 && actual > limit;
    }

    private static int EstimateCharacterTokens(int characters)
    {
        return characters == 0 ? 0 : checked((characters + 3) / 4);
    }

    private async Task<ToolContinuation> InvokeToolsAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        ModelProfileId modelProfileId,
        IReadOnlyList<ToolRequestModelOutput> requests,
        ToolInvocationContext childContext,
        IReadOnlyDictionary<string, ToolRegistration> registrations,
        AgentBudgetLedger ledger,
        ChildAgentEvidenceProgressTracker evidenceProgress,
        int round,
        CancellationToken cancellationToken)
    {
        ToolRegistration[] resolvedRegistrations =
        [
            .. requests.Select(request => ResolveRegistration(request, registrations)),
        ];
        var processCalls = resolvedRegistrations.Count(registration =>
            registration.Tool.Definition.Category
                is ToolCategory.ProcessExecution or ToolCategory.CodeExecution);
        ledger.Charge(new AgentResourceUsage
        {
            ToolCalls = requests.Count,
            Processes = processCalls,
        });
        ToolBatchRequest[] batch =
        [
            .. requests.Select((request, ordinal) =>
            {
                var registration = resolvedRegistrations[ordinal];
                return new ToolBatchRequest(
                    ordinal,
                    CreateToolCallId(assignment, round, ordinal),
                    new ToolInvocationRequest
                    {
                        ExpectedRegistration = registration,
                        SessionId = plan.Provenance.SessionId,
                        RunId = assignment.ChildRunId,
                        Phase = RunPhase.EvidenceCollection,
                        ToolId = request.ToolName,
                        ArgumentsJson = request.ArgumentsJson,
                        Context = registration.Tool is ChildAgentEvidenceTool
                            ? childContext with { AllowedToolIds = [ChildAgentEvidenceTool.ToolId] }
                            : childContext,
                    });
            }),
        ];
        var reads = batch.Where(item => item.Invocation.ExpectedRegistration?.Tool is ChildAgentEvidenceTool).ToArray();
        foreach (var read in reads)
        {
            registrations[ChildAgentEvidenceTool.ToolId].Tool.DeserializeInput(read.Invocation.ArgumentsJson);
        }

        var ordinary = batch.Except(reads).ToArray();
        var preflight = ordinary.Length > 0 ? _tools.PreflightBatch(ordinary) : null;
        if (preflight is not null && (!preflight.Succeeded || preflight.Preparation is null))
        {
            throw new InvalidDataException(
                $"The tool batch was not executed. Call {preflight.FailedOrdinal + 1} "
                + $"({preflight.FailedToolId}) failed validation: "
                + (preflight.SafeReason ?? "Tool arguments could not be validated.")
                + " Other calls in this batch were not executed; this does not mean their paths or arguments were invalid.");
        }

        var results = new List<ToolBatchResult>();
        if (preflight?.Preparation is { } preparation)
        {
            results.AddRange(await _tools.InvokePreparedBatchAsync(preparation, cancellationToken));
        }

        foreach (var read in reads)
        {
            var result = await _tools.InvokeAsync(read.Invocation, cancellationToken);
            results.Add(new ToolBatchResult(read.Ordinal, read.CorrelationId, result));
        }

        var coverageBefore = evidenceProgress.Capture();
        var messages = new List<ModelMessage>(results.Count * 2);
        var deliveredEvidenceIds = new List<EvidenceId>(results.Count);
        foreach (var result in results.OrderBy(result => result.Ordinal))
        {
            var request = requests[result.Ordinal];
            messages.Add(ChildAgentPrompt.CreateToolCallMessage(result.CorrelationId, request));
            var evidence = result.Result.Succeeded && result.Result.ToolId == ChildAgentEvidenceTool.ToolId
                ? new StoredToolEvidence(
                    new EvidenceId(((ChildAgentEvidenceInput)registrations[ChildAgentEvidenceTool.ToolId].Tool.DeserializeInput(request.ArgumentsJson)).EvidenceId),
                    result.Result.ModelResultContent ?? result.Result.ResultJson ?? string.Empty)
                : await StoreToolEvidenceAsync(
                plan,
                assignment,
                modelProfileId,
                result.Result,
                ledger,
                evidenceProgress,
                cancellationToken);
            deliveredEvidenceIds.Add(evidence.EvidenceId);
            messages.Add(ChildAgentPrompt.CreateToolResultMessage(
                result.CorrelationId,
                result.Result.ToolId,
                evidence.Content));
        }

        return new ToolContinuation(
            messages,
            deliveredEvidenceIds,
            evidenceProgress.Measure(coverageBefore));
    }

    private async Task<StoredToolEvidence> StoreToolEvidenceAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        ModelProfileId modelProfileId,
        ToolInvocationResult result,
        AgentBudgetLedger ledger,
        ChildAgentEvidenceProgressTracker evidenceProgress,
        CancellationToken cancellationToken)
    {
        var content = JsonOutputSanitizer.SanitizeJsonOrText(
            result.ModelResultContent
                ?? result.ResultJson
                ?? result.Error
                ?? _prompts.Get(PromptFileNames.ToolChildAgentToolInvocationCompleted),
            _sanitizer);
        var bytes = Encoding.UTF8.GetByteCount(content);
        evidenceProgress.Observe(result, content);
        var files = result.Sources
            .Where(source => string.Equals(source.Kind, "file", StringComparison.OrdinalIgnoreCase))
            .Select(source => source.Identifier)
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .Count();
        ledger.Charge(new AgentResourceUsage
        {
            EvidenceItems = 1,
            Files = files,
            Bytes = bytes,
        });
        var evidenceId = EvidenceId.New();
        var source = result.Sources.FirstOrDefault();
        await _evidence.AddAsync(
            new Evidence
            {
                EvidenceId = evidenceId,
                SessionId = plan.Provenance.SessionId,
                RunId = assignment.ChildRunId,
                Kind = result.Succeeded ? EvidenceKind.ToolResult : EvidenceKind.Failure,
                Content = content,
                Provenance = new EvidenceProvenance
                {
                    Source = $"tool:{result.ToolId}",
                    SourcePath = source?.Identifier,
                    ToolInvocationId = result.ToolInvocationId,
                    ChildRunId = assignment.ChildRunId,
                    AgentAssignmentId = assignment.AssignmentId,
                    ModelProfileId = modelProfileId,
                    BaselineIdentity = plan.Provenance.BaselineIdentity,
                },
                CollectedAt = DateTimeOffset.UtcNow,
                Relevance = result.Succeeded ? 0.8 : 1,
                EstimatedTokens = Math.Max(1, TokenEstimator.Estimate(content)),
                Sensitivity = assignment.Policy.Sensitivity == ConversationSensitivity.Sensitive
                    ? EvidenceSensitivity.Sensitive
                    : EvidenceSensitivity.None,
                InvalidationKeys = ["repository"],
            },
            cancellationToken);
        var modelContent = JsonSerializer.Serialize(new
        {
            evidenceId = evidenceId.Value.ToString("D"),
            succeeded = result.Succeeded,
            content = CreateModelVisibleToolContent(content),
            truncated = result.IsTruncated,
        });
        return new StoredToolEvidence(evidenceId, modelContent);
    }

    private static object CreateModelVisibleToolContent(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return content;
        }
    }

    private IReadOnlyList<ToolRegistration> ResolveRegistrations(AgentAssignment assignment)
    {
        var available = _parentRegistrations.ToDictionary(
            registration => registration.Tool.Definition.Id,
            StringComparer.OrdinalIgnoreCase);
        return assignment.Policy.AllowedToolIds.Select(toolId =>
        {
            if (!available.TryGetValue(toolId, out var registration)
                || registration.Tool.Definition.Category == ToolCategory.Workflow
                || string.Equals(
                    registration.Tool.Definition.Id,
                    DelegateAgentsContract.ToolId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException(
                    "The child tool policy is not backed by the exact parent registration snapshot.");
            }

            return registration;
        }).ToArray();
    }

    private static ToolRegistration ResolveRegistration(
        ToolRequestModelOutput request,
        IReadOnlyDictionary<string, ToolRegistration> registrations)
    {
        return registrations.TryGetValue(request.ToolName, out var registration)
            ? registration
            : throw new UnauthorizedAccessException("The child requested an unavailable tool.");
    }

    private void AddCorrection(
        ICollection<ModelMessage> messages,
        AgentBudgetLedger ledger,
        string reason,
        ChildAgentPrompt prompt,
        IReadOnlyList<ToolRequestModelOutput> requests,
        IReadOnlyDictionary<string, ToolRegistration> registrations,
        AgentAssignment assignment,
        int round)
    {
        ledger.Charge(new AgentResourceUsage { Corrections = 1 });
        var sanitized = BoundedText.Truncate(
            _sanitizer.Sanitize(reason),
            _options.EffectiveLimit(_options.MaximumCorrectionReasonCharacters) is > 0 and var limit ? limit : int.MaxValue,
            out _);
        var error = JsonSerializer.Serialize(new { succeeded = false, executed = false, error = sanitized });
        for (var ordinal = 0; ordinal < requests.Count; ordinal++)
        {
            var request = requests[ordinal];
            if (!registrations.ContainsKey(request.ToolName))
            {
                continue;
            }

            var correlationId = CreateToolCallId(assignment, round, ordinal);
            messages.Add(ChildAgentPrompt.CreateToolCallMessage(correlationId, request));
            messages.Add(ChildAgentPrompt.CreateToolResultMessage(correlationId, request.ToolName, error));
        }

        messages.Add(prompt.CreateCorrectionMessage(sanitized));
    }

    private static string CreateToolCallId(
        AgentAssignment assignment,
        int round,
        int ordinal)
    {
        return $"agent-{assignment.AssignmentId.Value:N}-{round}-{ordinal}";
    }

    private sealed record ModelRoundResponse(
        string Text,
        IReadOnlyList<ToolRequestModelOutput> ToolRequests,
        long ModelTokens);

    private sealed record StoredToolEvidence(EvidenceId EvidenceId, string Content);

    private sealed record ToolContinuation(
        IReadOnlyList<ModelMessage> Messages,
        IReadOnlyList<EvidenceId> DeliveredEvidenceIds,
        ChildAgentEvidenceProgress Progress);

    private sealed class ChildCompactionObserver : IActiveTurnCompactionAttemptObserver
    {
        private readonly SessionId _sessionId;
        private readonly SessionUsageProjection? _usage;
        private readonly AgentBudgetLedger _ledger;

        public ChildCompactionObserver(SessionId sessionId, SessionUsageProjection? usage, AgentBudgetLedger ledger)
        {
            _sessionId = sessionId;
            _usage = usage;
            _ledger = ledger;
        }

        public Task BeforeProviderCallAsync(
            ActiveTurnCompactionRequest request,
            int attempt,
            Guid invocationId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public Task AfterProviderCallAsync(
            ActiveTurnCompactionRequest request,
            int attempt,
            Guid invocationId,
            ActiveTurnCompactionAttemptOutcome outcome,
            ModelUsage? usage,
            TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            var id = new ModelRequestUsageId(request.RunId, "child-compaction", request.ToolContinuationRound, invocationId);
            if (usage is null)
            {
                _usage?.ObserveMissing(_sessionId, id);
            }
            else
            {
                _usage?.Observe(_sessionId, id, usage);
                _ledger.Charge(new AgentResourceUsage { ModelTokens = usage.InputTokens + usage.OutputTokens });
            }

            return Task.CompletedTask;
        }
    }
}

/// <summary>Preserves measured child failure details without changing the original exception contract.</summary>
internal sealed record ChildAgentFailureDetails(
    string SafeReason,
    AgentResourceUsage Usage,
    ModelProfileId ModelProfileId,
    AgentModelProvenance? ModelSelection)
{
    private const string ExceptionDataKey = "Threadsmith.Execution.ChildAgentFailureDetails";

    /// <summary>Attaches child failure details to the original exception.</summary>
    public static void Attach(
        Exception exception,
        string safeReason,
        AgentResourceUsage usage,
        ModelProfileId modelProfileId,
        AgentModelProvenance? modelSelection = null)
    {
        exception.Data[ExceptionDataKey] = new ChildAgentFailureDetails(
            safeReason,
            usage,
            modelProfileId,
            modelSelection);
    }

    /// <summary>Attempts to read attached child failure details.</summary>
    public static bool TryGet(
        Exception exception,
        [NotNullWhen(true)] out ChildAgentFailureDetails? details)
    {
        details = exception.Data[ExceptionDataKey] as ChildAgentFailureDetails;
        return details is not null;
    }
}
