namespace Threadsmith.Hooks;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Threadsmith.Core;

/// <summary>Deterministic bounded lifecycle-hook coordinator.</summary>
public sealed class HookCoordinator : IHookCoordinator, IAsyncDisposable
{
    private static readonly ActivitySource ActivitySource = new("Threadsmith.Hooks");
    private static readonly Meter Meter = new("Threadsmith.Hooks");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("threadsmith.hook.duration", "ms");
    private static readonly Counter<long> Blocks = Meter.CreateCounter<long>("threadsmith.hook.blocks");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("threadsmith.hook.failures");
    private readonly ConcurrentDictionary<HookAdapterKind, IHookHandlerAdapter> _adapters;
    private readonly ConcurrentDictionary<string, bool> _enablement = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyList<HookHandlerDescriptor> _handlers;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _limiters = new(StringComparer.Ordinal);
    private readonly IDomainEventStream? _events;
    private readonly HookPolicyEvaluator _policy;
    private readonly ILogger<HookCoordinator> _logger;
    private readonly IOutputSanitizer _sanitizer;
    private readonly IHookStore _store;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="HookCoordinator"/> class.</summary>
    public HookCoordinator(
        IEnumerable<HookHandlerDescriptor> handlers,
        IEnumerable<IHookHandlerAdapter> adapters,
        HookPolicyEvaluator policy,
        IHookStore store,
        IOutputSanitizer sanitizer,
        ILogger<HookCoordinator> logger,
        IDomainEventStream? events = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sanitizer);
        ArgumentNullException.ThrowIfNull(logger);
        _handlers = HookDescriptorValidator.Normalize(handlers);
        _adapters = new ConcurrentDictionary<HookAdapterKind, IHookHandlerAdapter>(
            adapters.ToDictionary(adapter => adapter.Kind));
        _policy = policy;
        _store = store;
        _sanitizer = sanitizer;
        _logger = logger;
        _events = events;
        _timeProvider = timeProvider ?? TimeProvider.System;
        foreach (var handler in _handlers)
        {
            _limiters[handler.Identity.Id.Value] = new SemaphoreSlim(handler.Limits.MaximumConcurrency);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<HookHandlerDescriptor> Handlers => [.. _handlers.Select(EffectiveDescriptor)];

    /// <inheritdoc />
    public HookHandlerDescriptor? GetHandler(HookHandlerId handlerId)
    {
        var descriptor = _handlers.FirstOrDefault(handler => handler.Identity.Id == handlerId);
        return descriptor is null ? null : EffectiveDescriptor(descriptor);
    }

    /// <summary>Registers or replaces one host-composed transport adapter.</summary>
    public void RegisterAdapter(IHookHandlerAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        _adapters[adapter.Kind] = adapter;
    }

    /// <inheritdoc />
    public bool SetEnabled(HookHandlerId handlerId, bool enabled)
    {
        if (_handlers.All(handler => handler.Identity.Id != handlerId))
        {
            return false;
        }

        _enablement[handlerId.Value] = enabled;
        return true;
    }

    /// <inheritdoc />
    public async Task<HookBoundaryDecision> InvokeAsync(
        HookPoint point,
        SessionId sessionId,
        RunId? runId,
        string? repositoryIdentity,
        Guid operationId,
        int generation,
        IReadOnlyDictionary<string, string>? payload = null,
        IReadOnlyList<ExecutionArtifactReference>? artifacts = null,
        IReadOnlyList<string>? callChain = null,
        CancellationToken cancellationToken = default)
    {
        return await InvokeCoreAsync(
            point,
            handlerId: null,
            sessionId,
            runId,
            repositoryIdentity,
            operationId,
            generation,
            payload,
            artifacts,
            callChain,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HookBoundaryDecision> InvokeHandlerAsync(
        HookHandlerId handlerId,
        HookPoint point,
        SessionId sessionId,
        RunId? runId,
        string? repositoryIdentity,
        Guid operationId,
        int generation,
        IReadOnlyDictionary<string, string>? payload = null,
        IReadOnlyList<ExecutionArtifactReference>? artifacts = null,
        IReadOnlyList<string>? callChain = null,
        CancellationToken cancellationToken = default)
    {
        return await InvokeCoreAsync(
            point,
            handlerId,
            sessionId,
            runId,
            repositoryIdentity,
            operationId,
            generation,
            payload,
            artifacts,
            callChain,
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private async Task<HookBoundaryDecision> InvokeCoreAsync(
        HookPoint point,
        HookHandlerId? handlerId,
        SessionId sessionId,
        RunId? runId,
        string? repositoryIdentity,
        Guid operationId,
        int generation,
        IReadOnlyDictionary<string, string>? payload,
        IReadOnlyList<ExecutionArtifactReference>? artifacts,
        IReadOnlyList<string>? callChain,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || generation < 0)
        {
            throw new ArgumentException("Hook operation identity and generation must be valid.", nameof(operationId));
        }

        var safePayload = ValidatePayload(payload);
        var safeArtifacts = artifacts ?? [];
        var chain = callChain ?? [];
        if (chain.Count > 8)
        {
            return new HookBoundaryDecision(HookDecisionKind.Continue, [], ["hook recursion depth suppressed"]);
        }

        using var activity = ActivitySource.StartActivity($"hook.{point}");
        activity?.SetTag("hook.point", point.ToString());
        activity?.SetTag("hook.operation_id", operationId.ToString("N"));
        var audits = new List<HookAuditRecord>();
        var advice = new List<string>();
        var selectedHandlers = _handlers
            .Where(handler => handler.HookPoints.Contains(point));
        if (handlerId is { } selectedHandlerId)
        {
            selectedHandlers = selectedHandlers.Where(handler => handler.Identity.Id == selectedHandlerId);
        }

        foreach (var configured in selectedHandlers)
        {
            var descriptor = EffectiveDescriptor(configured);
            cancellationToken.ThrowIfCancellationRequested();
            var chainIdentity = $"{operationId:N}:{descriptor.Identity.Id.Value}:{point}";
            if (chain.Contains(chainIdentity, StringComparer.Ordinal))
            {
                advice.Add($"{descriptor.Identity.Id}: recursive invocation suppressed");
                continue;
            }

            var eligibility = await _policy.EvaluateAsync(
                descriptor,
                point,
                repositoryIdentity,
                cancellationToken);
            if (!eligibility.Eligible)
            {
                continue;
            }

            var audit = await InvokeHandlerAsync(
                descriptor,
                eligibility,
                point,
                sessionId,
                runId,
                repositoryIdentity,
                operationId,
                generation,
                safePayload,
                safeArtifacts,
                [.. chain, chainIdentity],
                cancellationToken);
            audits.Add(audit);
            if (audit.Status == HookInvocationStatus.Advised || (audit.Status == HookInvocationStatus.Denied && audit.Decision == HookDecisionKind.Continue))
            {
                advice.Add($"{descriptor.Identity.Id}: {audit.Explanation}");
            }

            if (audit.Decision == HookDecisionKind.Block)
            {
                Blocks.Add(1);
                return new HookBoundaryDecision(HookDecisionKind.Block, audits, advice);
            }
        }

        return new HookBoundaryDecision(HookDecisionKind.Continue, audits, advice);
    }

    private HookHandlerDescriptor EffectiveDescriptor(HookHandlerDescriptor descriptor)
    {
        return _enablement.TryGetValue(descriptor.Identity.Id.Value, out var enabled)
            ? descriptor with { Enabled = enabled }
            : descriptor;
    }

    private async Task<HookAuditRecord> InvokeHandlerAsync(
        HookHandlerDescriptor descriptor,
        HookEligibilityDecision eligibility,
        HookPoint point,
        SessionId sessionId,
        RunId? runId,
        string? repositoryIdentity,
        Guid operationId,
        int generation,
        IReadOnlyDictionary<string, string> payload,
        IReadOnlyList<ExecutionArtifactReference> artifacts,
        IReadOnlyList<string> callChain,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var invocationId = HookInvocationId.New();
        var envelope = new HookInvocationEnvelope
        {
            HookPoint = point,
            InvocationId = invocationId,
            HandlerIdentity = descriptor.Identity,
            SessionId = sessionId,
            RunId = runId,
            RepositoryIdentity = repositoryIdentity,
            OperationId = operationId,
            Generation = generation,
            OccurredAt = startedAt,
            DataScope = eligibility.DataScope,
            Payload = payload,
            SecretReferences = eligibility.SecretReferences,
            Artifacts = eligibility.DataScope.HasFlag(HookDataScope.ArtifactReferences) ? artifacts : [],
            CallChain = callChain,
        };
        await PublishAsync(new HookInvocationStartedEvent(sessionId, startedAt, invocationId, point, descriptor.Identity.Id, operationId), cancellationToken);

        HookInvocationStatus status;
        HookHandlerResult? result = null;
        string? code = null;
        string? explanation = null;
        var decision = HookDecisionKind.Continue;
        var limiter = _limiters[descriptor.Identity.Id.Value];
        await limiter.WaitAsync(cancellationToken);
        try
        {
            if (!_adapters.TryGetValue(descriptor.AdapterKind, out var adapter))
            {
                result = new HookFailureResult("adapter-unavailable", "The configured hook adapter is unavailable.");
            }
            else
            {
                result = await InvokeWithRetriesAsync(adapter, descriptor, envelope, cancellationToken);
            }

            ValidateResult(result, descriptor.Limits.MaximumOutputBytes);
            (status, code, explanation, decision) = NormalizeDecision(result, eligibility);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            status = HookInvocationStatus.TimedOut;
            code = "timeout";
            explanation = "The hook handler exceeded its timeout.";
            decision = eligibility.FailureMode == HookFailureMode.FailClosed
                && eligibility.Authority == HookAuthority.ManagedBlocking
                ? HookDecisionKind.Block
                : HookDecisionKind.Continue;
            Failures.Add(1);
        }
        catch (TimeoutException)
        {
            status = HookInvocationStatus.TimedOut;
            code = "timeout";
            explanation = "The hook handler exceeded its timeout.";
            decision = eligibility.FailureMode == HookFailureMode.FailClosed
                && eligibility.Authority == HookAuthority.ManagedBlocking
                ? HookDecisionKind.Block
                : HookDecisionKind.Continue;
            Failures.Add(1);
        }
        catch (OperationCanceledException)
        {
            status = HookInvocationStatus.Cancelled;
            code = "cancelled";
            explanation = "The owning operation was cancelled.";
            decision = HookDecisionKind.Cancelled;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Hook handler {HandlerId} failed", descriptor.Identity.Id.Value);
            status = HookInvocationStatus.Failed;
            code = "handler-failure";
            explanation = "The hook handler failed or returned malformed output.";
            decision = eligibility.FailureMode == HookFailureMode.FailClosed
                && eligibility.Authority == HookAuthority.ManagedBlocking
                ? HookDecisionKind.Block
                : HookDecisionKind.Continue;
            Failures.Add(1);
        }
        finally
        {
            limiter.Release();
        }

        var audit = new HookAuditRecord
        {
            InvocationId = invocationId,
            HookPoint = point,
            HandlerIdentity = descriptor.Identity,
            OperationId = operationId,
            RepositoryIdentity = repositoryIdentity,
            Status = status,
            Authority = eligibility.Authority,
            FailureMode = eligibility.FailureMode,
            ResultKind = result?.Kind,
            Code = Bound(code),
            Explanation = Bound(explanation),
            Decision = decision,
            DataScope = eligibility.DataScope,
            SecretReferences = eligibility.SecretReferences,
            AuthoritySource = Bound(eligibility.AuthoritySource),
            Duration = _timeProvider.GetUtcNow() - startedAt,
            RecordedAt = _timeProvider.GetUtcNow(),
        };
        var auditCancellation = decision == HookDecisionKind.Cancelled
            ? CancellationToken.None
            : cancellationToken;
        await _store.AppendAuditAsync(audit, auditCancellation);
        await PublishAsync(new HookInvocationCompletedEvent(sessionId, audit.RecordedAt, invocationId, point, descriptor.Identity.Id, operationId, status, decision, code), auditCancellation);
        Duration.Record(audit.Duration.TotalMilliseconds);
        if (decision == HookDecisionKind.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        return audit;
    }

    private static async Task<HookHandlerResult> InvokeWithRetriesAsync(
        IHookHandlerAdapter adapter,
        HookHandlerDescriptor descriptor,
        HookInvocationEnvelope envelope,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(descriptor.Limits.Timeout);
            var invocation = adapter.InvokeAsync(
                descriptor,
                envelope with { Attempt = attempt },
                timeout.Token);
            HookHandlerResult result;
            try
            {
                result = await invocation.WaitAsync(descriptor.Limits.Timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                await timeout.CancelAsync();
                _ = invocation.ContinueWith(
                    static completed => _ = completed.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                throw;
            }

            if (result is not HookFailureResult { Transient: true } || attempt > descriptor.Limits.MaximumRetries)
            {
                return result;
            }
        }
    }

    private static (HookInvocationStatus Status, string? Code, string? Explanation, HookDecisionKind Decision) NormalizeDecision(
        HookHandlerResult result,
        HookEligibilityDecision eligibility)
    {
        return result switch
        {
            HookAcknowledgeResult => (HookInvocationStatus.Acknowledged, null, null, HookDecisionKind.Continue),
            HookAdviceResult advice => (HookInvocationStatus.Advised, null, string.Join("; ", advice.Findings), HookDecisionKind.Continue),
            HookDenyResult denial when eligibility.Authority == HookAuthority.ManagedBlocking
                && eligibility.AllowedDenialCodes.Contains(denial.Code, StringComparer.Ordinal)
                => (HookInvocationStatus.Denied, denial.Code, denial.Explanation, HookDecisionKind.Block),
            HookDenyResult denial => (HookInvocationStatus.Denied, denial.Code, denial.Explanation, HookDecisionKind.Continue),
            HookFailureResult failure => (HookInvocationStatus.Failed, failure.Code, failure.Explanation,
                eligibility.Authority == HookAuthority.ManagedBlocking && eligibility.FailureMode == HookFailureMode.FailClosed
                    ? HookDecisionKind.Block
                    : HookDecisionKind.Continue),
            _ => throw new InvalidDataException("Unknown hook result kind."),
        };
    }

    private static void ValidateResult(HookHandlerResult result, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.SchemaVersion != 1 || JsonSerializer.SerializeToUtf8Bytes(result, result.GetType()).Length > maximumBytes)
        {
            throw new InvalidDataException("Hook result schema or size is invalid.");
        }

        switch (result)
        {
            case HookAdviceResult advice when advice.Findings.Count > 32
                || advice.Findings.Any(finding => string.IsNullOrWhiteSpace(finding) || finding.Length > 1024):
            case HookDenyResult denial when string.IsNullOrWhiteSpace(denial.Code)
                || denial.Code.Length > 64 || denial.Explanation.Length > 1024:
            case HookFailureResult failure when string.IsNullOrWhiteSpace(failure.Code)
                || failure.Code.Length > 64 || failure.Explanation.Length > 1024:
                throw new InvalidDataException("Hook result fields exceed bounds.");
        }
    }

    private static IReadOnlyDictionary<string, string> ValidatePayload(IReadOnlyDictionary<string, string>? payload)
    {
        if (payload is null)
        {
            return new Dictionary<string, string>();
        }

        if (payload.Count > 32 || payload.Any(pair => pair.Key.Length > 64 || pair.Value.Length > 2048))
        {
            throw new ArgumentOutOfRangeException(nameof(payload), "Hook payload exceeds metadata bounds.");
        }

        return payload.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private string? Bound(string? value)
    {
        return value is null ? null : _sanitizer.Sanitize(value)[..Math.Min(1024, _sanitizer.Sanitize(value).Length)];
    }

    private Task PublishAsync(IDomainEvent domainEvent, CancellationToken cancellationToken)
    {
        return _events is null
        ? Task.CompletedTask
        : _events.PublishAsync(domainEvent, cancellationToken);
    }
}
