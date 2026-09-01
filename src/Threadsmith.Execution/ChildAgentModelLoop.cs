namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Threadsmith.Context;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>One admitted Explorer result plus measured child resource usage.</summary>
internal sealed record ChildAgentModelResult(
    AgentFindingSet Findings,
    AgentResourceUsage Usage,
    IReadOnlyList<EvidenceId> DeliveredEvidenceIds);

/// <summary>Runs bounded child model continuations and exact pipeline-fenced tool batches.</summary>
internal sealed class ChildAgentModelLoop
{
    private const int MaximumChildOutputCharacters = 128 * 1024;
    private const int MaximumCorrectionReasonCharacters = 512;
    private const int MaximumModelToolArgumentBytes = 32 * 1024;
    private const int MaximumModelToolArgumentsAggregateBytes = 96 * 1024;
    private const int MaximumModelToolNameCharacters = 256;
    private const int MaximumModelToolRequestsPerRound = 32;
    private readonly IEvidenceStore _evidence;
    private readonly IModelProvider _models;
    private readonly DelegateAgentsOptions _options;
    private readonly IReadOnlyList<ToolRegistration> _parentRegistrations;
    private readonly IOutputSanitizer _sanitizer;
    private readonly SessionUsageProjection? _sessionUsage;
    private readonly IToolInvocationPipeline _tools;

    /// <summary>Initializes a new instance of the <see cref="ChildAgentModelLoop"/> class.</summary>
    public ChildAgentModelLoop(
        IModelProvider models,
        IToolInvocationPipeline tools,
        IEvidenceStore evidence,
        IOutputSanitizer sanitizer,
        DelegateAgentsOptions options,
        IReadOnlyList<ToolRegistration> parentRegistrations,
        SessionUsageProjection? sessionUsage = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(parentRegistrations);
        _models = models;
        _tools = tools;
        _evidence = evidence;
        _sanitizer = sanitizer;
        _options = options;
        _parentRegistrations = parentRegistrations.ToArray();
        _sessionUsage = sessionUsage;
    }

