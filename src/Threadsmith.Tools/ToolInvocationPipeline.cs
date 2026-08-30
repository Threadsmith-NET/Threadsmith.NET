namespace Threadsmith.Tools;

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Validates, authorizes, executes, bounds, and records every tool request.</summary>
public interface IToolInvocationPipeline
{
    /// <summary>Invokes a registered tool through the complete host policy pipeline.</summary>
    Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Validates a complete sibling set without publishing events, requesting approval, or executing tools.</summary>
    ToolBatchPreflightResult PreflightBatch(IReadOnlyList<ToolBatchRequest> requests);

    /// <summary>Executes a preflight-prepared sibling set against the validated registration snapshot.</summary>
    Task<IReadOnlyList<ToolBatchResult>> InvokePreparedBatchAsync(
        ToolBatchPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        return InvokeBatchAsync(preparation.Requests, cancellationToken);
    }

    /// <summary>Executes a complete sibling set in deterministic conflict-free waves.</summary>
    async Task<IReadOnlyList<ToolBatchResult>> InvokeBatchAsync(
        IReadOnlyList<ToolBatchRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var results = new List<ToolBatchResult>(requests.Count);
        foreach (var request in requests.OrderBy(item => item.Ordinal))
        {
            var result = await InvokeAsync(request.Invocation, cancellationToken);
            results.Add(new ToolBatchResult(request.Ordinal, request.CorrelationId, result));
        }

        return results;
    }
}

/// <summary>Default centralized tool invocation pipeline.</summary>
public sealed class ToolInvocationPipeline : IToolInvocationPipeline
{
    private const int MaximumActivityDetailCharacters = 240;
    private const int MaximumPreflightReasonCharacters = 512;
    private static readonly ActivitySource _activitySource = new("Threadsmith.Tools");
    private static readonly Meter _meter = new("Threadsmith.Tools");
    private static readonly Histogram<double> _latency = _meter.CreateHistogram<double>(
        "threadsmith.tool.duration",
        "ms");

    private static readonly Counter<long> _rejections = _meter.CreateCounter<long>(
        "threadsmith.tool.rejections");

    private readonly IApprovalPolicy _approvalPolicy;
    private readonly IBudget? _budget;
    private readonly IDomainEventStream _events;
    private readonly ILogger<ToolInvocationPipeline> _logger;
    private readonly IHookCoordinator? _hooks;
    private readonly IPolicyEngine _policy;
    private readonly IToolRegistry _registry;
    private readonly IOutputSanitizer _sanitizer;
    private readonly TimeProvider _timeProvider;
    private readonly ToolParallelOptions _parallelOptions;
    private readonly ToolSourceConcurrencyLimiter _sourceConcurrencyLimiter = new();

    /// <summary>Gets the registry used to resolve and identity-fence dynamic registrations.</summary>
    public IToolRegistry Registry => _registry;

    /// <summary>Initializes a new instance of the <see cref="ToolInvocationPipeline"/> class.</summary>
    public ToolInvocationPipeline(
        IToolRegistry registry,
        IPolicyEngine policy,
        IApprovalPolicy approvalPolicy,
        IDomainEventStream events,
        IOutputSanitizer sanitizer,
        ILogger<ToolInvocationPipeline> logger,
        IBudget? budget = null,
        TimeProvider? timeProvider = null,
        IHookCoordinator? hooks = null,
        ToolParallelOptions? parallelOptions = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(approvalPolicy);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _policy = policy;
        _approvalPolicy = approvalPolicy;
        _events = events;
        _sanitizer = sanitizer;
        _logger = logger;
        _budget = budget;
        _hooks = hooks;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _parallelOptions = parallelOptions ?? ToolParallelOptions.Default;
    }

    /// <inheritdoc />
    public ToolBatchPreflightResult PreflightBatch(IReadOnlyList<ToolBatchRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return ToolBatchPreflightResult.Success;
        }

