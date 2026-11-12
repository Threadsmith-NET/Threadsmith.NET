namespace Threadsmith.Tools;

using System.Collections.Immutable;
using Threadsmith.Core;
using Threadsmith.Policies.Configuration;
using Threadsmith.Policies.Contracts;
using Threadsmith.Policies.Evaluation;
using Threadsmith.Policies.Grants;

/// <summary>Reports an Always save separately from admission of its originating invocation; never project it into model context.</summary>
public sealed record CommandApprovalCompletion(Guid RequestId, PolicyUpdateReceipt? Receipt, bool ContinuationApproved);

/// <summary>Adapts scoped command approvals to the existing tool approval boundary.</summary>
public sealed class CommandPolicyCoordinator : IApprovalPolicy
{
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _promptGate = new(1, 1);
    private Func<PolicyApprovalRequest, CancellationToken, Task<PolicyApprovalResponse>>? _handler;
    private Func<CommandApprovalCompletion, CancellationToken, Task>? _completionHandler;
    private string? _scope;
    private Func<EffectiveSessionPolicy>? _currentPolicy;

    /// <summary>Gets the shared, memory-only grant authority.</summary>
    public PolicyGrantAuthority Grants { get; } = new();

    /// <summary>Gets the effective configuration service after application composition.</summary>
    public PolicyConfigurationService? Configuration { get; private set; }

    /// <summary>Attaches the single coordinated configuration adapter.</summary>
    public void AttachConfiguration(PolicyConfigurationService service, Func<EffectiveSessionPolicy> currentPolicy)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(currentPolicy);
        if (Configuration is not null)
        {
            throw new InvalidOperationException("Command configuration is already attached.");
        }

        Configuration = service;
        _currentPolicy = currentPolicy;
    }

    /// <summary>Gets a host-only management snapshot; callers must not project it into model context.</summary>
    public EffectiveSessionPolicy Inspect() => _currentPolicy?.Invoke() ?? throw new InvalidOperationException("Command policy is unavailable.");

    /// <summary>Attaches the frontend-neutral modal projection for its interactive lifetime.</summary>
    public IDisposable Attach(
        Func<PolicyApprovalRequest, CancellationToken, Task<PolicyApprovalResponse>> handler,
        Func<CommandApprovalCompletion, CancellationToken, Task>? completionHandler = null)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
        {
            if (_handler is not null)
            {
                throw new InvalidOperationException("Command approval is already attached.");
            }

            _handler = handler;
            _completionHandler = completionHandler;
            return new HandlerLease(this, handler);
        }
    }

    /// <summary>Revokes live approvals at their common lifecycle boundary.</summary>
    public void Reset() => InvalidateAuthority(null);

    /// <summary>Revokes live authority while preserving only an explicitly correlated pending publication.</summary>
    public void InvalidateAuthority(Guid? authorityTransitionId)
    {
        lock (_gate)
        {
            _scope = null;
            Grants.Reset(authorityTransitionId);
        }
    }

    /// <inheritdoc />
    public Task<bool> IsApprovedAsync(string action, ApprovalLevel requiredLevel, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(requiredLevel == ApprovalLevel.None);
    }

    /// <inheritdoc />
    public async Task<PolicyApprovalResponse> RequestApprovalAsync(PolicyApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _promptGate.WaitAsync(cancellationToken);
        try
        {
            Func<PolicyApprovalRequest, CancellationToken, Task<PolicyApprovalResponse>>? handler;
            lock (_gate)
            {
                handler = _handler;
            }

            return handler is null ? new PolicyApprovalResponse(request.RequestId, PolicyApprovalChoice.Deny)
                : await handler(request, cancellationToken);
        }
        finally
        {
            _promptGate.Release();
        }
    }

    /// <summary>Reports persistence only to the attached user surface, not the domain event stream.</summary>
    internal Task ReportCompletionAsync(CommandApprovalCompletion completion, CancellationToken cancellationToken = default)
    {
        Func<CommandApprovalCompletion, CancellationToken, Task>? handler;
        lock (_gate)
        {
            handler = _completionHandler;
        }

        return handler?.Invoke(completion, cancellationToken) ?? Task.CompletedTask;
    }

    /// <summary>Atomically adopts a refreshed scope and completes its exact persistent continuation.</summary>
    internal bool CompletePersistent(Guid operation, SessionId sessionId, EffectiveSessionPolicy policy, PolicyApprovalRequest request, string ruleFingerprint, out long epoch)
    {
        lock (_gate)
        {
            if (!Grants.CompletePersistent(operation, request, ruleFingerprint, out epoch))
            {
                return false;
            }

            _scope = sessionId.Value + ":" + policy.BindingId + ":" + policy.Generation;
            return true;
        }
    }

    /// <summary>Ensures parent and eligible children share only the same live authority scope.</summary>
    internal void EnsureScope(SessionId sessionId, EffectiveSessionPolicy policy)
    {
        var scope = sessionId.Value + ":" + policy.BindingId + ":" + policy.Generation;
        lock (_gate)
        {
            if (_scope != scope)
            {
                Grants.Reset();
                _scope = scope;
            }
        }
    }

    /// <summary>Combines all action requirements without offering a scope excluded by any requirement.</summary>
    internal static PolicyDecision Combine(PolicyDecision[] decisions, bool mandatoryReview)
    {
        var outcome = mandatoryReview || decisions.Any(decision => decision.Outcome == PolicyOutcome.ApprovalAlwaysRequired)
            ? PolicyOutcome.ApprovalAlwaysRequired
            : decisions.Any(decision => decision.Outcome == PolicyOutcome.ApprovalRequiredSession) ? PolicyOutcome.ApprovalRequiredSession
            : decisions.Any(decision => decision.Outcome == PolicyOutcome.ApprovalRequired) ? PolicyOutcome.ApprovalRequired : PolicyOutcome.Allow;
        ImmutableArray<string> ids = [.. decisions.SelectMany(decision => decision.RuleIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        var fingerprint = PolicyJson.Fingerprint(new { Rules = decisions.Select(decision => decision.RuleFingerprint).ToArray(), outcome });
        return new PolicyDecision(true, outcome != PolicyOutcome.Allow, PolicyReasons.Matched, outcome, ids, fingerprint, PolicyEvaluator.Choices(outcome));
    }

    private sealed class HandlerLease : IDisposable
    {
        private readonly CommandPolicyCoordinator _owner;
        private readonly Func<PolicyApprovalRequest, CancellationToken, Task<PolicyApprovalResponse>> _handler;

        /// <summary>Initializes a new instance of the <see cref="HandlerLease"/> class.</summary>
        internal HandlerLease(CommandPolicyCoordinator owner, Func<PolicyApprovalRequest, CancellationToken, Task<PolicyApprovalResponse>> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            lock (_owner._gate)
            {
                if (_owner._handler == _handler)
                {
                    _owner._handler = null;
                    _owner._completionHandler = null;
                }
            }
        }
    }
}