    /// <summary>Runs until one valid finding set is returned or a child bound is exhausted.</summary>
    public async Task<ChildAgentModelResult> RunAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
        AgentContextSnapshot context,
        RepositoryInstructionBundle instructions,
        ToolInvocationContext childToolContext,
        AgentModelSelection model,
        CancellationToken cancellationToken)
    {
        var registrations = ResolveRegistrations(assignment);
        var toolDefinitions = ChildAgentPrompt.CreateToolDefinitions(registrations);
        var toolWireEstimate = ModelWireEstimator.EstimateTools(
            toolDefinitions,
            ToolTransportMode.Native);
        var registrationById = registrations.ToDictionary(
            registration => registration.Tool.Definition.Id,
            StringComparer.OrdinalIgnoreCase);
        var desiredOutputTokens = ResolveDesiredOutputTokens(model);
        var initialRequest = ChildAgentRequestFitter.Create(
            context,
            instructions,
            toolWireEstimate,
            model,
            desiredOutputTokens);
        var messages = initialRequest.Messages;
        var deliveredEvidenceIds = initialRequest.DeliveredEvidenceIds.ToHashSet();
        var evidenceProgress = new ChildAgentEvidenceProgressTracker(context.Evidence);
        var rejectedResponseDigests = new HashSet<string>(StringComparer.Ordinal);
        ModelWireEstimate? initialWireEstimate = initialRequest.WireEstimate;
        var ledger = new AgentBudgetLedger(assignment.Budget);
        var parser = new ChildAgentFindingParser(_options, _sanitizer);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var maximumRounds = assignment.Budget.EnforceLimits
                ? Math.Max(1, assignment.Budget.ToolCalls + assignment.Budget.Corrections + 1)
                : int.MaxValue;
            for (var round = 0; round < maximumRounds; round++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unreservedEstimate = initialWireEstimate is null
                    ? ModelWireEstimator.Estimate(
                        messages,
                        toolWireEstimate,
                        stablePrefixMessageCount: 0,
                        outputReserveTokens: 0)
                    : initialWireEstimate with { OutputReserveTokens = 0 };
                initialWireEstimate = null;
                var availableOutputTokens = model.ContextWindowTokens
                    - unreservedEstimate.WireInputTokens;
                if (availableOutputTokens <= 0)
                {
                    throw new InvalidOperationException("The child request exceeds its model context bound.");
                }

                var maximumOutputTokens = checked((int)Math.Min(
                    availableOutputTokens,
                    desiredOutputTokens));
                var wireEstimate = unreservedEstimate with
                {
                    OutputReserveTokens = maximumOutputTokens,
                };

                var response = await StreamAsync(
                    plan.Provenance.SessionId,
                    assignment,
                    model,
                    messages,
                    toolDefinitions,
                    maximumOutputTokens,
                    wireEstimate,
                    round,
                    cancellationToken);
                ledger.Charge(new AgentResourceUsage { ModelTokens = response.ModelTokens });
                if (response.ToolRequests.Count > 0)
                {
                    try
                    {
                        var continuation = await InvokeToolsAsync(
                            plan,
                            assignment,
                            response.ToolRequests,
                            childToolContext,
                            registrationById,
                            ledger,
                            evidenceProgress,
                            round,
                            cancellationToken);
                        messages.AddRange(continuation.Messages);
                        messages.Add(ChildAgentPrompt.CreateEvidenceProgressMessage(continuation.Progress));
                        deliveredEvidenceIds.UnionWith(continuation.DeliveredEvidenceIds);
                    }
                    catch (Exception exception) when (exception is InvalidDataException
                        or ToolArgumentValidationException
                        or UnauthorizedAccessException)
                    {
                        AddCorrection(messages, ledger, exception.Message);
                    }

                    continue;
                }

                try
                {
                    var findings = parser.Parse(response.Text, plan, assignment, childToolContext);
                    if (findings.Findings.Count > 0)
                    {
                        AgentFindingAdmission.Validate(
                            plan,
                            assignment,
                            findings,
                            deliveredEvidenceIds);
                    }

                    ledger.Charge(new AgentResourceUsage { EvidenceItems = findings.Findings.Count });
                    stopwatch.Stop();
                    return new ChildAgentModelResult(
                        findings,
                        ledger.Snapshot with { WallTime = stopwatch.Elapsed },
                        deliveredEvidenceIds
                            .OrderBy(item => item.Value)
                            .ToArray());
                }
                catch (Exception exception) when (exception is InvalidDataException
                    or UnauthorizedAccessException)
                {
                    var digest = Convert.ToHexStringLower(
                        SHA256.HashData(Encoding.UTF8.GetBytes(response.Text)));
                    if (!rejectedResponseDigests.Add(digest))
                    {
                        throw new InvalidDataException(
                            "The child repeated a previously rejected finding response.",
                            exception);
                    }

                    AddCorrection(messages, ledger, exception.Message);
                }
            }

            throw new InvalidOperationException("The child model-turn limit is exhausted.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            ChildAgentFailureDetails.Attach(
                exception,
                ResolveSafeFailureReason(exception),
                ledger.Snapshot with { WallTime = stopwatch.Elapsed },
                model.ProfileId);
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

    private async Task<ModelRoundResponse> StreamAsync(
        SessionId sessionId,
        AgentAssignment assignment,
        AgentModelSelection model,
        IReadOnlyList<ModelMessage> messages,
        IReadOnlyList<ModelToolDefinition> tools,
        int maximumOutputTokens,
        ModelWireEstimate wireEstimate,
        int round,
        CancellationToken cancellationToken)
    {
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
            round,
            Guid.NewGuid());
        try
        {
            await foreach (var chunk in _models.StreamAsync(
                new ModelStreamRequest
                {
                    RunId = assignment.ChildRunId,
                    Input = assignment.Objective,
                    Seed = HashCode.Combine(assignment.AssignmentId.Value, round),
                    ToolContinuationRound = round,
                    WorkloadClass = WorkloadClass.General,
                    ContainsSensitiveData = assignment.Policy.Sensitivity == ConversationSensitivity.Sensitive,
                    RequiredCapabilities = new ModelCapabilitySet
                    {
                        Streaming = true,
                        ToolCalls = tools.Count > 0,
                        StructuredOutput = true,
                    },
                    SelectionConstraints = new ModelSelectionConstraints
                    {
                        MinimumContextWindow = checked((int)Math.Min(
                            wireEstimate.TotalCapacityTokens,
                            int.MaxValue)),
                        ContainsSensitiveData = assignment.Policy.Sensitivity
                            == ConversationSensitivity.Sensitive,
                    },
                    ResolvedProfileId = model.ProfileId,
                    MaximumOutputTokens = maximumOutputTokens,
                    ReasoningLevel = model.ReasoningLevel,
                    Tools = tools,
                    AllowMultipleToolCalls = true,
                    Messages = messages,
                    WireEstimate = wireEstimate,
                },
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
                        var maximumToolRequests = assignment.Budget.EnforceLimits
                            ? Math.Min(MaximumModelToolRequestsPerRound, assignment.Budget.ToolCalls)
                            : MaximumModelToolRequestsPerRound;
                        if (toolRequests.Count >= maximumToolRequests
                            || string.IsNullOrWhiteSpace(toolRequest.ToolName)
                            || toolRequest.ToolName.Length > MaximumModelToolNameCharacters)
                        {
                            throw new InvalidDataException(
                                "The child response exceeds its tool-request count or name bound.");
                        }

                        var argumentBytes = Encoding.UTF8.GetByteCount(toolRequest.ArgumentsJson);
                        toolArgumentBytes = checked(toolArgumentBytes + argumentBytes);
                        if (argumentBytes > MaximumModelToolArgumentBytes
                            || toolArgumentBytes > MaximumModelToolArgumentsAggregateBytes)
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
                            "The Explorer returned an unsupported structured output type.");
                }

                if (chunk.Usage is not null)
                {
                    usage = chunk.Usage;
                    _sessionUsage?.Observe(sessionId, usageRequestId, chunk.Usage);
                }

                if (checked(text.Length + reasoningCharacters) > MaximumChildOutputCharacters)
                {
                    throw new InvalidDataException("The child response exceeds its output bound.");
                }

                var estimatedOutputTokens = checked(
                    EstimateCharacterTokens(text.Length)
                    + reasoningTokens
                    + toolRequestTokens);
                if (estimatedOutputTokens > maximumOutputTokens)
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

        return new ModelRoundResponse(responseText.Trim(), toolRequests, modelTokens);
    }

    private static int ResolveDesiredOutputTokens(AgentModelSelection model)
    {
        return checked((int)Math.Min(
            model.MaximumOutputTokens,
            model.OutputReserveTokens));
    }

    private static int EstimateCharacterTokens(int characters)
    {
        return characters == 0 ? 0 : checked((characters + 3) / 4);
    }

    private async Task<ToolContinuation> InvokeToolsAsync(
        DelegationPlan plan,
        AgentAssignment assignment,
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
                        Context = childContext,
                    });
            }),
        ];
        var preflight = _tools.PreflightBatch(batch);
        if (!preflight.Succeeded || preflight.Preparation is null)
        {
            throw new InvalidDataException(
                preflight.SafeReason ?? "The child tool batch failed host preflight.");
        }

        var results = await _tools.InvokePreparedBatchAsync(preflight.Preparation, cancellationToken);
        var coverageBefore = evidenceProgress.Capture();
        var messages = new List<ModelMessage>(results.Count * 2);
        var deliveredEvidenceIds = new List<EvidenceId>(results.Count);
        foreach (var result in results.OrderBy(result => result.Ordinal))
        {
            var request = requests[result.Ordinal];
            messages.Add(ChildAgentPrompt.CreateToolCallMessage(result.CorrelationId, request));
            var evidence = await StoreToolEvidenceAsync(
                plan,
                assignment,
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
        ToolInvocationResult result,
        AgentBudgetLedger ledger,
        ChildAgentEvidenceProgressTracker evidenceProgress,
        CancellationToken cancellationToken)
    {
        var content = _sanitizer.Sanitize(
            result.ModelResultContent
                ?? result.ResultJson
                ?? result.Error
                ?? "Tool completed without model-visible content.");
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
                    ModelProfileId = assignment.Policy.ModelProfileId == default
                        ? null
                        : assignment.Policy.ModelProfileId,
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
            content,
            truncated = result.IsTruncated,
        });
        return new StoredToolEvidence(evidenceId, modelContent);
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
        string reason)
    {
        ledger.Charge(new AgentResourceUsage { Corrections = 1 });
        var sanitized = BoundedText.Truncate(
            _sanitizer.Sanitize(reason),
            MaximumCorrectionReasonCharacters,
            out _);
        messages.Add(ChildAgentPrompt.CreateCorrectionMessage(sanitized));
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
}

/// <summary>Preserves measured child failure details without changing the original exception contract.</summary>
internal sealed record ChildAgentFailureDetails(
    string SafeReason,
    AgentResourceUsage Usage,
    ModelProfileId ModelProfileId)
{
    private const string ExceptionDataKey = "Threadsmith.Execution.ChildAgentFailureDetails";

    /// <summary>Attaches child failure details to the original exception.</summary>
    public static void Attach(
        Exception exception,
        string safeReason,
        AgentResourceUsage usage,
        ModelProfileId modelProfileId)
    {
        exception.Data[ExceptionDataKey] = new ChildAgentFailureDetails(
            safeReason,
            usage,
            modelProfileId);
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