        var duplicate = requests
            .GroupBy(request => request.CorrelationId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.OrderBy(request => request.Ordinal)
            .Skip(1)
            .FirstOrDefault();
        if (duplicate is not null)
        {
            return new ToolBatchPreflightResult
            {
                FailedOrdinal = duplicate.Ordinal,
                FailedToolId = duplicate.Invocation.ToolId,
                ErrorClassification = ToolErrorClassification.InvalidArguments,
                SafeReason = "Sibling tool-call correlation identifiers must be unique.",
            };
        }

        var planner = new ToolConflictPlanner(_registry, _parallelOptions);
        var waves = planner.Plan(requests);
        foreach (var planned in waves.SelectMany(static wave => wave).OrderBy(item => item.Request.Ordinal))
        {
            if (planned.PreparationError is not null)
            {
                return new ToolBatchPreflightResult
                {
                    FailedOrdinal = planned.Request.Ordinal,
                    FailedToolId = planned.Request.Invocation.ToolId,
                    ErrorClassification = ToolErrorClassification.InvalidArguments,
                    SafeReason = CreatePreflightSafeReason(planned.PreparationError),
                };
            }
        }

        return new ToolBatchPreflightResult
        {
            Succeeded = true,
            Preparation = new ToolBatchPreparation(waves),
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolBatchResult>> InvokePreparedBatchAsync(
        ToolBatchPreparation preparation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (preparation.Requests.Count == 0)
        {
            return [];
        }

        return await InvokePlannedWavesAsync(
            preparation.Requests,
            preparation.Waves,
            usePreparedSnapshot: true,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ToolBatchResult>> InvokeBatchAsync(
        IReadOnlyList<ToolBatchRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        if (requests.Select(request => request.CorrelationId).Distinct(StringComparer.Ordinal).Count() != requests.Count)
        {
            throw new ToolArgumentValidationException("Sibling tool-call correlation identifiers must be unique.");
        }

        var planner = new ToolConflictPlanner(_registry, _parallelOptions);
        var waves = planner.Plan(requests);
        return await InvokePlannedWavesAsync(
            requests,
            waves,
            usePreparedSnapshot: false,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ToolInvocationResult> InvokeAsync(
        ToolInvocationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ToolId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ArgumentsJson);
        ArgumentNullException.ThrowIfNull(request.Context);
        return await InvokeCoreAsync(
            request,
            preparationError: null,
            preparedRegistration: null,
            returnCancellationResult: false,
            cancellationToken);
    }

    private async Task<IReadOnlyList<ToolBatchResult>> InvokePlannedWavesAsync(
        IReadOnlyList<ToolBatchRequest> requests,
        IReadOnlyList<IReadOnlyList<PlannedToolInvocation>> waves,
        bool usePreparedSnapshot,
        CancellationToken cancellationToken)
    {
        var results = new List<ToolBatchResult>(requests.Count);
        using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        foreach (var wave in waves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task<ToolBatchResult>[] tasks = [.. wave.Select(async planned =>
            {
                var invocation = usePreparedSnapshot
                    ? planned.Request.Invocation
                    : planned.Request.Invocation with
                    {
                        ExpectedRegistration = planned.Registration?.Tool,
                    };
                var result = await InvokeCoreAsync(
                    invocation,
                    planned.PreparationError,
                    usePreparedSnapshot ? planned.Registration : null,
                    returnCancellationResult: true,
                    batchCancellation.Token);
                if (_parallelOptions.FailureMode == ToolBatchFailureMode.CancelBatchOnFailure
                    && !result.Succeeded
                    && result.ErrorClassification != ToolErrorClassification.Cancelled)
                {
                    await batchCancellation.CancelAsync();
                }

                return new ToolBatchResult(
                    planned.Request.Ordinal,
                    planned.Request.CorrelationId,
                    result);
            })];
            var waveResults = await Task.WhenAll(tasks);
            results.AddRange(waveResults);
            if (batchCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                var completedCorrelationIds = results
                    .Select(result => result.CorrelationId)
                    .ToHashSet(StringComparer.Ordinal);
                results.AddRange(requests
                    .Where(request => !completedCorrelationIds.Contains(request.CorrelationId))
                    .Select(request => new ToolBatchResult(
                        request.Ordinal,
                        request.CorrelationId,
                        new ToolInvocationResult
                        {
                            ToolInvocationId = ToolInvocationId.New(),
                            ToolId = request.Invocation.ToolId,
                            ErrorClassification = ToolErrorClassification.Cancelled,
                            Error = "Skipped because an earlier sibling tool call failed.",
                        })));
                break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return [.. results.OrderBy(result => result.Ordinal)];
    }

    private async Task<ToolInvocationResult> InvokeCoreAsync(
        ToolInvocationRequest request,
        string? preparationError,
        ToolRegistration? preparedRegistration,
        bool returnCancellationResult,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);

        var invocationId = ToolInvocationId.New();
        var suppressLifecycleHooks = request.Context.RequestedBy.StartsWith("hook:", StringComparison.Ordinal);
        var startedAt = _timeProvider.GetUtcNow();
        var registration = preparedRegistration;
        if (registration is null)
        {
            try
            {
                registration = _registry.GetRegistration(request.ToolId);
            }
            catch (KeyNotFoundException)
            {
                registration = null;
            }
        }

        var source = registration?.Source
            ?? new ToolActivitySource(ToolActivitySourceKind.Unknown);

        using var activity = _activitySource.StartActivity("tool.invoke");
        activity?.SetTag("threadsmith.tool.id", request.ToolId);
        activity?.SetTag("threadsmith.tool.invocation_id", invocationId.Value.ToString("D"));
        activity?.SetTag("threadsmith.tool.arguments_included", false);

        if (preparationError is not null)
        {
            await PublishStartedAsync(request, invocationId, startedAt, source, activityDetail: null);
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.InvalidArguments,
                preparationError,
                startedAt);
        }

        ITool tool;
        object input;
        try
        {
            tool = registration?.Tool ?? request.ExpectedRegistration ?? _registry.Get(request.ToolId);
            if (request.ExpectedRegistration is not null
                && !ReferenceEquals(tool, request.ExpectedRegistration))
            {
                throw new ToolArgumentValidationException(
                    $"Tool '{request.ToolId}' no longer matches the approved capability identity.");
            }

            input = tool.DeserializeInput(request.ArgumentsJson);
        }
        catch (Exception exception) when (exception is KeyNotFoundException
            or ToolArgumentValidationException
            or ArgumentException)
        {
            await PublishStartedAsync(request, invocationId, startedAt, source, activityDetail: null);
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.InvalidArguments,
                exception.Message,
                startedAt);
        }

        var activityDetail = CreateActivityDetail(tool, input);
        await PublishStartedAsync(request, invocationId, startedAt, source, activityDetail);

        ToolPolicyDecision policyDecision;
        try
        {
            policyDecision = _policy.Evaluate(tool, input, request.Context);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or UnauthorizedAccessException)
        {
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.PolicyDenied,
                exception.Message,
                startedAt);
        }

        if (!policyDecision.IsAllowed)
        {
            activity?.SetTag("threadsmith.tool.policy_allowed", false);
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.PolicyDenied,
                policyDecision.Reason,
                startedAt);
        }

        activity?.SetTag("threadsmith.tool.policy_allowed", true);
        if (_hooks is not null && !suppressLifecycleHooks)
        {
            HookBoundaryDecision hookDecision;
            try
            {
                hookDecision = await _hooks.InvokeAsync(
                    HookPoint.BeforeToolInvocation,
                    request.SessionId,
                    request.RunId,
                    request.Context.RepositoryPath,
                    invocationId.Value,
                    0,
                    new Dictionary<string, string>
                    {
                        ["toolId"] = tool.Definition.Id,
                        ["phase"] = request.Phase.ToString(),
                        ["requestedBy"] = request.Context.RequestedBy,
                    },
                    cancellationToken: cancellationToken);
            }
            catch (OperationCanceledException)
            {
                var cancelledResult = await CompleteFailureAsync(
                    request,
                    invocationId,
                    ToolErrorClassification.Cancelled,
                    "The tool invocation was cancelled while running its pre-invocation hooks.",
                    startedAt,
                    source: source);
                if (returnCancellationResult)
                {
                    return cancelledResult;
                }

                throw;
            }

            if (hookDecision.Decision == HookDecisionKind.Block)
            {
                return await CompleteFailureAsync(
                    request,
                    invocationId,
                    ToolErrorClassification.PolicyDenied,
                    "A trusted managed lifecycle policy blocked the tool invocation.",
                    startedAt);
            }
        }

        var budgetDelta = new BudgetDimensions(0, 1, TimeSpan.Zero);
        var budgetCheck = _budget?.Check(budgetDelta);
        if (budgetCheck?.IsExhausted == true)
        {
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.PolicyDenied,
                budgetCheck.Reason ?? "The tool-call budget is exhausted.",
                startedAt);
        }

        if (policyDecision.RequiredApproval != ApprovalLevel.None)
        {
            var approvalId = ApprovalId.New();
            var action = $"Invoke tool '{tool.Definition.Id}'";
            await _events.PublishAsync(
                new ApprovalRequested(
                    request.SessionId,
                    _timeProvider.GetUtcNow(),
                    approvalId,
                    action,
                    ApprovalRequestKind.ToolInvocation)
                {
                    SchemaVersion = 2,
                },
                CancellationToken.None);
            bool approved;
            try
            {
                approved = await _approvalPolicy.IsApprovedAsync(
                    action,
                    policyDecision.RequiredApproval,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await _events.PublishAsync(
                    new ApprovalDenied(
                        request.SessionId,
                        _timeProvider.GetUtcNow(),
                        approvalId,
                        "The approval request was cancelled."),
                    CancellationToken.None);
                var cancelledResult = await CompleteFailureAsync(
                    request,
                    invocationId,
                    ToolErrorClassification.Cancelled,
                    "The tool invocation was cancelled while awaiting approval.",
                    startedAt);
                if (returnCancellationResult)
                {
                    return cancelledResult;
                }

                throw;
            }

            if (!approved)
            {
                const string reason = "The required user approval was not granted.";
                await _events.PublishAsync(
                    new ApprovalDenied(
                        request.SessionId,
                        _timeProvider.GetUtcNow(),
                        approvalId,
                        reason),
                    CancellationToken.None);
                return await CompleteFailureAsync(
                    request,
                    invocationId,
                    ToolErrorClassification.ApprovalDenied,
                    reason,
                    startedAt);
            }

            await _events.PublishAsync(
                new ApprovalGranted(
                    request.SessionId,
                    _timeProvider.GetUtcNow(),
                    approvalId),
                CancellationToken.None);
        }

        var budgetStatus = _budget?.Accrue(budgetDelta);
        if (budgetStatus?.IsExhausted == true)
        {
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.PolicyDenied,
                budgetStatus.Reason ?? "The tool-call budget is exhausted.",
                startedAt);
        }

        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeoutCancellation.CancelAfter(tool.Definition.Timeout);
        var executionStarted = _timeProvider.GetTimestamp();
        try
        {
            using var sourceLease = await _sourceConcurrencyLimiter.AcquireAsync(
                source,
                tool.Definition.Scheduling.MaximumSourceConcurrency,
                timeoutCancellation.Token);
            var execution = await tool.ExecuteAsync(
                input,
                new ToolExecutionContext(
                    invocationId,
                    request.SessionId,
                    request.RunId,
                    request.Context)
                {
                    Phase = request.Phase,
                },
                timeoutCancellation.Token);
            var executionDuration = _timeProvider.GetElapsedTime(executionStarted);
            var authoritativeElapsedMilliseconds = execution.AuthoritativeElapsedMilliseconds
                ?? ToElapsedMilliseconds(executionDuration);
            await using var resultStream = new MemoryStream();
            await JsonSerializer.SerializeAsync(
                resultStream,
                execution.Value,
                execution.Value.GetType(),
                cancellationToken: timeoutCancellation.Token);
            if (resultStream.Length > tool.Definition.MaximumOutputBytes)
            {
                return await CompleteFailureAsync(
                    request,
                    invocationId,
                    ToolErrorClassification.OutputLimitExceeded,
                    "The tool result exceeded its declared output bound.",
                    startedAt,
                    isTruncated: true,
                    source,
                    authoritativeElapsedMilliseconds);
            }

            var resultJson = JsonOutputSanitizer.Sanitize(
                Encoding.UTF8.GetString(resultStream.GetBuffer(), 0, (int)resultStream.Length),
                _sanitizer);
            if (Encoding.UTF8.GetByteCount(resultJson) > tool.Definition.MaximumOutputBytes)
            {
                return await CompleteFailureAsync(
                    request,
                    invocationId,
                    ToolErrorClassification.OutputLimitExceeded,
                    "The sanitized tool result exceeded its declared output bound.",
                    startedAt,
                    isTruncated: true,
                    source,
                    authoritativeElapsedMilliseconds);
            }

            string? modelResultContent = null;
            if (!string.IsNullOrEmpty(execution.ModelResultContent))
            {
                modelResultContent = _sanitizer.Sanitize(execution.ModelResultContent);
                if (Encoding.UTF8.GetByteCount(modelResultContent) > tool.Definition.MaximumOutputBytes)
                {
                    return await CompleteFailureAsync(
                        request,
                        invocationId,
                        ToolErrorClassification.OutputLimitExceeded,
                        "The sanitized model-visible tool result exceeded its declared output bound.",
                        startedAt,
                        isTruncated: true,
                        source,
                        authoritativeElapsedMilliseconds);
                }
            }

            var isTruncated = execution.IsTruncated;
            if (tool is IPostSanitizationToolOutputBoundary outputBoundary)
            {
                var boundedOutput = outputBoundary.BoundSanitizedOutput(
                    resultJson,
                    modelResultContent,
                    request.Context);
                resultJson = boundedOutput.ResultJson;
                modelResultContent = boundedOutput.ModelResultContent;
                isTruncated |= boundedOutput.WasTruncated;
                if (Encoding.UTF8.GetByteCount(resultJson) > tool.Definition.MaximumOutputBytes
                    || (modelResultContent is not null
                        && Encoding.UTF8.GetByteCount(modelResultContent) > tool.Definition.MaximumOutputBytes))
                {
                    return await CompleteFailureAsync(
                        request,
                        invocationId,
                        ToolErrorClassification.OutputLimitExceeded,
                        "The bounded sanitized tool output exceeded its declared output bound.",
                        startedAt,
                        isTruncated: true,
                        source,
                        authoritativeElapsedMilliseconds);
                }
            }

            var duration = authoritativeElapsedMilliseconds is { } measured
                ? TimeSpan.FromMilliseconds(measured)
                : TimeSpan.Zero;
            activity?.SetTag("threadsmith.tool.succeeded", true);
            activity?.SetStatus(ActivityStatusCode.Ok);
            _latency.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>(
                "threadsmith.tool.id",
                tool.Definition.Id));
            await _events.PublishAsync(
                new ToolInvocationCompleted(
                    request.SessionId,
                    _timeProvider.GetUtcNow(),
                    invocationId,
                    true,
                    resultJson,
                    IsTruncated: isTruncated,
                    Source: source,
                    ElapsedMilliseconds: authoritativeElapsedMilliseconds,
                    Outcome: OperationActivityOutcome.Completed,
                    ModelResultContent: modelResultContent),
                CancellationToken.None);
            await InvokeAfterHookAsync(request, invocationId, succeeded: true, null, suppressLifecycleHooks);
            return new ToolInvocationResult
            {
                ToolInvocationId = invocationId,
                ToolId = tool.Definition.Id,
                Succeeded = true,
                ResultJson = resultJson,
                ModelResultContent = modelResultContent,
                Sources = execution.Sources,
                IsTruncated = isTruncated,
                ErrorClassification = ToolErrorClassification.None,
                Duration = duration,
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.Timeout,
                "The tool invocation timed out.",
                startedAt,
                source: source,
                elapsedMilliseconds: GetElapsedMilliseconds(executionStarted));
        }
        catch (OperationCanceledException)
        {
            var cancelledResult = await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.Cancelled,
                "The tool invocation was cancelled.",
                startedAt,
                source: source,
                elapsedMilliseconds: GetElapsedMilliseconds(executionStarted));
            if (returnCancellationResult)
            {
                return cancelledResult;
            }

            throw;
        }
        catch (ToolExecutionException exception)
        {
            var elapsedMilliseconds = exception.AuthoritativeElapsedMilliseconds
                ?? GetElapsedMilliseconds(executionStarted);
            return await CompleteFailureAsync(
                request,
                invocationId,
                exception.ErrorClassification,
                exception.Message,
                startedAt,
                source: source,
                elapsedMilliseconds: elapsedMilliseconds,
                transientError: exception.TransientError);
        }
        catch (TimeoutException exception)
        {
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.Timeout,
                exception.Message,
                startedAt,
                source: source,
                elapsedMilliseconds: GetElapsedMilliseconds(executionStarted));
        }
        catch (Exception exception)
        {
            var sanitizedError = _sanitizer.Sanitize(exception.Message);
            _logger.LogError(
                "Tool {ToolId} failed for invocation {ToolInvocationId}: {Error}",
                tool.Definition.Id,
                invocationId.Value,
                sanitizedError);
            return await CompleteFailureAsync(
                request,
                invocationId,
                ToolErrorClassification.ExecutionFailure,
                sanitizedError,
                startedAt,
                source: source,
                elapsedMilliseconds: GetElapsedMilliseconds(executionStarted));
        }
    }

    private string CreatePreflightSafeReason(string? preparationError)
    {
        if (string.IsNullOrWhiteSpace(preparationError))
        {
            return "Tool arguments do not match the declared input schema or host invariants.";
        }

        var sanitized = _sanitizer.Sanitize(preparationError);
        return string.IsNullOrWhiteSpace(sanitized)
            ? "Tool arguments do not match the declared input schema or host invariants."
            : BoundSingleLine(sanitized, MaximumPreflightReasonCharacters);
    }

    private static string BoundSingleLine(string value, int maximumCharacters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        var builder = new StringBuilder(Math.Min(value.Length, maximumCharacters));
        foreach (var character in value)
        {
            if (builder.Length == maximumCharacters)
            {
                break;
            }

            builder.Append(char.IsWhiteSpace(character) || char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString().Trim();
    }

    private string? CreateActivityDetail(ITool tool, object input)
    {
        string? detail;
        try
        {
            detail = tool.GetActivityDetail(input);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Tool {ToolId} failed to create optional activity detail.",
                tool.Definition.Id);
            return null;
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var sanitized = _sanitizer.Sanitize(detail);
        var normalized = new StringBuilder(Math.Min(sanitized.Length, MaximumActivityDetailCharacters + 2));
        var previousWasWhitespace = false;
        foreach (var rune in sanitized.EnumerateRunes())
        {
            var isWhitespace = Rune.IsWhiteSpace(rune) || Rune.IsControl(rune);
            if (isWhitespace)
            {
                if (!previousWasWhitespace && normalized.Length > 0)
                {
                    normalized.Append(' ');
                }

                previousWasWhitespace = true;
            }
            else
            {
                normalized.Append(rune.ToString());
                previousWasWhitespace = false;
            }

            if (normalized.Length > MaximumActivityDetailCharacters)
            {
                break;
            }
        }

        var result = normalized.ToString().Trim();
        if (result.Length > MaximumActivityDetailCharacters)
        {
            const int maximumContentCharacters = MaximumActivityDetailCharacters - 3;
            normalized.Clear();
            foreach (var rune in result.EnumerateRunes())
            {
                if (normalized.Length + rune.Utf16SequenceLength > maximumContentCharacters)
                {
                    break;
                }

                normalized.Append(rune.ToString());
            }

            result = normalized.ToString().TrimEnd() + "...";
        }

        return result.Length == 0 ? null : result;
    }

    private Task PublishStartedAsync(
        ToolInvocationRequest request,
        ToolInvocationId invocationId,
        DateTimeOffset startedAt,
        ToolActivitySource source,
        string? activityDetail)
    {
        return _events.PublishAsync(
            new ToolInvocationStarted(
                request.SessionId,
                startedAt,
                invocationId,
                request.ToolId,
                request.RunId,
                request.Context.RequestedBy,
                source,
                activityDetail),
            CancellationToken.None);
    }

    private async Task<ToolInvocationResult> CompleteFailureAsync(
        ToolInvocationRequest request,
        ToolInvocationId invocationId,
        ToolErrorClassification classification,
        string error,
        DateTimeOffset startedAt,
        bool isTruncated = false,
        ToolActivitySource? source = null,
        long? elapsedMilliseconds = null,
        string? transientError = null)
    {
        var sanitizedError = _sanitizer.Sanitize(error);
        var returnedError = transientError is null
            ? sanitizedError
            : _sanitizer.Sanitize(transientError);
        source ??= ResolveSourceOrUnknown(request.ToolId);
        var duration = elapsedMilliseconds is { } measured
            ? TimeSpan.FromMilliseconds(measured)
            : TimeSpan.Zero;
        Activity.Current?.SetTag("threadsmith.tool.succeeded", false);
        Activity.Current?.SetTag("threadsmith.tool.error_classification", classification.ToString());
        Activity.Current?.SetStatus(ActivityStatusCode.Error, classification.ToString());
        _latency.Record(duration.TotalMilliseconds, new KeyValuePair<string, object?>(
            "threadsmith.tool.id",
            request.ToolId));
        if (classification is ToolErrorClassification.InvalidArguments
            or ToolErrorClassification.PolicyDenied
            or ToolErrorClassification.ApprovalDenied)
        {
            _rejections.Add(1, new KeyValuePair<string, object?>(
                "threadsmith.tool.classification",
                classification.ToString()));
        }

        var outcome = classification switch
        {
            ToolErrorClassification.Cancelled => OperationActivityOutcome.Cancelled,
            ToolErrorClassification.Timeout => OperationActivityOutcome.TimedOut,
            _ => OperationActivityOutcome.Failed,
        };
        await _events.PublishAsync(
            new ToolInvocationCompleted(
                request.SessionId,
                _timeProvider.GetUtcNow(),
                invocationId,
                false,
                Error: sanitizedError,
                IsTruncated: isTruncated,
                Source: source,
                ElapsedMilliseconds: elapsedMilliseconds,
                Outcome: outcome),
            CancellationToken.None);
        await InvokeAfterHookAsync(
            request,
            invocationId,
            succeeded: false,
            classification.ToString(),
            request.Context.RequestedBy.StartsWith("hook:", StringComparison.Ordinal));
        return new ToolInvocationResult
        {
            ToolInvocationId = invocationId,
            ToolId = request.ToolId,
            Succeeded = false,
            IsTruncated = isTruncated,
            ErrorClassification = classification,
            Error = returnedError,
            Duration = duration,
        };
    }

    private ToolActivitySource ResolveSourceOrUnknown(string toolId)
    {
        try
        {
            return _registry.GetSource(toolId);
        }
        catch (KeyNotFoundException)
        {
            return new ToolActivitySource(ToolActivitySourceKind.Unknown);
        }
    }

    private long? GetElapsedMilliseconds(long startedTimestamp)
    {
        return ToElapsedMilliseconds(_timeProvider.GetElapsedTime(startedTimestamp));
    }

    private static long? ToElapsedMilliseconds(TimeSpan elapsed)
    {
        return elapsed < TimeSpan.Zero ? null : elapsed.Ticks / TimeSpan.TicksPerMillisecond;
    }

    private async Task InvokeAfterHookAsync(
        ToolInvocationRequest request,
        ToolInvocationId invocationId,
        bool succeeded,
        string? classification,
        bool suppressLifecycleHooks)
    {
        if (_hooks is null || suppressLifecycleHooks)
        {
            return;
        }

        _ = await _hooks.InvokeAsync(
            HookPoint.AfterToolInvocation,
            request.SessionId,
            request.RunId,
            request.Context.RepositoryPath,
            invocationId.Value,
            0,
            new Dictionary<string, string>
            {
                ["toolId"] = request.ToolId,
                ["succeeded"] = succeeded.ToString(),
                ["classification"] = classification ?? "None",
            },
            cancellationToken: CancellationToken.None);
    }
}
