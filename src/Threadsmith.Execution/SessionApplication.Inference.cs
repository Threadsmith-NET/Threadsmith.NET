namespace Threadsmith.Execution;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Threadsmith.Core;
using Threadsmith.Models;
using Threadsmith.Tools;

/// <summary>Shared model lifecycle and invocation-bound internal inference admission.</summary>
public sealed partial class SessionApplication : IBoundedModelInference
{
    /// <inheritdoc />
    public IBoundedModelOperation Open(ToolExecutionContext context, int maximumCalls, int maximumResultBytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCalls, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCalls, 32);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumResultBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumResultBytes, 262144);
        if (context.Invocation.IsDelegated || !_sessions.ContainsKey(context.SessionId))
        {
            throw new UnauthorizedAccessException("Internal inference requires a registered top-level session.");
        }

        var catalog = _inferenceCatalog ?? throw new InvalidOperationException("Configured inference is unavailable.");
        var preferences = _sessionPreferences?.Capture();
        var selected = new DefaultModelSelectionPolicy(catalog).Resolve(new ModelSelectionRequest
        {
            WorkloadClass = WorkloadClass.General,
            PreferredProfileId = context.Invocation.ModelProfileId ?? preferences?.ProfileId ?? _defaultModelProfileId,
            RequiredCapabilities = new() { Streaming = true, StructuredOutput = true },
            Constraints = new() { ContainsSensitiveData = true },
        });
        var profile = catalog.Get(selected.ProfileId);
        var reasoning = context.Invocation.ModelReasoningLevel is { } level
            ? new ReasoningLevel(level) : preferences?.ResolveFor(profile.Id, profile.DefaultReasoningLevel) ?? profile.DefaultReasoningLevel;
        if (!profile.SupportsReasoningLevel(reasoning))
        {
            throw new InvalidOperationException("The selected inference model does not support the caller's reasoning level.");
        }

        _runs.TryGetValue(context.RunId, out var registration);
        if (registration is not null && registration.SessionId != context.SessionId)
        {
            throw new UnauthorizedAccessException("Inference run belongs to another session.");
        }

        return new BoundedModelOperation(this, context, profile, reasoning, registration?.Budget ?? _budgetFactory(), registration, maximumCalls, maximumResultBytes);
    }

    private async IAsyncEnumerable<ModelChunk> StreamHostModelRequestAsync(
        SessionId sessionId,
        RunId runId,
        string? repositoryPath,
        ModelRequestUsageId usageId,
        string stage,
        IBudget budget,
        ModelStreamRequest request,
        Action<TimeSpan> accrueElapsed,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var boundary = new ModelRequestHookBoundary(sessionId, runId, repositoryPath, usageId.InvocationId, request.ToolContinuationRound, stage, request.WorkloadClass, request.ContainsSensitiveData, request.Tools.Count);
        ModelRequestBudgetUsage.CheckAdmission(budget, request);
        await InvokeBeforeModelRequestHookAsync(boundary, cancellationToken);
        var usage = new ModelRequestBudgetUsage();
        var reported = false;
        var succeeded = false;
        string? exhaustedReason = null;
        var clock = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            usage.Start(budget, request);
            await foreach (var chunk in RepositoryMemoryDispatch.StreamAsync(_model, request, _repositoryMemories, _contextAssembler, _logger, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chunk.Usage is { } receipt)
                {
                    reported = true;
                    _sessionUsage?.Observe(sessionId, usageId, receipt);
                    var status = usage.Accrue(budget, receipt);
                    if (status.IsExhausted)
                    {
                        throw new BudgetExceededException(status.Reason ?? "Model execution budget exhausted.");
                    }
                }

                yield return chunk;
            }

            cancellationToken.ThrowIfCancellationRequested();
            succeeded = true;
        }
        finally
        {
            clock.Stop();
            var status = budget.Accrue(new BudgetDimensions(0, 0, clock.Elapsed));
            accrueElapsed(clock.Elapsed);
            if (usage.HasStarted && !reported)
            {
                _sessionUsage?.ObserveMissing(sessionId, usageId);
            }

            await InvokeAfterModelRequestHookAsync(boundary, succeeded, reported);
            if (succeeded && status.IsExhausted)
            {
                exhaustedReason = status.Reason ?? "Model wall-clock budget exhausted.";
            }
        }

        if (exhaustedReason is not null)
        {
            throw new BudgetExceededException(exhaustedReason);
        }
    }

    private sealed class BoundedModelOperation : IBoundedModelOperation
    {
        private readonly SessionApplication _owner;
        private readonly ToolExecutionContext _context;
        private readonly ModelProfile _profile;
        private readonly ReasoningLevel _reasoning;
        private readonly IBudget _budget;
        private readonly RunRegistration? _registration;
        private readonly int _maximumCalls;
        private readonly int _maximumResultBytes;
        private int _calls;
        private int _busy;
        private bool _disposed;

        public BoundedModelOperation(SessionApplication owner, ToolExecutionContext context, ModelProfile profile, ReasoningLevel reasoning, IBudget budget, RunRegistration? registration, int maximumCalls, int maximumResultBytes)
        {
            _owner = owner;
            _context = context;
            _profile = profile;
            _reasoning = reasoning;
            _budget = budget;
            _registration = registration;
            _maximumCalls = maximumCalls;
            _maximumResultBytes = maximumResultBytes;
            Selection = new(profile.Id, profile.Provider, profile.ModelId, reasoning.ToString(), profile.ContextWindow);
        }

        public BoundedModelSelection Selection { get; }

        public int MaximumOutputTokens => _profile.MaximumOutputTokens;

        public void Dispose() => _disposed = true;

        public async Task<string> ExecuteAsync(BoundedModelRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            {
                throw new InvalidOperationException("One inference request may execute at a time per operation.");
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _registration?.Cancellation.Token ?? default);
                var token = linked.Token;
                token.ThrowIfCancellationRequested();
                if (_calls >= _maximumCalls)
                {
                    throw new BudgetExceededException("The invocation model-call allowance is exhausted.");
                }

                ArgumentException.ThrowIfNullOrWhiteSpace(request.Instructions);
                ArgumentException.ThrowIfNullOrWhiteSpace(request.DataJson);
                ArgumentException.ThrowIfNullOrWhiteSpace(request.SchemaId);
                ArgumentException.ThrowIfNullOrWhiteSpace(request.JsonSchema);
                ArgumentOutOfRangeException.ThrowIfLessThan(request.MaximumOutputTokens, 1);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(request.MaximumOutputTokens, _profile.MaximumOutputTokens);
                var data = JsonOutputSanitizer.Sanitize(request.DataJson, _owner._sanitizer);
                ModelMessage[] messages =
                [
                    new() { Role = ModelMessageRole.System, SectionId = "bounded-inference-policy", Content = [new() { Content = request.Instructions }] },
                    new() { Role = ModelMessageRole.System, SectionId = "bounded-inference-schema", Content = [new() { Kind = ModelContentPartKind.Json, Content = request.JsonSchema }] },
                    new() { Role = ModelMessageRole.User, SectionId = "bounded-inference-data", Content = [new() { Kind = ModelContentPartKind.Json, Content = data }] },
                ];
                var instructions = _owner._inferenceInstructions?.Resolve(_profile.Id);
                var stream = ModelRequestPreparation.Prepare(_owner._model, new ModelStreamRequest
                {
                    RunId = _context.RunId,
                    CacheAffinityId = _context.SessionId.Value,
                    Input = data,
                    Messages = messages,
                    ResolvedProfileId = _profile.Id,
                    ReasoningLevel = _reasoning,
                    IncludeReasoningText = false,
                    RequiredCapabilities = new() { Streaming = true, StructuredOutput = true },
                    ContainsSensitiveData = true,
                    SelectionConstraints = new() { ContainsSensitiveData = true },
                    MaximumOutputTokens = request.MaximumOutputTokens,
                    AdmissionCost = _profile.Cost,
                    AdmissionOutputTokenCeiling = _profile.EnforcesRequestOutputTokenLimit ? request.MaximumOutputTokens : _profile.MaximumOutputTokens,
                    AdmissionContextWindowTokens = _profile.ContextWindow,
                    AdmissionWallClock = _profile.Timeout > TimeSpan.Zero ? _profile.Timeout : null,
                    ResponseFormat = new() { SchemaId = request.SchemaId, JsonSchema = request.JsonSchema },
                    ProviderInstructions = instructions,
                    ToolContinuationRound = _calls,
                    WireEstimate = ModelWireEstimator.Estimate(messages, ModelWireEstimator.EstimateTools([], ToolTransportMode.Native), 0, request.MaximumOutputTokens, instructions),
                });
                if (stream.WireEstimate is not { } estimate || estimate.TotalCapacityTokens > _profile.ContextWindow)
                {
                    throw new BudgetExceededException("Required inference evidence and schema cannot fit the selected model; narrow the scope.");
                }

                var usageId = new ModelRequestUsageId(_context.RunId, "bounded-inference", _calls++, Guid.NewGuid());
                stream = _owner._sessionUsage?.ObservePreparedRequest(_context.SessionId, usageId, stream, _profile.ContextWindow) ?? stream;
                var text = new StringBuilder();
                var bytes = 0;
                var success = false;
                await _owner._events.PublishAsync(
                    new DiagnosticObserved(_context.SessionId, DateTimeOffset.UtcNow, "HostInferenceStarted", $"Run {_context.RunId.Value:D}; parent {_context.ToolInvocationId.Value:D}; request {usageId.InvocationId:D}; model {_profile.Name}; reasoning {_reasoning}.")
                    {
                        RunId = _context.RunId,
                        ToolInvocationId = _context.ToolInvocationId,
                        ActivityProgress = $"Interpreting evidence with {_profile.Name} ({_reasoning}).",
                    },
                    token);
                try
                {
                    await foreach (var chunk in _owner.StreamHostModelRequestAsync(
                        _context.SessionId,
                        _context.RunId,
                        _context.Invocation.RepositoryPath,
                        usageId,
                        "bounded-inference",
                        _budget,
                        stream,
                        AccrueElapsed,
                        token))
                    {
                        var delta = chunk.Text ?? (chunk.Output as TextModelOutput)?.Text;
                        if (chunk.Output is not null and not TextModelOutput || chunk.FinishReason is ModelFinishReason.Length or ModelFinishReason.ToolCalls)
                        {
                            throw new InvalidDataException("Bounded inference returned an incomplete response or unauthorized output.");
                        }

                        if (delta is not null)
                        {
                            bytes = checked(bytes + Encoding.UTF8.GetByteCount(delta));
                            if (bytes > _maximumResultBytes)
                            {
                                throw new InvalidDataException("Inference result exceeds its serialized byte allowance.");
                            }

                            text.Append(delta);
                        }
                    }

                    token.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    success = true;
                    return JsonOutputSanitizer.Sanitize(text.ToString(), _owner._sanitizer);
                }
                finally
                {
                    await _owner._events.PublishAsync(
                        new DiagnosticObserved(_context.SessionId, DateTimeOffset.UtcNow, "HostInferenceCompleted", $"Run {_context.RunId.Value:D}; parent {_context.ToolInvocationId.Value:D}; request {usageId.InvocationId:D}; succeeded {success}; response bytes {bytes}.")
                        {
                            RunId = _context.RunId,
                            ToolInvocationId = _context.ToolInvocationId,
                            ActivityProgress = success ? "Evidence interpretation completed." : "Evidence interpretation did not complete.",
                        },
                        CancellationToken.None);
                }
            }
            catch (Exception exception) when (exception is ModelProviderException or ModelProviderTimeoutException or TransientModelException or BudgetExceededException)
            {
                throw new InvalidOperationException("The bounded model request is unavailable or its host budget is exhausted.", exception);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The model did not return valid structured JSON.", exception);
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        private void AccrueElapsed(TimeSpan elapsed)
        {
            _registration?.ModelRequestWallClockAccrued += elapsed;
        }
    }
}
